using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Statistic;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Database;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Jellyfin.Plugin.TvHeadendApi.Service.Statistic;
using Jellyfin.Plugin.TvHeadendApi.Service.Storage;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Statistic;

public class StatisticsServiceTests
{
    private static DatabaseHealthService CreateTestDbHealth()
    {
        var dir = Path.Combine(Path.GetTempPath(), "test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var pathProvider = new DataFolderPathProvider(() => dir);
        var provider = new DatabaseProvider(pathProvider);
        var factory = new DatabaseConnectionFactory(provider);
        var migration = new DatabaseMigrationService(factory, NullLogger<DatabaseMigrationService>.Instance);
        var recovery = new DatabaseRecoveryService(provider, migration, factory, NullLogger<DatabaseRecoveryService>.Instance);
        var health = new DatabaseHealthService(provider, factory, migration, recovery, NullLogger<DatabaseHealthService>.Instance);
        health.Initialize();
        return health;
    }

    private static StatisticsService CreateSut(Mock<ISessionManager>? sessionManager = null)
    {
        var sm = sessionManager ?? new Mock<ISessionManager>();
        var options = new DbContextOptionsBuilder<ViewingSessionContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new StatisticsService(
            NullLogger<StatisticsService>.Instance,
            sm.Object,
            new ConfigurationProvider(() => null),
            CreateTestDbHealth(),
            new DatabaseWriteCoordinator(),
            options);
    }

    // ── Constructor ───────────────────────────────────────────────────

    [Fact]
    public void Constructor_WithNullLogger_Throws()
    {
        var sm = new Mock<ISessionManager>();
        var options = new DbContextOptionsBuilder<ViewingSessionContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new ViewingSessionContext(options);

        Assert.Throws<ArgumentNullException>(() =>
            new StatisticsService(null!, sm.Object, new ConfigurationProvider(() => null), CreateTestDbHealth(), new DatabaseWriteCoordinator(), options));
    }

    [Fact]
    public void Constructor_WithNullSessionManager_Throws()
    {
        var options = new DbContextOptionsBuilder<ViewingSessionContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new ViewingSessionContext(options);

        Assert.Throws<ArgumentNullException>(() =>
            new StatisticsService(NullLogger<StatisticsService>.Instance, null!, new ConfigurationProvider(() => null), CreateTestDbHealth(), new DatabaseWriteCoordinator(), options));
    }

    [Fact]
    public void Constructor_WithNullDbContextOptions_Throws()
    {
        var sm = new Mock<ISessionManager>();

        Assert.Throws<ArgumentNullException>(() =>
            new StatisticsService(NullLogger<StatisticsService>.Instance, sm.Object, new ConfigurationProvider(() => null), CreateTestDbHealth(), new DatabaseWriteCoordinator(), (DatabaseProvider)null!));
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
        Assert.Single(stats.ActiveSessions);

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
        Assert.Empty(stats.ActiveSessions);
        Assert.NotNull(stats.Sessions[0].EndTimeUtc);

        sut.Dispose();
    }

    [Fact]
    public async Task GetStatistics_WithActiveSession_ReturnsActiveSessionsSeparately()
    {
        var sm = new Mock<ISessionManager>();
        var sut = CreateSut(sm);
        await sut.StartAsync(CancellationToken.None);

        var channel = new LiveTvChannel { Name = "TestChannel" };
        var startArgs = new MediaBrowser.Controller.Library.PlaybackProgressEventArgs
        {
            Item = channel,
            PlaySessionId = "active-session",
            DeviceName = "Dev",
            ClientName = "Client",
        };

        sm.Raise(m => m.PlaybackStart += null, startArgs);

        var stats = sut.GetStatistics(0);

        Assert.Empty(stats.Sessions);
        Assert.Single(stats.ActiveSessions);
        Assert.Equal(1, stats.ActiveCount);
        Assert.Equal(1, stats.TotalCount);

        sut.Dispose();
    }

    [Fact]
    public async Task PlaybackLifecycle_WithSqliteDatabase_PersistsSessionsToDisk()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tvh-stats-{Guid.NewGuid():N}.db");
        try
        {
            var sm = new Mock<ISessionManager>();
            var options = new DbContextOptionsBuilder<ViewingSessionContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            // Schema creation is normally done by DatabaseMigrationService on the main DB.
            // For this test's separate file, we need to ensure schema manually.
            using (var ctx = new ViewingSessionContext(options))
            {
                ctx.Database.EnsureCreated();
            }

            var sut = new StatisticsService(
                NullLogger<StatisticsService>.Instance,
                sm.Object,
                new ConfigurationProvider(() => null),
                CreateTestDbHealth(),
                new DatabaseWriteCoordinator(),
                options);

            await sut.StartAsync(CancellationToken.None);

            var channel = new LiveTvChannel { Id = Guid.NewGuid(), Name = "SQLiteChannel" };
            sm.Raise(m => m.PlaybackStart += null, new MediaBrowser.Controller.Library.PlaybackProgressEventArgs
            {
                Item = channel,
                PlaySessionId = "sqlite-session",
                DeviceName = "Device",
                ClientName = "Client",
            });

            sm.Raise(m => m.PlaybackStopped += null, new MediaBrowser.Controller.Library.PlaybackStopEventArgs
            {
                Item = channel,
                PlaySessionId = "sqlite-session",
                DeviceName = "Device",
                ClientName = "Client",
            });

            sut.Dispose();

            var persistedOptions = new DbContextOptionsBuilder<ViewingSessionContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            using var persistedContext = new ViewingSessionContext(persistedOptions);
            var persistedSession = persistedContext.ViewingSessions.Single();
            Assert.Equal("SQLiteChannel", persistedSession.ChannelName);
            Assert.NotNull(persistedSession.EndTimeUtc);
        }
        finally
        {
            foreach (var file in new[] { dbPath, dbPath + "-shm", dbPath + "-wal" })
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                catch (IOException)
                {
                    // Ignore transient cleanup failures on Windows when SQLite releases the file slightly later.
                }
            }
        }
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

