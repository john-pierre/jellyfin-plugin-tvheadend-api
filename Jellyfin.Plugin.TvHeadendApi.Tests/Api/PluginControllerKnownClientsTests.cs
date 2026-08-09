// Tests for the KnownClients endpoint feeding the configuration rule editor.

using Jellyfin.Plugin.TvHeadendApi.Api;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Auth;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Stream;
using Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

/// <summary>
/// Tests for <see cref="PluginController.GetKnownClients"/>.
/// </summary>
[Trait("Category", "Unit")]
public class PluginControllerKnownClientsTests
{
    [Fact]
    public void GetKnownClients_ReturnsServiceResult()
    {
        var expected = new KnownClientsResult
        {
            Users = new[] { new KnownUser { Id = "00000000-0000-0000-0000-000000000001", Name = "alice" } },
            Clients = new[] { "Jellyfin Web" },
            Devices = new[] { "Chrome" },
        };
        var knownClientsService = new Mock<IKnownClientsService>();
        knownClientsService.Setup(s => s.GetKnownClients()).Returns(expected);

        var controller = CreateController(knownClientsService.Object);

        var result = controller.GetKnownClients();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var value = Assert.IsType<KnownClientsResult>(okResult.Value);
        Assert.Same(expected, value);
        knownClientsService.Verify(s => s.GetKnownClients(), Times.Once);
    }

    [Fact]
    public void GetKnownClients_WithoutService_ReturnsEmptyResult()
    {
        var controller = CreateController(knownClientsService: null);

        var result = controller.GetKnownClients();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var value = Assert.IsType<KnownClientsResult>(okResult.Value);
        Assert.Empty(value.Users);
        Assert.Empty(value.Clients);
        Assert.Empty(value.Devices);
    }

    private static PluginController CreateController(IKnownClientsService? knownClientsService)
    {
        return new PluginController(
            new Mock<IDiagnosticService>().Object,
            new Mock<IDefaultProfileService>().Object,
            new Mock<ITokenService>().Object,
            new Mock<IMediaInfoCacheService>().Object,
            knownClientsService: knownClientsService);
    }
}
