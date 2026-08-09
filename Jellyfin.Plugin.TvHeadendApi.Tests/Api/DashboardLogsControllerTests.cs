using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TvHeadendApi.Api.Endpoint;
using Jellyfin.Plugin.TvHeadendApi.Api.Model;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Statistic;
using Jellyfin.Plugin.TvHeadendApi.Service;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Logging;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Api;

/// <summary>
/// Tests for <see cref="DashboardLogsController"/>.
/// </summary>
public class DashboardLogsControllerTests
{
    private readonly Mock<IPluginLogQueryService> _logServiceMock;
    private readonly PluginConfiguration _config;
    private readonly ConfigurationProvider _configProvider;
    private readonly DashboardLogsController _controller;

    public DashboardLogsControllerTests()
    {
        _logServiceMock = new Mock<IPluginLogQueryService>();
        _config = new PluginConfiguration();
        _configProvider = new ConfigurationProvider(() => _config);
        _controller = new DashboardLogsController(_logServiceMock.Object, _configProvider);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetDashboardLogs_EmptyResponse_ReturnsValid()
    {
        _logServiceMock.Setup(s => s.QueryLogs(
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<string>()))
            .Returns(Array.Empty<PluginLogEntry>());
        _logServiceMock.Setup(s => s.GetTotalCount(
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<string?>()))
            .Returns(0);

        var result = _controller.GetDashboardLogs();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DashboardLogsResponse>(okResult.Value);
        Assert.Empty(response.Entries);
        Assert.Equal(0, response.TotalCount);
        Assert.False(string.IsNullOrEmpty(response.LastUpdatedUtc));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetDashboardLogs_ReturnsEntries()
    {
        var entries = new List<PluginLogEntry>
        {
            new()
            {
                Id = 1,
                CreatedAtUtc = DateTime.UtcNow,
                Source = "plugin",
                Level = "Information",
                LogType = "information",
                Category = "TestService",
                Message = "Service started",
            },
            new()
            {
                Id = 2,
                CreatedAtUtc = DateTime.UtcNow.AddSeconds(-1),
                Source = "tvheadend",
                Level = "Warning",
                LogType = "tvheadend_warning",
                Category = "mpegts",
                Message = "Signal lost",
            },
        };

        _logServiceMock.Setup(s => s.QueryLogs(null, null, null, null, 500, "created_at_utc", "desc"))
            .Returns(entries);
        _logServiceMock.Setup(s => s.GetTotalCount(null, null, null, null)).Returns(2);

        var result = _controller.GetDashboardLogs();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DashboardLogsResponse>(okResult.Value);
        Assert.Equal(2, response.Entries.Count);
        Assert.Equal(2, response.TotalCount);
        Assert.Equal("plugin", response.Entries[0].Source);
        Assert.Equal("tvheadend", response.Entries[1].Source);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetDashboardLogs_SourceFilter_PassedToService()
    {
        _logServiceMock.Setup(s => s.QueryLogs("tvheadend", null, null, null, 500, "created_at_utc", "desc"))
            .Returns(Array.Empty<PluginLogEntry>());
        _logServiceMock.Setup(s => s.GetTotalCount("tvheadend", null, null, null)).Returns(0);

        var result = _controller.GetDashboardLogs(source: "tvheadend");

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DashboardLogsResponse>(okResult.Value);
        Assert.Equal("tvheadend", response.FiltersApplied.Source);
        _logServiceMock.Verify(s => s.QueryLogs("tvheadend", null, null, null, 500, "created_at_utc", "desc"), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetDashboardLogs_SearchFilter_PassedToService()
    {
        _logServiceMock.Setup(s => s.QueryLogs(null, null, null, "signal", 500, "created_at_utc", "desc"))
            .Returns(Array.Empty<PluginLogEntry>());

        var result = _controller.GetDashboardLogs(search: "signal");

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DashboardLogsResponse>(okResult.Value);
        Assert.Equal("signal", response.FiltersApplied.Search);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetDashboardLogs_SortApplied()
    {
        _logServiceMock.Setup(s => s.QueryLogs(null, null, null, null, 500, "source", "asc"))
            .Returns(Array.Empty<PluginLogEntry>());

        var result = _controller.GetDashboardLogs(sortField: "source", sortDirection: "asc");

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DashboardLogsResponse>(okResult.Value);
        Assert.Equal("source", response.FiltersApplied.SortField);
        Assert.Equal("asc", response.FiltersApplied.SortDirection);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetDashboardLogs_LastUpdatedUtcPopulated()
    {
        _logServiceMock.Setup(s => s.QueryLogs(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Array.Empty<PluginLogEntry>());

        var result = _controller.GetDashboardLogs();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DashboardLogsResponse>(okResult.Value);
        Assert.False(string.IsNullOrEmpty(response.LastUpdatedUtc));
        // Should be a valid ISO 8601 date
        Assert.True(DateTime.TryParse(response.LastUpdatedUtc, out _));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetDashboardLogs_SensitiveFieldsNotExposed()
    {
        var entries = new List<PluginLogEntry>
        {
            new()
            {
                Id = 1,
                CreatedAtUtc = DateTime.UtcNow,
                Source = "plugin",
                Level = "Information",
                LogType = "information",
                Message = "test",
                RawSource = "should not be in DTO",
                RawLineHash = "hashvalue",
            },
        };

        _logServiceMock.Setup(s => s.QueryLogs(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(entries);
        _logServiceMock.Setup(s => s.GetTotalCount(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(1);

        var result = _controller.GetDashboardLogs();
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DashboardLogsResponse>(okResult.Value);

        // DTO should not expose RawSource or RawLineHash
        var dto = response.Entries[0];
        var dtoType = dto.GetType();
        Assert.Null(dtoType.GetProperty("RawSource"));
        Assert.Null(dtoType.GetProperty("RawLineHash"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetDashboardLogs_LimitClampedToMax()
    {
        _logServiceMock.Setup(s => s.QueryLogs(null, null, null, null, 5000, "created_at_utc", "desc"))
            .Returns(Array.Empty<PluginLogEntry>());

        var result = _controller.GetDashboardLogs(limit: 50000);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DashboardLogsResponse>(okResult.Value);
        Assert.Equal(5000, response.Limit);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetDashboardLogs_CombinedPluginAndTvheadend_OrderedCorrectly()
    {
        var now = DateTime.UtcNow;
        var entries = new List<PluginLogEntry>
        {
            new() { Id = 1, CreatedAtUtc = now, Source = "plugin", Level = "Information", LogType = "information", Message = "newest" },
            new() { Id = 2, CreatedAtUtc = now.AddSeconds(-1), Source = "tvheadend", Level = "Warning", LogType = "tvheadend_warning", Message = "older" },
            new() { Id = 3, CreatedAtUtc = now.AddSeconds(-2), Source = "plugin", Level = "Error", LogType = "error", Message = "oldest" },
        };

        _logServiceMock.Setup(s => s.QueryLogs(null, null, null, null, 500, "created_at_utc", "desc"))
            .Returns(entries);
        _logServiceMock.Setup(s => s.GetTotalCount(null, null, null, null)).Returns(3);

        var result = _controller.GetDashboardLogs();
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<DashboardLogsResponse>(okResult.Value);

        Assert.Equal(3, response.Entries.Count);
        Assert.Equal("plugin", response.Entries[0].Source);
        Assert.Equal("tvheadend", response.Entries[1].Source);
        Assert.Equal("plugin", response.Entries[2].Source);
    }
}