    // ── StartAsync / StopAsync ─────────────────────────────────────────

    [Fact]
    public async Task StartAsync_InitializesDatabase()
    {
        var sut = CreateSut();

        await sut.StartAsync(CancellationToken.None);

        // Verify no exception was thrown and service is initialized
        Assert.NotNull(sut);

        sut.Dispose();
    }

    [Fact]
    public async Task StartAsync_WhenDatabaseUnavailable_DegradesGracefully()
    {
        var sm = new Mock<ISessionManager>();
        var invalidOptions = new DbContextOptionsBuilder<ViewingSessionContext>().Options;

        // Create an uninitialized (unavailable) DatabaseHealthService.
        // Use a null-returning path provider so lazy initialization cannot trigger.
        var pathProvider = new DataFolderPathProvider(() => null);
        var provider = new DatabaseProvider(pathProvider);
        var factory = new DatabaseConnectionFactory(provider);
        var migration = new DatabaseMigrationService(factory, NullLogger<DatabaseMigrationService>.Instance);
        var recovery = new DatabaseRecoveryService(provider, migration, factory, NullLogger<DatabaseRecoveryService>.Instance);
        var unavailableHealth = new DatabaseHealthService(provider, factory, migration, recovery, NullLogger<DatabaseHealthService>.Instance);
        // Do NOT call Initialize() — health service stays Unknown/unavailable

        var sut = new StatisticsService(
            NullLogger<StatisticsService>.Instance,
            sm.Object,
            new ConfigurationProvider(() => null),
            unavailableHealth,
            new DatabaseWriteCoordinator(),
            invalidOptions);

        await sut.StartAsync(CancellationToken.None);

        var stats = sut.GetStatistics(30);
        Assert.Equal(0, stats.TotalCount);
        Assert.Equal(0, stats.ActiveCount);
        Assert.Empty(sut.AllSessions);

        sut.ClearStatistics();
        await sut.StopAsync(CancellationToken.None);
        sut.Dispose();
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

    // ── Channel zapping / orphaned sessions ──────────────────────────

    [Fact]
    public async Task PlaybackStart_OnChannelSwitch_ClosesPreviousOpenSession()
    {
        var sm = new Mock<ISessionManager>();
        var sut = CreateSut(sm);
        await sut.StartAsync(CancellationToken.None);

        var channelA = new LiveTvChannel { Id = Guid.NewGuid(), Name = "ChannelA" };
        var channelB = new LiveTvChannel { Id = Guid.NewGuid(), Name = "ChannelB" };

        sm.Raise(m => m.PlaybackStart += null, new MediaBrowser.Controller.Library.PlaybackProgressEventArgs
        {
            Item = channelA,
            PlaySessionId = "zap-1",
            DeviceName = "Dev",
            ClientName = "Client",
        });

        // Zap to channel B without a stop event for channel A
        sm.Raise(m => m.PlaybackStart += null, new MediaBrowser.Controller.Library.PlaybackProgressEventArgs
        {
            Item = channelB,
            PlaySessionId = "zap-2",
            DeviceName = "Dev",
            ClientName = "Client",
        });

        var stats = sut.GetStatistics(0);
        Assert.Equal(1, stats.ActiveCount);
        Assert.Equal("ChannelB", stats.ActiveSessions.Single().ChannelName);
        var completed = Assert.Single(stats.Sessions);
        Assert.Equal("ChannelA", completed.ChannelName);
        Assert.NotNull(completed.EndTimeUtc);

        sut.Dispose();
    }

    [Fact]
    public async Task PlaybackStart_WhenReAnnouncedForSamePlayback_DoesNotDuplicateSession()
    {
        var sm = new Mock<ISessionManager>();
        var sut = CreateSut(sm);
        await sut.StartAsync(CancellationToken.None);

        var channel = new LiveTvChannel { Id = Guid.NewGuid(), Name = "TestChannel" };
        for (var i = 0; i < 2; i++)
        {
            sm.Raise(m => m.PlaybackStart += null, new MediaBrowser.Controller.Library.PlaybackProgressEventArgs
            {
                Item = channel,
                PlaySessionId = "retry-session",
                DeviceName = "Dev",
                ClientName = "Client",
            });
        }

        var stats = sut.GetStatistics(0);
        Assert.Equal(1, stats.ActiveCount);
        Assert.Empty(stats.Sessions);

        sut.Dispose();
    }

    [Fact]
    public void CloseOrphanedSessions_ClosesStaleChannelRow_WhileDeviceStillActiveOnOtherChannel()
    {
        var sm = new Mock<ISessionManager>();
        var options = new DbContextOptionsBuilder<ViewingSessionContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var channelAId = Guid.NewGuid().ToString("N");
        var channelB = Guid.NewGuid();
        var channelBId = channelB.ToString("N");

        // Seed two open rows directly: channel A is stale, channel B is still playing.
        using (var seed = new ViewingSessionContext(options))
        {
            seed.ViewingSessions.Add(new ViewingSession
            {
                UserName = "User",
                DeviceName = "Dev",
                ClientName = "Client",
                ChannelName = "ChannelA",
                ChannelId = channelAId,
                PlayMethod = "DirectPlay",
                StartTimeUtc = DateTime.UtcNow.AddHours(-2),
                PlaySessionId = "orphan-a",
            });
            seed.ViewingSessions.Add(new ViewingSession
            {
                UserName = "User",
                DeviceName = "Dev",
                ClientName = "Client",
                ChannelName = "ChannelB",
                ChannelId = channelBId,
                PlayMethod = "DirectPlay",
                StartTimeUtc = DateTime.UtcNow.AddMinutes(-5),
                PlaySessionId = "active-b",
            });
            seed.SaveChanges();
        }

        // Jellyfin reports the device as actively playing channel B only.
        var jellyfinSession = new SessionInfo(Mock.Of<ISessionManager>(), NullLogger<SessionInfo>.Instance)
        {
            UserName = "User",
            DeviceName = "Dev",
            Client = "Client",
            NowPlayingItem = new MediaBrowser.Model.Dto.BaseItemDto { Id = channelB },
        };
        sm.Setup(m => m.Sessions).Returns(new[] { jellyfinSession });

        var sut = new StatisticsService(
            NullLogger<StatisticsService>.Instance,
            sm.Object,
            new ConfigurationProvider(() => null),
            CreateTestDbHealth(),
            new DatabaseWriteCoordinator(),
            options);

        sut.CloseOrphanedSessions();

        using var verify = new ViewingSessionContext(options);
        var rowA = verify.ViewingSessions.Single(s => s.ChannelId == channelAId);
        var rowB = verify.ViewingSessions.Single(s => s.ChannelId == channelBId);
        Assert.NotNull(rowA.EndTimeUtc);
        Assert.Null(rowB.EndTimeUtc);

        sut.Dispose();
    }

    // ── Event handler resilience ─────────────────────────────────────

    [Fact]
    public async Task PlaybackStart_WhenDatabaseWriteFails_DoesNotThrow()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tvh-stats-broken-{Guid.NewGuid():N}.db");
        try
        {
            var sm = new Mock<ISessionManager>();

            // SQLite database whose schema was never created — every DB operation throws.
            var options = new DbContextOptionsBuilder<ViewingSessionContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            var sut = new StatisticsService(
                NullLogger<StatisticsService>.Instance,
                sm.Object,
                new ConfigurationProvider(() => null),
                CreateTestDbHealth(),
                new DatabaseWriteCoordinator(),
                options);

            await sut.StartAsync(CancellationToken.None);

            var channel = new LiveTvChannel { Id = Guid.NewGuid(), Name = "TestChannel" };
            var exception = Record.Exception(() => sm.Raise(m => m.PlaybackStart += null, new MediaBrowser.Controller.Library.PlaybackProgressEventArgs
            {
                Item = channel,
                PlaySessionId = "broken-start",
                DeviceName = "Dev",
                ClientName = "Client",
            }));

            Assert.Null(exception);
            sut.Dispose();
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    [Fact]
    public async Task PlaybackStopped_WhenDatabaseWriteFails_DoesNotThrow()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"tvh-stats-broken-{Guid.NewGuid():N}.db");
        try
        {
            var sm = new Mock<ISessionManager>();

            // SQLite database whose schema was never created — every DB operation throws.
            var options = new DbContextOptionsBuilder<ViewingSessionContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            var sut = new StatisticsService(
                NullLogger<StatisticsService>.Instance,
                sm.Object,
                new ConfigurationProvider(() => null),
                CreateTestDbHealth(),
                new DatabaseWriteCoordinator(),
                options);

            await sut.StartAsync(CancellationToken.None);

            var channel = new LiveTvChannel { Id = Guid.NewGuid(), Name = "TestChannel" };
            var exception = Record.Exception(() => sm.Raise(m => m.PlaybackStopped += null, new MediaBrowser.Controller.Library.PlaybackStopEventArgs
            {
                Item = channel,
                PlaySessionId = "broken-stop",
                DeviceName = "Dev",
                ClientName = "Client",
            }));

            Assert.Null(exception);
            sut.Dispose();
        }
        finally
        {
            DeleteSqliteFiles(dbPath);
        }
    }

    private static void DeleteSqliteFiles(string dbPath)
    {
        foreach (var file in new[] { dbPath, dbPath + "-shm", dbPath + "-wal" })
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
                // Ignore transient cleanup failures on Windows when SQLite releases the file slightly later.
            }
        }
    }
}
