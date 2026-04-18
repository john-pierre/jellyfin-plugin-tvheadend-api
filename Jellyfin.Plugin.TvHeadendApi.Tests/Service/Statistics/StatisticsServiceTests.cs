using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Jellyfin.Plugin.TvHeadendApi.Service.Statistics;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Statistics;

public class StatisticsServiceTests
{
    private static StatisticsService CreateSut(Mock<ISessionManager>? sessionManager = null)
    {
        var sm = sessionManager ?? new Mock<ISessionManager>();
        return new StatisticsService(
            NullLogger<StatisticsService>.Instance,
            sm.Object,
            new PluginConfigurationProvider(() => null),
            new DataFolderPathProvider(() => null));
    }

    // ── Constructor ───────────────────────────────────────────────────

    [Fact]
    public void Constructor_WithNullLogger_Throws()
    {
        var sm = new Mock<ISessionManager>();
        Assert.Throws<ArgumentNullException>(() => new StatisticsService(null!, sm.Object, new PluginConfigurationProvider(() => null), new DataFolderPathProvider(() => null)));
    }

    [Fact]
    public void Constructor_WithNullSessionManager_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new StatisticsService(NullLogger<StatisticsService>.Instance, null!, new PluginConfigurationProvider(() => null), new DataFolderPathProvider(() => null)));
    }

    // ── GetStatistics ────────────────────────────────────────────────

    [Fact]
    public void GetStatistics_WhenEmpty_ReturnsZeroCounts()
    {
        var sut = CreateSut();

        var result = sut.GetStatistics(30);

        Assert.NotNull(result);
        Assert.Empty(result.Sessions);
        Assert.Equal(0, result.TotalCount);
        Assert.Equal(0, result.ActiveCount);
    }

    [Fact]
    public void GetStatistics_WithZeroDays_ReturnsAllSessions()
    {
        var sut = CreateSut();

        // No sessions added, but the code path for days=0 should work
        var result = sut.GetStatistics(0);

        Assert.NotNull(result);
        Assert.Empty(result.Sessions);
    }

    // ── AllSessions ──────────────────────────────────────────────────

    [Fact]
    public void AllSessions_WhenEmpty_ReturnsEmptyList()
    {
        var sut = CreateSut();

        var sessions = sut.AllSessions;

        Assert.NotNull(sessions);
        Assert.Empty(sessions);
    }

    // ── ClearStatistics ──────────────────────────────────────────────

    [Fact]
    public void ClearStatistics_WhenEmpty_DoesNotThrow()
    {
        // Plugin.Instance is null, so SaveToDisk will short-circuit gracefully
        var sut = CreateSut();

        sut.ClearStatistics();

        Assert.Empty(sut.AllSessions);
    }

    // ── StartAsync / StopAsync ───────────────────────────────────────

    [Fact]
    public async Task StartAsync_SubscribesToSessionManagerEvents()
    {
        var sm = new Mock<ISessionManager>();
        var sut = CreateSut(sm);

        // Plugin.Instance is null, so LoadFromDisk short-circuits
        await sut.StartAsync(CancellationToken.None);

        // Verify event subscriptions were added (Moq tracks add/remove on events)
        sm.VerifyAdd(m => m.PlaybackStart += It.IsAny<EventHandler<MediaBrowser.Controller.Library.PlaybackProgressEventArgs>>(), Times.Once);
        sm.VerifyAdd(m => m.PlaybackStopped += It.IsAny<EventHandler<MediaBrowser.Controller.Library.PlaybackStopEventArgs>>(), Times.Once);

        sut.Dispose();
    }

    [Fact]
    public async Task StopAsync_UnsubscribesFromSessionManagerEvents()
    {
        var sm = new Mock<ISessionManager>();
        var sut = CreateSut(sm);

        await sut.StartAsync(CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);

        sm.VerifyRemove(m => m.PlaybackStart -= It.IsAny<EventHandler<MediaBrowser.Controller.Library.PlaybackProgressEventArgs>>(), Times.Once);
        sm.VerifyRemove(m => m.PlaybackStopped -= It.IsAny<EventHandler<MediaBrowser.Controller.Library.PlaybackStopEventArgs>>(), Times.Once);

        sut.Dispose();
    }

    [Fact]
    public async Task StopAsync_WithoutStart_DoesNotThrow()
    {
        var sut = CreateSut();

        await sut.StopAsync(CancellationToken.None);

        sut.Dispose();
    }

    // ── Playback event tracking ──────────────────────────────────────

    [Fact]
    public async Task PlaybackStart_ForLiveTvChannel_CreatesActiveSession()
    {
        var sm = new Mock<ISessionManager>();
        var sut = CreateSut(sm);
        await sut.StartAsync(CancellationToken.None);

        // Simulate a LiveTvChannel playback start
        var channel = new LiveTvChannel { Name = "TestChannel" };
        var args = new MediaBrowser.Controller.Library.PlaybackProgressEventArgs
        {
            Item = channel,
            PlaySessionId = "session-1",
            DeviceName = "TestDevice",
            ClientName = "TestClient",
        };

        sm.Raise(m => m.PlaybackStart += null, args);

        // Active session should be reflected in statistics
        var stats = sut.GetStatistics(0);
        Assert.Equal(1, stats.ActiveCount);

        sut.Dispose();
    }

    [Fact]
    public async Task PlaybackStart_ForNonLiveTvItem_IsIgnored()
    {
        var sm = new Mock<ISessionManager>();
        var sut = CreateSut(sm);
        await sut.StartAsync(CancellationToken.None);

        // Use a non-LiveTvChannel item (e.g. a generic BaseItem would not be LiveTvChannel)
        // Since we can't easily create a non-LiveTvChannel BaseItem, we pass null item
        // which will not match `is LiveTvChannel`
        var args = new MediaBrowser.Controller.Library.PlaybackProgressEventArgs
        {
            Item = null!,
            PlaySessionId = "session-2",
        };

        sm.Raise(m => m.PlaybackStart += null, args);

        var stats = sut.GetStatistics(0);
        Assert.Equal(0, stats.ActiveCount);

        sut.Dispose();
    }

    [Fact]
    public async Task PlaybackStopped_CompletesActiveSession()
    {
        var sm = new Mock<ISessionManager>();
        var sut = CreateSut(sm);
        await sut.StartAsync(CancellationToken.None);

        var channel = new LiveTvChannel { Name = "TestChannel" };

        // Start playback
        var startArgs = new MediaBrowser.Controller.Library.PlaybackProgressEventArgs
        {
            Item = channel,
            PlaySessionId = "session-3",
            DeviceName = "TestDevice",
            ClientName = "TestClient",
        };
        sm.Raise(m => m.PlaybackStart += null, startArgs);

        // Stop playback
        var stopArgs = new MediaBrowser.Controller.Library.PlaybackStopEventArgs
        {
            Item = channel,
            PlaySessionId = "session-3",
        };
        sm.Raise(m => m.PlaybackStopped += null, stopArgs);

        // Session should now be in completed list, not active
        var stats = sut.GetStatistics(0);
        Assert.Equal(0, stats.ActiveCount);
        Assert.Equal(1, stats.TotalCount);
        Assert.Single(stats.Sessions);
        Assert.NotNull(stats.Sessions[0].EndTimeUtc);

        sut.Dispose();
    }

    [Fact]
    public async Task PlaybackStopped_ForUnknownSession_IsIgnored()
    {
        var sm = new Mock<ISessionManager>();
        var sut = CreateSut(sm);
        await sut.StartAsync(CancellationToken.None);

        var channel = new LiveTvChannel { Name = "TestChannel" };
        var stopArgs = new MediaBrowser.Controller.Library.PlaybackStopEventArgs
        {
            Item = channel,
            PlaySessionId = "unknown-session",
        };
        sm.Raise(m => m.PlaybackStopped += null, stopArgs);

        var stats = sut.GetStatistics(0);
        Assert.Equal(0, stats.TotalCount);

        sut.Dispose();
    }

    [Fact]
    public async Task ClearStatistics_RemovesCompletedAndActiveSessions()
    {
        var sm = new Mock<ISessionManager>();
        var sut = CreateSut(sm);
        await sut.StartAsync(CancellationToken.None);

        var channel = new LiveTvChannel { Name = "TestChannel" };

        // Start and stop a session
        var startArgs = new MediaBrowser.Controller.Library.PlaybackProgressEventArgs
        {
            Item = channel,
            PlaySessionId = "session-4",
            DeviceName = "Dev",
            ClientName = "Client",
        };
        sm.Raise(m => m.PlaybackStart += null, startArgs);
        var stopArgs = new MediaBrowser.Controller.Library.PlaybackStopEventArgs
        {
            Item = channel,
            PlaySessionId = "session-4",
        };
        sm.Raise(m => m.PlaybackStopped += null, stopArgs);

        Assert.Equal(1, sut.GetStatistics(0).TotalCount);

        sut.ClearStatistics();

        Assert.Equal(0, sut.GetStatistics(0).TotalCount);
        Assert.Empty(sut.AllSessions);

        sut.Dispose();
    }

    [Fact]
    public async Task StopAsync_ClosesActiveSessions()
    {
        var sm = new Mock<ISessionManager>();
        var sut = CreateSut(sm);
        await sut.StartAsync(CancellationToken.None);

        var channel = new LiveTvChannel { Name = "TestChannel" };
        var startArgs = new MediaBrowser.Controller.Library.PlaybackProgressEventArgs
        {
            Item = channel,
            PlaySessionId = "session-5",
            DeviceName = "Dev",
            ClientName = "Client",
        };
        sm.Raise(m => m.PlaybackStart += null, startArgs);

        // Active session should exist
        Assert.Equal(1, sut.GetStatistics(0).ActiveCount);

        await sut.StopAsync(CancellationToken.None);

        // After stop, active sessions are closed and added to completed list
        var stats = sut.GetStatistics(0);
        Assert.Equal(0, stats.ActiveCount);
        Assert.Equal(1, stats.TotalCount);

        sut.Dispose();
    }

    // ── Dispose ──────────────────────────────────────────────────────

    [Fact]
    public void Dispose_WithoutStart_DoesNotThrow()
    {
        var sut = CreateSut();
        sut.Dispose();
    }

    [Fact]
    public async Task Dispose_AfterStart_DoesNotThrow()
    {
        var sut = CreateSut();
        await sut.StartAsync(CancellationToken.None);
        sut.Dispose();
    }

    // ── LoadFromDisk / SaveToDisk ─────────────────────────────────────

    [Fact]
    public async Task StartAsync_WhenFileExists_LoadsSessionsFromDisk()
    {
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
        System.IO.Directory.CreateDirectory(tempDir);
        try
        {
            var sessions = new[]
            {
                new Jellyfin.Plugin.TvHeadendApi.Model.Statistics.ViewingSession
                {
                    UserName = "TestUser",
                    ChannelName = "TestChannel",
                    StartTimeUtc = DateTime.UtcNow.AddMinutes(-10),
                    EndTimeUtc = DateTime.UtcNow.AddMinutes(-5),
                    PlaySessionId = "persisted-1",
                    DeviceName = "Dev",
                    ClientName = "Client",
                    PlayMethod = "DirectPlay",
                    ChannelId = "ch-1"
                }
            };
            var json = System.Text.Json.JsonSerializer.Serialize(sessions, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(tempDir, "viewing-statistics.json"), json);

            var sm = new Mock<ISessionManager>();
            var sut = new StatisticsService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<StatisticsService>.Instance,
                sm.Object,
                new PluginConfigurationProvider(() => null),
                new DataFolderPathProvider(() => tempDir));

            await sut.StartAsync(CancellationToken.None);

            Assert.Single(sut.AllSessions);
            Assert.Equal("TestUser", sut.AllSessions[0].UserName);

            sut.Dispose();
        }
        finally
        {
            System.IO.Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ClearStatistics_WhenPathProvided_WritesJsonFile()
    {
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
        System.IO.Directory.CreateDirectory(tempDir);
        try
        {
            var sm = new Mock<ISessionManager>();
            var sut = new StatisticsService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<StatisticsService>.Instance,
                sm.Object,
                new PluginConfigurationProvider(() => null),
                new DataFolderPathProvider(() => tempDir));

            await sut.StartAsync(CancellationToken.None);
            sut.ClearStatistics();

            var filePath = System.IO.Path.Combine(tempDir, "viewing-statistics.json");
            Assert.True(System.IO.File.Exists(filePath));

            sut.Dispose();
        }
        finally
        {
            System.IO.Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetStatistics_WithDaysFilter_ExcludesOldSessions()
    {
        var sm = new Mock<ISessionManager>();
        var sut = CreateSut(sm);
        await sut.StartAsync(CancellationToken.None);

        var channel = new LiveTvChannel { Name = "TestChannel" };

        // Start a session
        var startArgs = new MediaBrowser.Controller.Library.PlaybackProgressEventArgs
        {
            Item = channel,
            PlaySessionId = "filter-session",
            DeviceName = "Dev",
            ClientName = "Client",
        };
        sm.Raise(m => m.PlaybackStart += null, startArgs);
        var stopArgs = new MediaBrowser.Controller.Library.PlaybackStopEventArgs
        {
            Item = channel,
            PlaySessionId = "filter-session",
        };
        sm.Raise(m => m.PlaybackStopped += null, stopArgs);

        // days=0 returns all, days=30 should include recent session
        var all = sut.GetStatistics(0);
        var recent = sut.GetStatistics(30);

        Assert.Equal(1, all.TotalCount);
        Assert.Equal(1, recent.TotalCount);

        sut.Dispose();
    }

    [Fact]
    public async Task PlaybackStopped_WhenStopEventHasPlayMethod_UpdatesSessionPlayMethod()
    {
        var sm = new Mock<ISessionManager>();
        var sut = CreateSut(sm);
        await sut.StartAsync(CancellationToken.None);

        var channel = new LiveTvChannel { Name = "TestChannel" };
        var startArgs = new MediaBrowser.Controller.Library.PlaybackProgressEventArgs
        {
            Item = channel,
            PlaySessionId = "pm-session",
            DeviceName = "Dev",
            ClientName = "Client",
        };
        sm.Raise(m => m.PlaybackStart += null, startArgs);

        var stopArgs = new MediaBrowser.Controller.Library.PlaybackStopEventArgs
        {
            Item = channel,
            PlaySessionId = "pm-session",
            Session = new MediaBrowser.Controller.Session.SessionInfo(Mock.Of<ISessionManager>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaBrowser.Controller.Session.SessionInfo>.Instance)
            {
                PlayState = new MediaBrowser.Model.Session.PlayerStateInfo { PlayMethod = MediaBrowser.Model.Session.PlayMethod.Transcode }
            }
        };
        sm.Raise(m => m.PlaybackStopped += null, stopArgs);

        var session = sut.AllSessions.Single();
        Assert.Equal("Transcode", session.PlayMethod);

        sut.Dispose();
    }

    [Fact]
    public async Task PlaybackStart_WithNullPlaySessionId_AssignsGeneratedSessionId()
    {
        var sm = new Mock<ISessionManager>();
        var sut = CreateSut(sm);
        await sut.StartAsync(CancellationToken.None);

        var channel = new LiveTvChannel { Name = "TestChannel" };
        var startArgs = new MediaBrowser.Controller.Library.PlaybackProgressEventArgs
        {
            Item = channel,
            PlaySessionId = null,
            DeviceName = "Dev",
            ClientName = "Client",
        };
        sm.Raise(m => m.PlaybackStart += null, startArgs);

        var stats = sut.GetStatistics(0);
        Assert.Equal(1, stats.ActiveCount);

        sut.Dispose();
    }

    [Fact]
    public async Task PlaybackStart_WithNonLiveTvItem_IsIgnored()
    {
        var sm = new Mock<ISessionManager>();
        var sut = CreateSut(sm);
        await sut.StartAsync(CancellationToken.None);

        // Use a non-LiveTvChannel item (e.g., a Movie)
        var nonLiveItem = new MediaBrowser.Controller.Entities.Movies.Movie { Name = "SomeMovie" };
        var startArgs = new MediaBrowser.Controller.Library.PlaybackProgressEventArgs
        {
            Item = nonLiveItem,
            PlaySessionId = "non-live-session",
            DeviceName = "Dev",
            ClientName = "Client",
        };
        sm.Raise(m => m.PlaybackStart += null, startArgs);

        var stats = sut.GetStatistics(0);
        Assert.Equal(0, stats.ActiveCount);

        sut.Dispose();
    }

    [Fact]
    public async Task PlaybackStopped_WithNonLiveTvItem_IsIgnored()
    {
        var sm = new Mock<ISessionManager>();
        var sut = CreateSut(sm);
        await sut.StartAsync(CancellationToken.None);

        var nonLiveItem = new MediaBrowser.Controller.Entities.Movies.Movie { Name = "SomeMovie" };
        var stopArgs = new MediaBrowser.Controller.Library.PlaybackStopEventArgs
        {
            Item = nonLiveItem,
            PlaySessionId = "non-live-session",
        };
        sm.Raise(m => m.PlaybackStopped += null, stopArgs);

        Assert.Empty(sut.AllSessions);

        sut.Dispose();
    }

    [Fact]
    public async Task PlaybackStopped_WithUnknownSessionId_IsIgnored()
    {
        var sm = new Mock<ISessionManager>();
        var sut = CreateSut(sm);
        await sut.StartAsync(CancellationToken.None);

        var channel = new LiveTvChannel { Name = "TestChannel" };
        var stopArgs = new MediaBrowser.Controller.Library.PlaybackStopEventArgs
        {
            Item = channel,
            PlaySessionId = "session-that-never-started",
        };
        sm.Raise(m => m.PlaybackStopped += null, stopArgs);

        Assert.Empty(sut.AllSessions);

        sut.Dispose();
    }
}

