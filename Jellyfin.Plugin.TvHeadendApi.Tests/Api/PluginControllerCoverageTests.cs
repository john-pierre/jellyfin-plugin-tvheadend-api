using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Api;
using Jellyfin.Plugin.TvHeadendApi.Model.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Auth;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Stream;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

/// <summary>
/// Additional PluginController tests for uncovered endpoints and constructor null guards.
/// </summary>
public class PluginControllerCoverageTests
{
    private static PluginController CreateController(
        IDiagnosticService? diag = null,
        IDefaultProfileService? profile = null,
        ITokenService? token = null,
        IMediaInfoCacheService? cache = null)
    {
        return new PluginController(
            diag ?? new Mock<IDiagnosticService>().Object,
            profile ?? new Mock<IDefaultProfileService>().Object,
            token ?? new Mock<ITokenService>().Object,
            cache ?? new Mock<IMediaInfoCacheService>().Object);
    }

    // --- GetPluginInfo ---

    [Fact]
    public void GetPluginInfo_ReturnsOk()
    {
        var sut = CreateController();
        var result = sut.GetPluginInfo();
        Assert.IsType<OkObjectResult>(result.Result);
    }

    // --- WarmCacheStream ---

    [Fact]
    public async Task WarmCacheStream_ClientDisconnectsDuringWarmup_DoesNotThrow()
    {
        // Simulates the admin closing the dashboard mid-warmup: the request token cancels and the
        // warmup task observes it cooperatively. The action must swallow the resulting cancellation
        // of the awaited warmup task — like its other cancellation points — instead of rethrowing it.
        var cacheMock = new Mock<IMediaInfoCacheService>();
        cacheMock
            .Setup(x => x.WarmAllChannelCachesAsync(It.IsAny<IProgress<CacheWarmupProgress>?>(), It.IsAny<CancellationToken>()))
            .Returns(async (IProgress<CacheWarmupProgress>? progress, CancellationToken token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new CacheWarmupResult(0, 0, 0, 0, new List<string>());
            });

        var sut = CreateController(cache: cacheMock.Object);
        sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
        sut.HttpContext.Response.Body = new MemoryStream();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var exception = await Record.ExceptionAsync(() => sut.WarmCacheStream(cts.Token));

        Assert.Null(exception);
    }

    // --- Constructor null guards ---

    [Fact]
    public void Constructor_NullDiagnose_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PluginController(
            null!,
            new Mock<IDefaultProfileService>().Object,
            new Mock<ITokenService>().Object,
            new Mock<IMediaInfoCacheService>().Object));
    }

    [Fact]
    public void Constructor_NullProfileService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PluginController(
            new Mock<IDiagnosticService>().Object,
            null!,
            new Mock<ITokenService>().Object,
            new Mock<IMediaInfoCacheService>().Object));
    }

    [Fact]
    public void Constructor_NullTokenService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PluginController(
            new Mock<IDiagnosticService>().Object,
            new Mock<IDefaultProfileService>().Object,
            null!,
            new Mock<IMediaInfoCacheService>().Object));
    }
}
