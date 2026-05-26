namespace Jellyfin.Plugin.PunchPlay;

/// <summary>
/// Tracks runtime diagnostics for the PunchPlay plugin.
/// </summary>
public class PluginDiagnosticsService
{
    private readonly object _sync = new();
    private DateTimeOffset? _lastSuccessfulScrobbleAtUtc;
    private DateTimeOffset? _lastFailedScrobbleAtUtc;
    private string? _lastError;
    private int _queuedScrobbleCount;
    private DateTimeOffset? _oldestQueuedScrobbleAtUtc;
    private DateTimeOffset? _nextQueuedRetryAtUtc;
    private int _highestQueuedRetryCount;

    public DateTimeOffset? LastSuccessfulScrobbleAtUtc
    {
        get
        {
            lock (_sync)
                return _lastSuccessfulScrobbleAtUtc;
        }
    }

    public DateTimeOffset? LastFailedScrobbleAtUtc
    {
        get
        {
            lock (_sync)
                return _lastFailedScrobbleAtUtc;
        }
    }

    public string? LastError
    {
        get
        {
            lock (_sync)
                return _lastError;
        }
    }

    public int QueuedScrobbleCount
    {
        get
        {
            lock (_sync)
                return _queuedScrobbleCount;
        }
    }

    public DateTimeOffset? OldestQueuedScrobbleAtUtc
    {
        get
        {
            lock (_sync)
                return _oldestQueuedScrobbleAtUtc;
        }
    }

    public DateTimeOffset? NextQueuedRetryAtUtc
    {
        get
        {
            lock (_sync)
                return _nextQueuedRetryAtUtc;
        }
    }

    public int HighestQueuedRetryCount
    {
        get
        {
            lock (_sync)
                return _highestQueuedRetryCount;
        }
    }

    public void MarkSuccess()
    {
        lock (_sync)
        {
            _lastSuccessfulScrobbleAtUtc = DateTimeOffset.UtcNow;
            _lastError = null;
        }
    }

    public void MarkFailure(string? error)
    {
        lock (_sync)
        {
            _lastFailedScrobbleAtUtc = DateTimeOffset.UtcNow;
            _lastError = error;
        }
    }

    public void SetQueuedScrobbleCount(int count)
    {
        lock (_sync)
            _queuedScrobbleCount = Math.Max(0, count);
    }

    public void SetQueueSnapshot(int count, DateTimeOffset? oldestQueuedScrobbleAtUtc, DateTimeOffset? nextQueuedRetryAtUtc, int highestQueuedRetryCount)
    {
        lock (_sync)
        {
            _queuedScrobbleCount = Math.Max(0, count);
            _oldestQueuedScrobbleAtUtc = oldestQueuedScrobbleAtUtc;
            _nextQueuedRetryAtUtc = nextQueuedRetryAtUtc;
            _highestQueuedRetryCount = Math.Max(0, highestQueuedRetryCount);
        }
    }
}
