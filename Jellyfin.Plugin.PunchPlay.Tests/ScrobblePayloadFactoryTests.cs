using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.PunchPlay.Tests;

public class ScrobblePayloadFactoryTests
{
    private readonly ScrobblePayloadFactory _factory = new(NullLogger<ScrobblePayloadFactory>.Instance);

    [Fact]
    public void Create_MoviePayloadUsesTmdbMetadata()
    {
        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Arrival",
            ProductionYear = 2016,
            RunTimeTicks = TimeSpan.FromMinutes(116).Ticks
        };
        movie.ProviderIds["Tmdb"] = "329865";

        var session = CreateSession(Guid.NewGuid());
        var args = CreatePlaybackEvent(movie, session, positionSeconds: 58 * 60);
        var createdAt = new DateTimeOffset(2026, 5, 25, 16, 0, 0, TimeSpan.Zero);

        var payload = Assert.IsType<ScrobblePayload>(
            _factory.Create(args, "start", session.UserId.ToString(), "server-123", "Jellyfin (Home)", "2.0.5.0", eventCreatedAt: createdAt));
        Assert.Equal("movie", payload.MediaType);
        Assert.Equal("Arrival", payload.Title);
        Assert.Equal(2016, payload.Year);
        Assert.Equal(329865, payload.TmdbId);
        Assert.Equal(58 * 60, payload.PositionSeconds);
        Assert.Equal(116 * 60, payload.DurationSeconds);
        Assert.Equal(0.5d, payload.Progress);
        Assert.Equal(Math.Round((58d * 60d) / (116d * 60d) * 100d, 2), payload.ProgressPercent);
        Assert.Equal("server-123", payload.DeviceId);
        Assert.Equal("Living Room Web", payload.DeviceName);
        Assert.Equal($"{session.UserId}:session:session-1:{movie.Id}", payload.PlaybackSessionId);
        Assert.Equal(createdAt.ToUnixTimeMilliseconds(), payload.EventCreatedAt);
        Assert.Equal($"{payload.PlaybackSessionId}:start:{createdAt.ToUnixTimeMilliseconds()}:{58 * 60}", payload.EventId);
        Assert.Equal("Jellyfin (Home)", payload.ServerName);
        Assert.Equal(session.UserId.ToString(), payload.JellyfinUserId);
    }

    [Fact]
    public void Create_MoviePayloadUsesImdbFallbackWhenTmdbMissing()
    {
        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Primer",
            ProductionYear = 2004,
            RunTimeTicks = TimeSpan.FromMinutes(77).Ticks
        };
        movie.ProviderIds["Imdb"] = "tt0390384";

        var session = CreateSession(Guid.NewGuid());
        var args = CreatePlaybackEvent(movie, session, positionSeconds: 10 * 60);

        var payload = Assert.IsType<ScrobblePayload>(
            _factory.Create(args, "progress", session.UserId.ToString(), "server-123", "Jellyfin (Home)", "2.0.2.0"));
        Assert.Null(payload.TmdbId);
        Assert.Equal("tt0390384", payload.ImdbId);
        Assert.Null(payload.Watched);
    }

    [Fact]
    public void Create_EpisodePayloadUsesSeriesTmdbMetadata()
    {
        var series = new Series
        {
            Id = Guid.NewGuid(),
            Name = "Severance",
            ProductionYear = 2022
        };
        series.ProviderIds["Tmdb"] = "95396";
        series.ProviderIds["Imdb"] = "tt11280740";
        series.ProviderIds["Tvdb"] = "371980";

        var libraryManager = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
        libraryManager.Setup(manager => manager.GetItemById(series.Id)).Returns(series);
        BaseItem.LibraryManager = libraryManager.Object;

        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            SeriesId = series.Id,
            Name = "In Perpetuity",
            ParentIndexNumber = 1,
            IndexNumber = 3,
            RunTimeTicks = TimeSpan.FromMinutes(57).Ticks
        };

        var session = CreateSession(Guid.NewGuid());
        var args = CreatePlaybackEvent(episode, session, positionSeconds: 15 * 60);

        var payload = Assert.IsType<ScrobblePayload>(
            _factory.Create(args, "progress", session.UserId.ToString(), "server-123", "Jellyfin (Home)", "2.0.2.0"));
        Assert.Equal("episode", payload.MediaType);
        Assert.Equal("Severance", payload.Title);
        Assert.Equal(2022, payload.Year);
        Assert.Equal(95396, payload.TmdbId);
        Assert.Equal("tt11280740", payload.ImdbId);
        Assert.Equal(371980, payload.TvdbId);
        Assert.Equal("In Perpetuity", payload.EpisodeTitle);
        Assert.Equal(1, payload.Season);
        Assert.Equal(3, payload.Episode);
    }

    [Fact]
    public void Create_EpisodeReturnsNullWhenSeasonOrEpisodeNumberIsMissing()
    {
        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            SeriesName = "Silo",
            IndexNumber = 4
        };
        episode.ProviderIds["Imdb"] = "tt14688458";

        var session = CreateSession(Guid.NewGuid());
        var args = CreatePlaybackEvent(episode, session, positionSeconds: 300);

        var payload = _factory.Create(args, "progress", session.UserId.ToString(), "server-123", "Jellyfin (Home)", "2.0.2.0");

        Assert.Null(payload);
    }

    [Fact]
    public void Create_StopBelowThresholdDoesNotMarkWatched()
    {
        var movie = CreateMovie(runtimeMinutes: 100);
        var session = CreateSession(Guid.NewGuid());
        var args = CreatePlaybackEvent(movie, session, positionSeconds: 84 * 60);

        var payload = Assert.IsType<ScrobblePayload>(
            _factory.Create(args, "stop", session.UserId.ToString(), "server-123", "Jellyfin (Home)", "2.0.2.0"));
        Assert.Equal(84d, payload.ProgressPercent);
        Assert.Null(payload.Watched);
        Assert.Null(payload.WatchedThreshold);
    }

    [Fact]
    public void Create_StopAtThresholdMarksWatched()
    {
        var movie = CreateMovie(runtimeMinutes: 100);
        var session = CreateSession(Guid.NewGuid());
        var args = CreatePlaybackEvent(movie, session, positionSeconds: 85 * 60);

        var payload = Assert.IsType<ScrobblePayload>(
            _factory.Create(args, "stop", session.UserId.ToString(), "server-123", "Jellyfin (Home)", "2.0.2.0"));
        Assert.True(payload.Watched is true);
        Assert.Equal(0.85d, payload.WatchedThreshold);
    }

    [Fact]
    public void Create_StopAboveThresholdMarksWatched()
    {
        var movie = CreateMovie(runtimeMinutes: 100);
        var session = CreateSession(Guid.NewGuid());
        var args = CreatePlaybackEvent(movie, session, positionSeconds: 90 * 60);

        var payload = Assert.IsType<ScrobblePayload>(
            _factory.Create(args, "stop", session.UserId.ToString(), "server-123", "Jellyfin (Home)", "2.0.2.0"));
        Assert.True(payload.Watched is true);
        Assert.Equal(0.85d, payload.WatchedThreshold);
    }

    [Fact]
    public void Create_ProgressAboveThresholdDoesNotMarkWatched()
    {
        var movie = CreateMovie(runtimeMinutes: 100);
        var session = CreateSession(Guid.NewGuid());
        var args = CreatePlaybackEvent(movie, session, positionSeconds: 95 * 60);

        var payload = Assert.IsType<ScrobblePayload>(
            _factory.Create(args, "progress", session.UserId.ToString(), "server-123", "Jellyfin (Home)", "2.0.2.0"));
        Assert.Null(payload.Watched);
        Assert.Null(payload.WatchedThreshold);
    }

    [Fact]
    public void Create_StopPlayedToCompletionMarksWatched()
    {
        var movie = CreateMovie(runtimeMinutes: 100);
        var session = CreateSession(Guid.NewGuid());
        var args = CreatePlaybackEvent(movie, session, positionSeconds: 5 * 60);

        var payload = Assert.IsType<ScrobblePayload>(
            _factory.Create(args, "stop", session.UserId.ToString(), "server-123", "Jellyfin (Home)", "2.0.2.0", playedToCompletion: true));
        Assert.True(payload.Watched is true);
        Assert.Equal(0.85d, payload.WatchedThreshold);
    }

    [Fact]
    public void BuildSessionKey_PrefersJellyfinSessionId()
    {
        var session = CreateSession(Guid.NewGuid(), sessionId: "play-session-123", deviceId: "device-123");
        var movie = CreateMovie(runtimeMinutes: 100);
        var args = CreatePlaybackEvent(movie, session, positionSeconds: 120);

        var sessionKey = ScrobblePayloadFactory.BuildSessionKey(args);

        Assert.Contains(":session:play-session-123:", sessionKey, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSessionKey_FallsBackToDeviceIdWhenSessionIdMissing()
    {
        var session = CreateSession(Guid.NewGuid(), sessionId: null!, deviceId: "device-123");
        var movie = CreateMovie(runtimeMinutes: 100);
        var args = CreatePlaybackEvent(movie, session, positionSeconds: 120);

        var sessionKey = ScrobblePayloadFactory.BuildSessionKey(args);

        Assert.Contains(":device:device-123:", sessionKey, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSessionKey_FallsBackToDeviceNameWhenSessionAndDeviceIdsAreMissing()
    {
        var session = CreateSession(Guid.NewGuid(), sessionId: null!, deviceId: null!, deviceName: "Chrome");
        var movie = CreateMovie(runtimeMinutes: 100);
        var args = CreatePlaybackEvent(movie, session, positionSeconds: 120);

        var sessionKey = ScrobblePayloadFactory.BuildSessionKey(args);

        Assert.Contains(":name:Chrome:", sessionKey, StringComparison.Ordinal);
    }

    private static Movie CreateMovie(int runtimeMinutes)
    {
        return new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Test Movie",
            ProductionYear = 2024,
            RunTimeTicks = TimeSpan.FromMinutes(runtimeMinutes).Ticks
        };
    }

    private static SessionInfo CreateSession(Guid userId, string sessionId = "session-1", string deviceId = "device-1", string deviceName = "Living Room Web")
    {
        return new SessionInfo(Mock.Of<ISessionManager>(), NullLogger.Instance)
        {
            Id = sessionId,
            UserId = userId,
            DeviceId = deviceId,
            DeviceName = deviceName,
            Client = "Jellyfin Web"
        };
    }

    private static PlaybackProgressEventArgs CreatePlaybackEvent(BaseItem item, SessionInfo session, int positionSeconds)
    {
        return new PlaybackProgressEventArgs
        {
            Item = item,
            Session = session,
            PlaybackPositionTicks = TimeSpan.FromSeconds(positionSeconds).Ticks,
            DeviceId = session.DeviceId,
            DeviceName = session.DeviceName,
            ClientName = session.Client
        };
    }
}
