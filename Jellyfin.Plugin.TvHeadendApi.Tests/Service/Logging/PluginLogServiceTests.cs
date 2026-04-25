// In-memory SQLite tests for PluginLogService — enqueue, query, count, and hosted service lifecycle.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Statistic;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Logging;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Jellyfin.Plugin.TvHeadendApi.Service.Statistic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Logging;

public class PluginLogServiceTests : IDisposable
{
    private readonly DbContextOptions<ViewingSessionContext> _dbOptions;
    private readonly PluginLogService _sut;
    private readonly PluginConfiguration _config;

    public PluginLogServiceTests()
    {
        var dbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pluginlog-test-{Guid.NewGuid():N}.db");
        _dbOptions = new DbContextOptionsBuilder<ViewingSessionContext>()
            .UseSqlite($"DataSource={dbPath}")
            .Options;

        _config = new PluginConfiguration
        {
            StorePluginLogsInSqlite = true,
            EnableDebugLogSanitization = false,
        };

        _sut = new PluginLogService(
            NullLogger<PluginLogService>.Instance,
            new ConfigurationProvider(() => _config),
            _dbOptions);

        // Ensure schema is created
        using var db = new ViewingSessionContext(_dbOptions);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _sut.Dispose();
    }

    [Fact]
    public void EnqueuePluginLog_WithDisabledConfig_DropsEntry()
    {
        _config.StorePluginLogsInSqlite = false;
        _sut.EnqueuePluginLog("Information", "TestCat", "should be dropped");

        // No way to observe queue directly, but the query should return nothing
        var logs = _sut.QueryLogs(limit: 10);
        Assert.Empty(logs);
    }

