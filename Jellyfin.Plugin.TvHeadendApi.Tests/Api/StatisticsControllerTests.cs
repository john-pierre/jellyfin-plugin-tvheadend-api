using System;
using Jellyfin.Plugin.TvHeadendApi.Api.Endpoint;
using Jellyfin.Plugin.TvHeadendApi.Model.Statistic;
using Jellyfin.Plugin.TvHeadendApi.Service.Statistic;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

/// <summary>
/// Tests for the <see cref="StatisticsController"/> endpoints and constructor null guards.
/// </summary>
public class StatisticsControllerTests
{
    private static StatisticsController CreateController(
        IStatisticsService? stats = null)
    {
        return new StatisticsController(
            stats ?? new Mock<IStatisticsService>().Object);
    }

    // --- GetStatistics ---

    [Fact]
    public void GetStatistics_ReturnsOkWithPayload()
    {
        var mockStats = new Mock<IStatisticsService>();
        mockStats.Setup(x => x.GetStatistics(30)).Returns(new ViewingStatisticsResult());
        var sut = CreateController(stats: mockStats.Object);

        var result = sut.GetStatistics(30);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<ViewingStatisticsResult>(ok.Value);
    }

    // --- ClearStatistics ---

    [Fact]
    public void ClearStatistics_ReturnsOk()
    {
        var mockStats = new Mock<IStatisticsService>();
        var sut = CreateController(stats: mockStats.Object);

        var result = sut.ClearStatistics();

        Assert.IsType<OkObjectResult>(result);
        mockStats.Verify(x => x.ClearStatistics(), Times.Once);
    }

    // --- Constructor null guard ---

    [Fact]
    public void Constructor_NullStatisticsService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new StatisticsController(null!));
    }
}
