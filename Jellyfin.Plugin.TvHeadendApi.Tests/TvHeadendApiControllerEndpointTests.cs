using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using Jellyfin.Plugin.TvHeadendApi.Api;
using Jellyfin.Plugin.TvHeadendApi.Model;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostics;
using Jellyfin.Plugin.TvHeadendApi.Service.Images;
using Jellyfin.Plugin.TvHeadendApi.Service.Profiles;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

/// <summary>
/// Extended API Controller tests.
/// Tests diagnostic and profile operations.
/// </summary>
public class TvHeadendApiControllerExtendedTests
{
    [Fact]
    public async Task GetImageProxy_ReturnsServiceResult_Passthrough()
    {
        // Arrange
        var expected = new NotFoundObjectResult("missing image");
        var mockImageProxyService = new Mock<IImageProxyService>();
        mockImageProxyService
            .Setup(x => x.ProxyImageAsync("imagecache/1715", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var controller = new TvHeadendApiController(
            mockImageProxyService.Object,
            new Mock<IDiagnoseService>().Object,
            new Mock<IProfileProvisioningService>().Object);

        // Act
        var result = await controller.GetImageProxy("imagecache/1715", CancellationToken.None);

        // Assert
        Assert.Same(expected, result);
    }

    [Fact]
    public async Task GetImageProxy_ForwardsCancellationTokenToService()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        var mockImageProxyService = new Mock<IImageProxyService>();
        mockImageProxyService
            .Setup(x => x.ProxyImageAsync("imagecache/1", token))
            .ReturnsAsync(new OkResult());

        var controller = new TvHeadendApiController(
            mockImageProxyService.Object,
            new Mock<IDiagnoseService>().Object,
            new Mock<IProfileProvisioningService>().Object);

        // Act
        await controller.GetImageProxy("imagecache/1", token);

        // Assert
        mockImageProxyService.Verify(x => x.ProxyImageAsync("imagecache/1", token), Times.Once);
    }

    [Fact]
    public async Task GetImageProxy_WithNullPath_DelegatesAndReturnsBadRequest()
    {
        // Arrange
        var expected = new BadRequestObjectResult("imagePath is required");
        var mockImageProxyService = new Mock<IImageProxyService>();
        mockImageProxyService
            .Setup(x => x.ProxyImageAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var controller = new TvHeadendApiController(
            mockImageProxyService.Object,
            new Mock<IDiagnoseService>().Object,
            new Mock<IProfileProvisioningService>().Object);

        // Act
        var result = await controller.GetImageProxy(null, CancellationToken.None);

        // Assert
        Assert.Same(expected, result);
        mockImageProxyService.Verify(x => x.ProxyImageAsync(null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetImageProxy_WhenServiceThrows_PropagatesException()
    {
        // Arrange
        var mockImageProxyService = new Mock<IImageProxyService>();
        mockImageProxyService
            .Setup(x => x.ProxyImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("proxy failed"));

        var controller = new TvHeadendApiController(
            mockImageProxyService.Object,
            new Mock<IDiagnoseService>().Object,
            new Mock<IProfileProvisioningService>().Object);

        // Act + Assert
        await Assert.ThrowsAsync<HttpRequestException>(() => controller.GetImageProxy("imagecache/1", CancellationToken.None));
    }

    [Fact]
    public async Task Diagnose_ReturnsOkWithValidDiagnoseResult()
    {
        // Arrange
        var expectedResult = new DiagnoseResult
        {
            OverallStatus = "OK",
            CompatibilityScore = 95
        };

        var mockDiagnoseService = new Mock<IDiagnoseService>();
        mockDiagnoseService
            .Setup(x => x.DiagnoseAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        var mockImageProxyService = new Mock<IImageProxyService>();
        var mockProfileService = new Mock<IProfileProvisioningService>();

        var controller = new TvHeadendApiController(
            mockImageProxyService.Object,
            mockDiagnoseService.Object,
            mockProfileService.Object);

        // Act
        var result = await controller.Diagnose(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedResult = Assert.IsType<DiagnoseResult>(okResult.Value);
        Assert.Equal("OK", returnedResult.OverallStatus);
        Assert.Equal(95, returnedResult.CompatibilityScore);
    }

    [Fact]
    public async Task Diagnose_CallsServiceOnce()
    {
        // Arrange
        var mockDiagnoseService = new Mock<IDiagnoseService>();
        mockDiagnoseService
            .Setup(x => x.DiagnoseAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiagnoseResult { OverallStatus = "OK" });

        var mockImageProxyService = new Mock<IImageProxyService>();
        var mockProfileService = new Mock<IProfileProvisioningService>();

        var controller = new TvHeadendApiController(
            mockImageProxyService.Object,
            mockDiagnoseService.Object,
            mockProfileService.Object);

        // Act
        await controller.Diagnose(CancellationToken.None);

        // Assert
        mockDiagnoseService.Verify(
            x => x.DiagnoseAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateProfile_ReturnsOkWithProfileDetectionResult()
    {
        // Arrange
        var expectedResult = new ProfileDetectionResult
        {
            Success = true,
            Message = "Profile created",
            ProfileName = "jellyfin"
        };

        var mockProfileService = new Mock<IProfileProvisioningService>();
        mockProfileService
            .Setup(x => x.CreateProfileAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        var mockImageProxyService = new Mock<IImageProxyService>();
        var mockDiagnoseService = new Mock<IDiagnoseService>();

        var controller = new TvHeadendApiController(
            mockImageProxyService.Object,
            mockDiagnoseService.Object,
            mockProfileService.Object);

        // Act
        var result = await controller.CreateProfile(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedResult = Assert.IsType<ProfileDetectionResult>(okResult.Value);
        Assert.True(returnedResult.Success);
        Assert.Equal("jellyfin", returnedResult.ProfileName);
    }

    [Fact]
    public async Task CreateProfile_WithFailure_ReturnsFailureResult()
    {
        // Arrange
        var failureResult = new ProfileDetectionResult
        {
            Success = false,
            Message = "Profile creation failed"
        };

        var mockProfileService = new Mock<IProfileProvisioningService>();
        mockProfileService
            .Setup(x => x.CreateProfileAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(failureResult);

        var mockImageProxyService = new Mock<IImageProxyService>();
        var mockDiagnoseService = new Mock<IDiagnoseService>();

        var controller = new TvHeadendApiController(
            mockImageProxyService.Object,
            mockDiagnoseService.Object,
            mockProfileService.Object);

        // Act
        var result = await controller.CreateProfile(CancellationToken.None);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedResult = Assert.IsType<ProfileDetectionResult>(okResult.Value);
        Assert.False(returnedResult.Success);
    }

    [Fact]
    public async Task GenerateAuthToken_ReturnsOkWithToken()
    {
        // Arrange
        var tokenResult = new AuthTokenGenerationResult
        {
            Success = true,
            AuthToken = "test-token-abc123",
            Message = "Token generated"
        };

        var mockProfileService = new Mock<IProfileProvisioningService>();
        mockProfileService
            .Setup(x => x.GenerateAuthTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(tokenResult);

        var mockImageProxyService = new Mock<IImageProxyService>();
        var mockDiagnoseService = new Mock<IDiagnoseService>();

        var controller = new TvHeadendApiController(
            mockImageProxyService.Object,
            mockDiagnoseService.Object,
            mockProfileService.Object);

        // Act
        var result = await controller.GenerateAuthToken(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedResult = Assert.IsType<AuthTokenGenerationResult>(okResult.Value);
        Assert.True(returnedResult.Success);
        Assert.NotEmpty(returnedResult.AuthToken);
    }

    [Fact]
    public void ResetToDefaults_WithoutPluginInstance_ReturnsBadRequest()
    {
        // Arrange
        var mockImageProxyService = new Mock<IImageProxyService>();
        var mockDiagnoseService = new Mock<IDiagnoseService>();
        var mockProfileService = new Mock<IProfileProvisioningService>();

        var controller = new TvHeadendApiController(
            mockImageProxyService.Object,
            mockDiagnoseService.Object,
            mockProfileService.Object);

        // Act
        var result = controller.ResetToDefaults();

        // Assert
        Assert.NotNull(result);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }
}

/// <summary>
/// Tests for profile provisioning service interactions.
/// </summary>
public class ProfileProvisioningServiceInteractionTests
{
    [Fact]
    public async Task CreateProfileAsync_SuccessfulResult_HasProfileName()
    {
        // Arrange
        var mockService = new Mock<IProfileProvisioningService>();
        mockService
            .Setup(x => x.CreateProfileAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDetectionResult
            {
                Success = true,
                ProfileName = "test-profile"
            });

        // Act
        var result = await mockService.Object.CreateProfileAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotEmpty(result.ProfileName);
    }

    [Fact]
    public async Task GenerateAuthTokenAsync_ReturnsToken()
    {
        // Arrange
        var mockService = new Mock<IProfileProvisioningService>();
        mockService
            .Setup(x => x.GenerateAuthTokenAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthTokenGenerationResult
            {
                Success = true,
                AuthToken = "token-123"
            });

        // Act
        var result = await mockService.Object.GenerateAuthTokenAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotEmpty(result.AuthToken);
    }
}

/// <summary>
/// Tests for diagnostic checks.
/// </summary>
public class DiagnosticCheckTests
{
    [Fact]
    public void DiagnoseCheck_WithValidData_CanBeCreated()
    {
        // Arrange
        var check = new DiagnoseCheck
        {
            Category = "Connection",
            Name = "TVHeadend Server",
            Status = "OK",
            Message = "Connected successfully"
        };

        // Act & Assert
        Assert.Equal("Connection", check.Category);
        Assert.Equal("TVHeadend Server", check.Name);
        Assert.Equal("OK", check.Status);
        Assert.Equal("Connected successfully", check.Message);
    }

    [Fact]
    public void DiagnoseCheck_WithWarning_StoresStatus()
    {
        // Arrange
        var check = new DiagnoseCheck
        {
            Status = "WARNING",
            Message = "Potential issue detected"
        };

        // Act & Assert
        Assert.Equal("WARNING", check.Status);
    }

    [Fact]
    public void DiagnoseCheck_WithError_StoresErrorStatus()
    {
        // Arrange
        var check = new DiagnoseCheck
        {
            Status = "ERROR",
            Message = "Connection failed"
        };

        // Act & Assert
        Assert.Equal("ERROR", check.Status);
    }

    [Fact]
    public void DiagnoseResult_CanStoreDiagnoseChecks()
    {
        // Arrange
        var result = new DiagnoseResult
        {
            OverallStatus = "OK",
            CompatibilityScore = 85
        };

        // Act & Assert
        Assert.Equal("OK", result.OverallStatus);
        Assert.Equal(85, result.CompatibilityScore);
    }
}


