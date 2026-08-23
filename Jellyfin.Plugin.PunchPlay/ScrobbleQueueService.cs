using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PunchPlay;

/// <summary>
/// Persists transient scrobble failures and retries them with backoff.
/// </summary>
public class ScrobbleQueueService : BackgroundService
{
    private const int MaxQueueSize = 500;
    private const int MaxRetryAttempts = 10;
    private static readonly TimeSpan MaxQueueAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan RetryLoopInterval = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly PunchPlayTransport _transport;
    private readonly PunchPlayAuthService _authService;
    private readonly PluginDiagnosticsService _diagnostics;
    private readonly ILogger<ScrobbleQueueService> _logger;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly SemaphoreSlim _flushSync = new(1, 1);
    private readonly SemaphoreSlim _flushSignal = new(0, 1);
    private readonly List<QueuedScrobbleEntry> _entries = [];
    private bool _loaded;
    private int _flushRequested;

    public ScrobbleQueueService(
        PunchPlayTransport transport,
        PunchPlayAuthService authService,
        PluginDiagnosticsService diagnostics,
        ILogger<ScrobbleQueueService> logger)
    {
        _transport = transport;
        _authService = authService;
        _diagnostics = diagnostics;
        _logger = logger;
    }

    /// <summary>
    /// Adds a scrobble to the retry queue.
    /// </summary>
    public async Task EnqueueAsync(string action, string jellyfinUserId, ScrobblePayload payload, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

        await _sync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PruneExpiredEntries(DateTimeOffset.UtcNow);

            _entries.Add(new QueuedScrobbleEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Action = action,
                JellyfinUserId = jellyfinUserId,
                Payload = payload,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                NextAttemptAtUtc = DateTimeOffset.UtcNow.AddMinutes(1),
                RetryCount = 0
            });

            while (_entries.Count > MaxQueueSize)
                _entries.RemoveAt(0);

            await PersistLockedAsync(ct).ConfigureAwait(false);
            UpdateQueueSnapshotLocked();
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>
    /// Clears all queued scrobbles.
    /// </summary>
    public async Task ClearAsync(CancellationToken ct)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

