using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Api;
using Jellyfin.Plugin.TvHeadendApi.Model.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Auth;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
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
        ITokenService? token = null)
    {
        return new PluginController(
            diag ?? new Mock<IDiagnosticService>().Object,
            profile ?? new Mock<IDefaultProfileService>().Object,
            token ?? new Mock<ITokenService>().Object);
    }

    // --- GetPluginInfo ---

    [Fact]
    public void GetPluginInfo_ReturnsOk()
    {
        var sut = CreateController();
        var result = sut.GetPluginInfo();
        Assert.IsType<OkObjectResult>(result.Result);
    }

    // --- Constructor null guards ---

    [Fact]
    public void Constructor_NullDiagnose_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PluginController(
            null!,
            new Mock<IDefaultProfileService>().Object,
            new Mock<ITokenService>().Object));
    }

    [Fact]
    public void Constructor_NullProfileService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PluginController(
            new Mock<IDiagnosticService>().Object,
            null!,
            new Mock<ITokenService>().Object));
    }

    [Fact]
    public void Constructor_NullTokenService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PluginController(
            new Mock<IDiagnosticService>().Object,
            new Mock<IDefaultProfileService>().Object,
            null!));
    }
}
