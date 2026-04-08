using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Api;
using Jellyfin.Plugin.TvHeadendApi.Model;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostics;
using Jellyfin.Plugin.TvHeadendApi.Service.Profiles;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class TvHeadendApiControllerTests
{
    [Fact]
    public async Task Diagnose_ReturnsOkWithPayload()
    {
        var expected = new DiagnoseResult { OverallStatus = "OK", CompatibilityScore = 100 };
        var sut = new TvHeadendApiController(
            new FakeDiagnoseService(expected),
            new FakeProfileProvisioningService());

        var result = await sut.Diagnose(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<DiagnoseResult>(ok.Value);
        Assert.Equal("OK", payload.OverallStatus);
    }

    [Fact]
    public async Task CreateProfile_ReturnsOkWithPayload()
    {
        var sut = new TvHeadendApiController(
            new FakeDiagnoseService(new DiagnoseResult()),
            new FakeProfileProvisioningService());

        var result = await sut.CreateProfile(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<ProfileDetectionResult>(ok.Value);
        Assert.True(payload.Success);
    }

    [Fact]
    public void ResetToDefaults_WhenPluginInstanceUnavailable_ReturnsBadRequest()
    {
        var sut = new TvHeadendApiController(
            new FakeDiagnoseService(new DiagnoseResult()),
            new FakeProfileProvisioningService());

        var result = sut.ResetToDefaults();

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }


    private sealed class FakeDiagnoseService : IDiagnoseService
    {
        private readonly DiagnoseResult _result;

        public FakeDiagnoseService(DiagnoseResult result)
        {
            _result = result;
        }

        public Task<DiagnoseResult> DiagnoseAsync(CancellationToken cancellationToken)
            => Task.FromResult(_result);
    }

    private sealed class FakeProfileProvisioningService : IProfileProvisioningService
    {
        public Task<ProfileDetectionResult> CreateProfileAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new ProfileDetectionResult
            {
                Success = true,
                Message = "ok",
                ProfileName = "jellyfin",
            });
        }

        public Task<AuthTokenGenerationResult> GenerateAuthTokenAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new AuthTokenGenerationResult
            {
                Success = true,
                Message = "Token generated",
                AuthToken = "test-token-abc123",
            });
        }
    }
}
