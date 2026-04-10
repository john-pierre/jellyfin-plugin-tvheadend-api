using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Auth;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Auth;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class ProvisioningServiceTests
{
    [Fact]
    public void Constructor_WithNullLogger_Throws()
    {
        var api = new Mock<IApiClient>();
        var tokenService = new Mock<ITokenService>();

        Assert.Throws<ArgumentNullException>(() => new ProvisioningService(null!, api.Object, tokenService.Object));
    }

    [Fact]
    public async Task CreateProfileAsync_WhenConfigurationMissing_ReturnsFailure()
    {
        var api = new Mock<IApiClient>();
        var tokenService = new Mock<ITokenService>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns((PluginConfiguration?)null);

        var sut = new ProvisioningService(NullLogger<ProvisioningService>.Instance, api.Object, tokenService.Object);
        var result = await sut.CreateProfileAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("configuration", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAuthTokenAsync_WhenTokenServiceFails_ReturnsFailure()
    {
        var api = new Mock<IApiClient>();
        var tokenService = new Mock<ITokenService>();
        tokenService
            .Setup(x => x.GenerateValidTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthTokenGenerationResult
            {
                Success = false,
                Message = "Token generation failed."
            });

        var sut = new ProvisioningService(NullLogger<ProvisioningService>.Instance, api.Object, tokenService.Object);
        var result = await sut.GenerateAuthTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("failed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAuthTokenAsync_WhenTokenExistsButPluginInstanceMissing_ReturnsFailure()
    {
        var api = new Mock<IApiClient>();
        var tokenService = new Mock<ITokenService>();
        tokenService
            .Setup(x => x.GenerateValidTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthTokenGenerationResult
            {
                Success = true,
                AuthToken = "abc123",
                Message = "Auth token generated successfully."
            });

        var sut = new ProvisioningService(NullLogger<ProvisioningService>.Instance, api.Object, tokenService.Object);
        var result = await sut.GenerateAuthTokenAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Plugin instance", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateProfileAsync_WhenHttpThrows_ReturnsConnectionFailure()
    {
        var config = new PluginConfiguration();
        var api = new Mock<IApiClient>();
        var tokenService = new Mock<ITokenService>();

        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient());
        api.Setup(x => x.GetBaseUrl(config)).Returns("http://127.0.0.1:9981");
        api.Setup(x => x.GetWebRoot(config)).Returns("/");
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));

        var sut = new ProvisioningService(NullLogger<ProvisioningService>.Instance, api.Object, tokenService.Object);
        var result = await sut.CreateProfileAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Cannot connect", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}