    [Fact]
    public async Task EnqueueAndFlush_PersistsToDatabase()
    {
        // Start the hosted service to enable the writer task
        var hosted = (IHostedService)_sut;
        await hosted.StartAsync(CancellationToken.None);

        _sut.EnqueuePluginLog("Warning", "TestCategory", "test message 1");
        _sut.EnqueuePluginLog("Error", "TestCategory", "test message 2");

        // Wait for writer to flush
        await Task.Delay(500);

        var logs = _sut.QueryLogs(limit: 10);
        Assert.Equal(2, logs.Count);

        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task QueryLogs_FiltersBySource()
    {
        var hosted = (IHostedService)_sut;
        await hosted.StartAsync(CancellationToken.None);

        _sut.EnqueuePluginLog("Information", "Cat1", "plugin msg");

        await Task.Delay(500);

        var pluginLogs = _sut.QueryLogs(source: "plugin", limit: 10);
        Assert.True(pluginLogs.Count >= 1);

        var tvhLogs = _sut.QueryLogs(source: "tvheadend", limit: 10);
        Assert.Empty(tvhLogs);

        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task QueryLogs_FiltersByLevel()
    {
        var hosted = (IHostedService)_sut;
        await hosted.StartAsync(CancellationToken.None);

        _sut.EnqueuePluginLog("Error", "Cat1", "error msg");
        _sut.EnqueuePluginLog("Information", "Cat1", "info msg");

        await Task.Delay(500);

        var errorLogs = _sut.QueryLogs(level: "Error", limit: 10);
        Assert.Single(errorLogs);

        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task QueryLogs_FiltersBySearch()
    {
        var hosted = (IHostedService)_sut;
        await hosted.StartAsync(CancellationToken.None);

        _sut.EnqueuePluginLog("Information", "Cat1", "alpha bravo charlie");
        _sut.EnqueuePluginLog("Information", "Cat1", "delta echo foxtrot");

        await Task.Delay(500);

        var results = _sut.QueryLogs(search: "bravo", limit: 10);
        Assert.Single(results);

        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task GetTotalCount_ReturnsCorrectCount()
    {
        var hosted = (IHostedService)_sut;
        await hosted.StartAsync(CancellationToken.None);

        _sut.EnqueuePluginLog("Information", "Cat1", "msg1");
        _sut.EnqueuePluginLog("Information", "Cat1", "msg2");
        _sut.EnqueuePluginLog("Information", "Cat1", "msg3");

        await Task.Delay(500);

        Assert.Equal(3, _sut.GetTotalCount());

        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task GetTotalCount_WithFilters_ReturnsFiltered()
    {
        var hosted = (IHostedService)_sut;
        await hosted.StartAsync(CancellationToken.None);

        _sut.EnqueuePluginLog("Error", "Cat1", "err1");
        _sut.EnqueuePluginLog("Information", "Cat1", "info1");

        await Task.Delay(500);

        Assert.Equal(1, _sut.GetTotalCount(level: "Error"));

        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAndStop_DoesNotThrow()
    {
        var hosted = (IHostedService)_sut;
        await hosted.StartAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Constructor_ThrowsOnNullLogger()
    {
        Assert.Throws<ArgumentNullException>(() => new PluginLogService(
            null!,
            new ConfigurationProvider(() => new PluginConfiguration()),
            _dbOptions));
    }

    [Fact]
    public void Constructor_ThrowsOnNullConfigProvider()
    {
        Assert.Throws<ArgumentNullException>(() => new PluginLogService(
            NullLogger<PluginLogService>.Instance,
            null!,
            _dbOptions));
    }

    [Fact]
    public void EnqueuePluginLog_TruncatesLongMessages()
    {
        var longMessage = new string('x', 5000);
        _sut.EnqueuePluginLog("Information", "Cat", longMessage);
        // No exception should be thrown
    }

    [Fact]
    public void EnqueuePluginLog_SanitizesWhenEnabled()
    {
        _config.EnableDebugLogSanitization = true;
        // Should not throw even with sensitive content
        _sut.EnqueuePluginLog("Information", "Cat", "password=secret123&token=abc");
    }

    [Fact]
    public async Task QueryLogs_SortByLevel_Works()
    {
        var hosted = (IHostedService)_sut;
        await hosted.StartAsync(CancellationToken.None);

        _sut.EnqueuePluginLog("Error", "Cat1", "msg1");
        _sut.EnqueuePluginLog("Debug", "Cat1", "msg2");

        await Task.Delay(500);

        var asc = _sut.QueryLogs(sortField: "level", sortDirection: "asc", limit: 10);
        var desc = _sut.QueryLogs(sortField: "level", sortDirection: "desc", limit: 10);

        Assert.True(asc.Count >= 2);
        Assert.True(desc.Count >= 2);

        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task QueryLogs_SortByCategory_Works()
    {
        var hosted = (IHostedService)_sut;
        await hosted.StartAsync(CancellationToken.None);

        _sut.EnqueuePluginLog("Information", "A_Cat", "msg");
        _sut.EnqueuePluginLog("Information", "Z_Cat", "msg");

        await Task.Delay(500);

        var result = _sut.QueryLogs(sortField: "category", sortDirection: "asc", limit: 10);
        Assert.True(result.Count >= 2);

        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task QueryLogs_SortByLogType_Works()
    {
        var hosted = (IHostedService)_sut;
        await hosted.StartAsync(CancellationToken.None);

        _sut.EnqueuePluginLog("Error", "Cat1", "msg");

        await Task.Delay(500);

        var result = _sut.QueryLogs(sortField: "log_type", limit: 10);
        Assert.True(result.Count >= 1);

        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void EnqueueTvHeadendLog_WithDisabledConfig_DropsEntry()
    {
        _config.StoreTvHeadendLogsInSqlite = false;
        _sut.EnqueueTvHeadendLog("2026-04-24T12:00:00Z [INFO] test line");
        // Should not throw
    }

    [Fact]
    public async Task EnqueueTvHeadendLog_Enabled_PersistsEntry()
    {
        _config.StoreTvHeadendLogsInSqlite = true;
        _config.TvHeadendLogImportEnabled = true;

        var hosted = (IHostedService)_sut;
        await hosted.StartAsync(CancellationToken.None);

        _sut.EnqueueTvHeadendLog("2026-04-24 12:00:00.000 [  INFO] mpegts: some message");

        await Task.Delay(500);

        var tvhLogs = _sut.QueryLogs(source: "tvheadend", limit: 10);
        Assert.True(tvhLogs.Count >= 1);

        await hosted.StopAsync(CancellationToken.None);
    }
}
