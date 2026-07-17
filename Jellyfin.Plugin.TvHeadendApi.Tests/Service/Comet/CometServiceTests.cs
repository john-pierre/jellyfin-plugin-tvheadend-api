using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Comet;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Database;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Jellyfin.Plugin.TvHeadendApi.Service.Statistic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Comet;

public class CometServiceTests
{
    private readonly UrlBuilder _urlBuilder = new();

    private static CometService CreateSut(
        PluginConfiguration? config = null,
        DbContextOptions<ViewingSessionContext>? dbOptions = null,
        DatabaseWriteCoordinator? writeCoordinator = null,
        ILogger<CometService>? logger = null)
    {
        var effectiveConfig = config ?? new PluginConfiguration();
        return new CometService(
            logger ?? NullLogger<CometService>.Instance,
            Mock.Of<IApiClient>(),
            new UrlBuilder(),
            new ConfigurationProvider(() => effectiveConfig),
            dbOptions,
            null,
            writeCoordinator);
    }

    [Fact]
    public void BuildWebSocketUri_WithAuthTokenAndWebRoot_ReturnsWssUriWithAuthQuery()
    {
        var config = new PluginConfiguration
        {
            Host = "tvheadend.local",
            Port = 9981,
            UseSSL = true,
            Webroot = "/tvh",
            AllowAnonymousAccess = false,
            AuthToken = "token123",
        };

        var uri = CometService.BuildWebSocketUri(config, _urlBuilder);

        Assert.Equal("wss", uri.Scheme);
        Assert.Equal("tvheadend.local", uri.Host);
        Assert.Equal(9981, uri.Port);
        Assert.Equal("/tvh/comet/ws", uri.AbsolutePath);
        Assert.Equal("auth=token123", uri.Query.TrimStart('?'));
    }

    [Fact]
    public void BuildWebSocketUri_WithAnonymousAccess_ReturnsWsUriWithoutAuthQuery()
    {
        var config = new PluginConfiguration
        {
            Host = "127.0.0.1",
            Port = 9981,
            UseSSL = false,
            Webroot = "/",
            AllowAnonymousAccess = true,
            AuthToken = "token123",
        };

        var uri = CometService.BuildWebSocketUri(config, _urlBuilder);

        Assert.Equal("ws", uri.Scheme);
        Assert.Equal("/comet/ws", uri.AbsolutePath);
        Assert.True(string.IsNullOrEmpty(uri.Query));
    }

    [Fact]
    public void WebSocketSubProtocol_IsTvHeadendComet()
    {
        Assert.Equal("tvheadend-comet", CometService.WebSocketSubProtocol);
    }

    [Fact]
    public void CalculateBackoffDelay_GrowsExponentially()
    {
        // attempt 1: 2 * 2^0 + jitter[0..2) => [2, 4)
        var first = CometService.CalculateBackoffDelay(1, 2, 120);

        // attempt 3: 2 * 2^2 + jitter[0..2) => [8, 10)
        var third = CometService.CalculateBackoffDelay(3, 2, 120);

        Assert.InRange(first.TotalSeconds, 2, 4);
        Assert.InRange(third.TotalSeconds, 8, 10);
        Assert.True(third > first);
    }

    [Fact]
    public void CalculateBackoffDelay_IsCappedAtMaxDelay()
    {
        // The exponent is clamped at 2^6: 2 * 64 = 128 > 120, so the cap applies exactly.
        var capped = CometService.CalculateBackoffDelay(20, 2, 120);

        Assert.Equal(120, capped.TotalSeconds);
    }

    [Fact]
    public async Task ApplyPostSessionBackoff_GracefulCloseWithoutMessages_IncrementsAttempt()
    {
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // The cancelled token aborts the backoff delay immediately, but the attempt counter
        // must already have been incremented — a graceful close before any message is a failure.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.ApplyPostSessionBackoffAsync(receivedAnyMessage: false, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.ApplyPostSessionBackoffAsync(receivedAnyMessage: false, cts.Token));

        Assert.Equal(2, sut.ReconnectAttempt);
    }

    [Fact]
    public async Task ApplyPostSessionBackoff_MessagesReceived_ResetsAttemptWithoutDelay()
    {
        var sut = CreateSut();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.ApplyPostSessionBackoffAsync(receivedAnyMessage: false, cts.Token));
        Assert.Equal(1, sut.ReconnectAttempt);

        var delay = await sut.ApplyPostSessionBackoffAsync(receivedAnyMessage: true, CancellationToken.None);

        Assert.Equal(TimeSpan.Zero, delay);
        Assert.Equal(0, sut.ReconnectAttempt);
    }

    [Fact]
    public async Task ApplyPostSessionBackoff_NoMessages_ReturnsPositiveBoundedDelay()
    {
        var config = new PluginConfiguration
        {
            CometReconnectBaseDelaySeconds = 1,
            CometReconnectMaxDelaySeconds = 1,
        };
        var sut = CreateSut(config);

        var delay = await sut.ApplyPostSessionBackoffAsync(receivedAnyMessage: false, CancellationToken.None);

        Assert.True(delay > TimeSpan.Zero);
        Assert.True(delay <= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AddLog_PersistsThroughWriteCoordinator()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"comet-test-{Guid.NewGuid():N}.db");
        var dbOptions = new DbContextOptionsBuilder<ViewingSessionContext>()
            .UseSqlite($"DataSource={dbPath}")
            .Options;

        using (var db = new ViewingSessionContext(dbOptions))
        {
            db.Database.EnsureCreated();
        }

        using var coordinator = new DatabaseWriteCoordinator();
        var sut = CreateSut(dbOptions: dbOptions, writeCoordinator: coordinator);

        // Hold the write lock: the background flusher must block on the coordinator
        // instead of writing to SQLite concurrently with other writers.
        var writeLock = coordinator.AcquireWrite();
        sut.AddLog("comet line one");
        sut.AddLog("comet line two");
        await Task.Delay(500);

        using (var verify = new ViewingSessionContext(dbOptions))
        {
            Assert.Equal(0, verify.TvheadendLogEntries.Count());
        }

        writeLock.Dispose();

        // Poll until the flusher has drained the queue after acquiring the lock.
        var count = 0;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var verify = new ViewingSessionContext(dbOptions);
            count = verify.TvheadendLogEntries.Count();
            if (count >= 2)
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert.Equal(2, count);
        Assert.Equal(2, sut.GetRecentLogs().Count);

        await sut.StopAsync();
    }

    [Fact]
    public async Task AddLog_WithoutDatabase_OnlyBuffersInMemory()
    {
        var sut = CreateSut();

        sut.AddLog("in-memory only");

        var logs = sut.GetRecentLogs();
        Assert.Single(logs);
        Assert.Equal("in-memory only", logs[0].Text);

        await sut.StopAsync();
    }

    [Fact]
    public async Task StopAsync_SecondInvocation_IsNoOp()
    {
        var logger = new Mock<ILogger<CometService>>();
        var sut = CreateSut(logger: logger.Object);

        await sut.StopAsync();
        await sut.StopAsync();

        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("stopped", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Dispose_AfterStopAsync_DoesNotRerunShutdown()
    {
        var logger = new Mock<ILogger<CometService>>();
        var sut = CreateSut(logger: logger.Object);

        await sut.StopAsync();
        sut.Dispose();

        logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("stopped", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var sut = CreateSut();

        await sut.DisposeAsync();
    }
}
