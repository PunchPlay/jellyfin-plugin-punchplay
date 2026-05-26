using System.Collections.Concurrent;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PunchPlay;

/// <summary>
/// Subscribes to Jellyfin playback events and scrobbles to PunchPlay.
/// </summary>
public sealed class PlaybackScrobbler : IHostedService, IDisposable
{
    private readonly ISessionManager _sessionManager;
    private readonly ScrobblePayloadFactory _payloadFactory;
    private readonly PunchPlayScrobbleClient _scrobbleClient;
    private readonly ILogger<PlaybackScrobbler> _logger;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastProgressSentAt = new();
    private readonly ConcurrentDictionary<string, SessionState> _sessionStates = new();

    public PlaybackScrobbler(
        ISessionManager sessionManager,
        ScrobblePayloadFactory payloadFactory,
        PunchPlayScrobbleClient scrobbleClient,
        ILogger<PlaybackScrobbler> logger)
    {
        _sessionManager = sessionManager;
        _payloadFactory = payloadFactory;
        _scrobbleClient = scrobbleClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        return Task.CompletedTask;
    }

    private async void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            var sessionKey = ScrobblePayloadFactory.BuildSessionKey(e);
            var state = _sessionStates.GetOrAdd(sessionKey, _ => new SessionState());
            if (state.StartSent)
                return;

            state.StartSent = true;
            state.IsPaused = false;
            state.LastPausePositionSeconds = null;
            await SendAsync(e, "start").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PunchPlay] Failed to process playback start event");
        }
    }

    private async void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            var sessionKey = ScrobblePayloadFactory.BuildSessionKey(e);
            var state = _sessionStates.GetOrAdd(sessionKey, _ => new SessionState());

            if (e.IsPaused)
            {
                var pausePositionSeconds = ToSeconds(e.PlaybackPositionTicks);
                if (state.IsPaused && state.LastPausePositionSeconds == pausePositionSeconds)
                    return;

                state.IsPaused = true;
                state.LastPausePositionSeconds = pausePositionSeconds;
                await SendAsync(e, "pause").ConfigureAwait(false);
                return;
            }

            var plugin = Plugin.Instance;
            if (plugin is null)
                return;

            var now = DateTimeOffset.UtcNow;
            if (state.IsPaused)
            {
                state.IsPaused = false;
                state.LastPausePositionSeconds = null;
                _lastProgressSentAt[sessionKey] = now;
                await SendAsync(e, "resume").ConfigureAwait(false);
                return;
            }

            state.IsPaused = false;
            state.LastPausePositionSeconds = null;

            var intervalSeconds = Math.Max(10, plugin.Configuration.ProgressIntervalSeconds);
            if (_lastProgressSentAt.TryGetValue(sessionKey, out var lastSent) &&
                (now - lastSent).TotalSeconds < intervalSeconds)
            {
                return;
            }

            _lastProgressSentAt[sessionKey] = now;
            await SendAsync(e, "progress").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PunchPlay] Failed to process playback progress event");
        }
    }

    private async void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            await SendAsync(e, "stop", e.PlayedToCompletion).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PunchPlay] Failed to process playback stop event");
        }
        finally
        {
            var sessionKey = ScrobblePayloadFactory.BuildSessionKey(e);
            _lastProgressSentAt.TryRemove(sessionKey, out _);
            _sessionStates.TryRemove(sessionKey, out _);
        }
    }

    private async Task SendAsync(PlaybackProgressEventArgs e, string action, bool playedToCompletion = false)
    {
        var plugin = Plugin.Instance;
        if (plugin is null || e.Item is null)
            return;

        var jellyfinUserId = e.Session.UserId.ToString();
        var accessToken = plugin.GetUserToken(jellyfinUserId);
        if (string.IsNullOrWhiteSpace(accessToken))
            return;

        var payload = _payloadFactory.Create(
            e,
            action,
            jellyfinUserId,
            plugin.EnsureServerId(),
            plugin.FriendlyServerName,
            plugin.ClientVersion,
            playedToCompletion);
        if (payload is null)
            return;

        await _scrobbleClient.DispatchAsync(action, jellyfinUserId, accessToken, payload, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static int ToSeconds(long? ticks) =>
        ticks.HasValue && ticks.Value > 0
            ? (int)(ticks.Value / 10_000_000)
            : 0;

    /// <inheritdoc />
    public void Dispose()
    {
        _lastProgressSentAt.Clear();
        _sessionStates.Clear();
    }

    private sealed class SessionState
    {
        public bool StartSent { get; set; }

        public bool IsPaused { get; set; }

        public int? LastPausePositionSeconds { get; set; }
    }
}