        await _sync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _entries.Clear();
            await PersistLockedAsync(ct).ConfigureAwait(false);
            UpdateQueueSnapshotLocked();
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>
    /// Clears all queued scrobbles for the specified Jellyfin user.
    /// </summary>
    public async Task ClearUserAsync(string jellyfinUserId, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

        await _sync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _entries.RemoveAll(entry => string.Equals(entry.JellyfinUserId, jellyfinUserId, StringComparison.OrdinalIgnoreCase));
            await PersistLockedAsync(ct).ConfigureAwait(false);
            UpdateQueueSnapshotLocked();
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <summary>
    /// Requests that the background retry loop wake up and process due entries soon.
    /// </summary>
    public void RequestFlush()
    {
        if (Interlocked.Exchange(ref _flushRequested, 1) != 0)
            return;

        try
        {
            _flushSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already pending.
        }
    }

    /// <summary>
    /// Runs a retry pass immediately.
    /// </summary>
    public Task RetryNowAsync(CancellationToken ct) => FlushEntriesAsync(force: true, ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await EnsureLoadedAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FlushEntriesAsync(force: false, stoppingToken).ConfigureAwait(false);
                await WaitForNextRetryAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PunchPlay] Queue retry loop failed");
            }
        }
    }

    private async Task WaitForNextRetryAsync(CancellationToken ct)
    {
        var signaled = await _flushSignal.WaitAsync(RetryLoopInterval, ct).ConfigureAwait(false);
        if (signaled)
        {
            Interlocked.Exchange(ref _flushRequested, 0);
        }
    }

    private async Task FlushEntriesAsync(bool force, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        await _flushSync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await FlushEntriesCoreAsync(force, ct).ConfigureAwait(false);
        }
        finally
        {
            _flushSync.Release();
        }
    }

    private async Task FlushEntriesCoreAsync(bool force, CancellationToken ct)
    {
        List<QueuedScrobbleEntry> dueEntries;
        var prunedExpiredEntries = false;
        await _sync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var countBeforePrune = _entries.Count;
            PruneExpiredEntries(now);
            prunedExpiredEntries = _entries.Count != countBeforePrune;
            dueEntries = _entries
                .Where(entry => force || entry.NextAttemptAtUtc <= now)
                .Select(entry => entry.Clone())
                .ToList();
        }
        finally
        {
            _sync.Release();
        }

        if (dueEntries.Count == 0)
        {
            if (prunedExpiredEntries)
            {
                await _sync.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await PersistLockedAsync(ct).ConfigureAwait(false);
                    UpdateQueueSnapshotLocked();
                }
                finally
                {
                    _sync.Release();
                }
            }

            return;
        }

        var plugin = Plugin.Instance;
        if (plugin is null)
            return;

        var changed = false;
        foreach (var entry in dueEntries)
        {
            var token = plugin.GetUserToken(entry.JellyfinUserId);
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogInformation("[PunchPlay] Dropping queued scrobble {QueueId} because Jellyfin user {UserId} is no longer linked", entry.Id, entry.JellyfinUserId);
                await RemoveUserEntriesAsync(entry.JellyfinUserId, ct).ConfigureAwait(false);
                changed = true;
                continue;
            }

            var result = await _transport.SendAsync(entry.Action, token, entry.Payload, ct).ConfigureAwait(false);
            if (result.Outcome == PunchPlayTransportOutcome.Unauthorized)
            {
                var refreshResult = await _authService.RefreshAccessTokenAsync(entry.JellyfinUserId, token, ct).ConfigureAwait(false);
                if (refreshResult.Outcome == PunchPlayTokenRefreshOutcome.Refreshed)
                {
                    result = await _transport.SendAsync(entry.Action, refreshResult.AccessToken!, entry.Payload, ct).ConfigureAwait(false);
                }
                else if (refreshResult.Outcome == PunchPlayTokenRefreshOutcome.RetryableFailure)
                {
                    result = PunchPlayTransportResult.RetryableFailure(refreshResult.Message);
                }
            }

            switch (result.Outcome)
            {
                case PunchPlayTransportOutcome.Success:
                    _diagnostics.MarkSuccess();
                    await RemoveEntryAsync(entry.Id, ct).ConfigureAwait(false);
                    changed = true;
                    break;
                case PunchPlayTransportOutcome.Unauthorized:
                    plugin.ClearUserToken(entry.JellyfinUserId);
                    _diagnostics.MarkFailure(result.Message);
                    await RemoveUserEntriesAsync(entry.JellyfinUserId, ct).ConfigureAwait(false);
                    changed = true;
                    break;
                case PunchPlayTransportOutcome.PermanentFailure:
                    _diagnostics.MarkFailure(result.Message);
                    await RemoveEntryAsync(entry.Id, ct).ConfigureAwait(false);
                    changed = true;
                    break;
                case PunchPlayTransportOutcome.RetryableFailure:
                    _diagnostics.MarkFailure(result.Message);
                    await UpdateEntryBackoffAsync(entry.Id, ct).ConfigureAwait(false);
                    changed = true;
                    break;
            }
        }

        if (changed)
        {
            await _sync.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await PersistLockedAsync(ct).ConfigureAwait(false);
                UpdateQueueSnapshotLocked();
            }
            finally
            {
                _sync.Release();
            }
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded)
            return;

        await _sync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded)
                return;

            var plugin = Plugin.Instance;
            if (plugin is null)
                return;

            if (File.Exists(plugin.QueueFilePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(plugin.QueueFilePath, ct).ConfigureAwait(false);
                    var entries = JsonSerializer.Deserialize<List<QueuedScrobbleEntry>>(json, JsonOptions);
                    if (entries is not null)
                        _entries.AddRange(entries);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[PunchPlay] Could not load persisted scrobble queue");
                }
            }

            PruneExpiredEntries(DateTimeOffset.UtcNow);
            _loaded = true;
            await PersistLockedAsync(ct).ConfigureAwait(false);
            UpdateQueueSnapshotLocked();
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task RemoveEntryAsync(string id, CancellationToken ct)
    {
        await _sync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _entries.RemoveAll(entry => entry.Id == id);
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task RemoveUserEntriesAsync(string jellyfinUserId, CancellationToken ct)
    {
        await _sync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _entries.RemoveAll(entry => string.Equals(entry.JellyfinUserId, jellyfinUserId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task UpdateEntryBackoffAsync(string id, CancellationToken ct)
    {
        await _sync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entry = _entries.FirstOrDefault(candidate => candidate.Id == id);
            if (entry is null)
                return;

            var nextRetryCount = entry.RetryCount + 1;
            if (nextRetryCount >= MaxRetryAttempts)
            {
                _logger.LogWarning("[PunchPlay] Dropping queued scrobble {QueueId} for user {UserId} after {RetryCount} retry attempts",
                    entry.Id, entry.JellyfinUserId, nextRetryCount);
                _entries.RemoveAll(candidate => candidate.Id == id);
                return;
            }

            entry.RetryCount = nextRetryCount;
            entry.NextAttemptAtUtc = DateTimeOffset.UtcNow.Add(CalculateBackoff(entry.RetryCount));
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task PersistLockedAsync(CancellationToken ct)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
            return;

        Directory.CreateDirectory(plugin.StateDirectoryPath);
        var json = JsonSerializer.Serialize(_entries, JsonOptions);
        await File.WriteAllTextAsync(plugin.QueueFilePath, json, ct).ConfigureAwait(false);
    }

    private void PruneExpiredEntries(DateTimeOffset now)
    {
        _entries.RemoveAll(entry => now - entry.CreatedAtUtc > MaxQueueAge);
    }

    private void UpdateQueueSnapshotLocked()
    {
        DateTimeOffset? oldestQueuedScrobbleAtUtc = _entries.Count == 0 ? null : _entries.Min(entry => entry.CreatedAtUtc);
        DateTimeOffset? nextQueuedRetryAtUtc = _entries.Count == 0 ? null : _entries.Min(entry => entry.NextAttemptAtUtc);
        var highestQueuedRetryCount = _entries.Count == 0 ? 0 : _entries.Max(entry => entry.RetryCount);
        _diagnostics.SetQueueSnapshot(_entries.Count, oldestQueuedScrobbleAtUtc, nextQueuedRetryAtUtc, highestQueuedRetryCount);
    }

    private static TimeSpan CalculateBackoff(int retryCount)
    {
        var minutes = Math.Min(Math.Pow(2, retryCount), 360d);
        return TimeSpan.FromMinutes(minutes);
    }

    private sealed class QueuedScrobbleEntry
    {
        public string Id { get; set; } = string.Empty;

        public string Action { get; set; } = string.Empty;

        public string JellyfinUserId { get; set; } = string.Empty;

        public ScrobblePayload Payload { get; set; } = new();

        public DateTimeOffset CreatedAtUtc { get; set; }

        public DateTimeOffset NextAttemptAtUtc { get; set; }

        public int RetryCount { get; set; }

        public QueuedScrobbleEntry Clone() =>
            new()
            {
                Id = Id,
                Action = Action,
                JellyfinUserId = JellyfinUserId,
                Payload = Payload,
                CreatedAtUtc = CreatedAtUtc,
                NextAttemptAtUtc = NextAttemptAtUtc,
                RetryCount = RetryCount
            };
    }
}
