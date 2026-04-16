using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Api;
using Jellyfin.Plugin.TvHeadendApi.Model.Auth;
using Jellyfin.Plugin.TvHeadendApi.Model.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Model.Statistics;
using Jellyfin.Plugin.TvHeadendApi.Service.Auth;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Statistics;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class PluginControllerTests
{
    [Fact]
    public async Task Diagnose_ReturnsOkWithPayload()
    {
        var expected = new DiagnoseResult { OverallStatus = "OK", CompatibilityScore = 100 };
        var sut = new PluginController(
            new FakeDiagnosticService(expected),
            new FakeDefaultProfileService(),
            new FakeTokenService(),
            new FakeStatisticsService());

        var result = await sut.Diagnose(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<DiagnoseResult>(ok.Value);
        Assert.Equal("OK", payload.OverallStatus);
    }

    [Fact]
    public async Task CreateProfile_ReturnsOkWithPayload()
    {
        var sut = new PluginController(
            new FakeDiagnosticService(new DiagnoseResult()),
            new FakeDefaultProfileService(),
            new FakeTokenService(),
            new FakeStatisticsService());

        var result = await sut.CreateProfile(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<ProfileDetectionResult>(ok.Value);
        Assert.True(payload.Success);
    }

    [Fact]
    public async Task GetProfileOptions_ReturnsStreamingAndRecordingProfiles()
    {
        var diagnose = new DiagnoseResult { OverallStatus = "OK" };
        diagnose.AvailableStreamingProfiles.Add("pass");
        diagnose.AvailableStreamingProfiles.Add("jellyfin");
        diagnose.AvailableRecordingProfiles.Add("default");

        var sut = new PluginController(
            new FakeDiagnosticService(diagnose),
            new FakeDefaultProfileService(),
            new FakeTokenService(),
            new FakeStatisticsService());

        var result = await sut.GetProfileOptions(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<ProfileOptionsResult>(ok.Value);
        Assert.Contains("pass", payload.StreamingProfiles);
        Assert.Contains("jellyfin", payload.StreamingProfiles);
        Assert.Contains("default", payload.RecordingProfiles);
    }

    [Fact]
    public void ResetToDefaults_WhenPluginInstanceUnavailable_ReturnsBadRequest()
    {
        var sut = new PluginController(
            new FakeDiagnosticService(new DiagnoseResult()),
            new FakeDefaultProfileService(),
            new FakeTokenService(),
            new FakeStatisticsService());

        var result = sut.ResetToDefaults();

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }


    private sealed class FakeDiagnosticService : IDiagnosticService
    {
        private readonly DiagnoseResult _result;

        public FakeDiagnosticService(DiagnoseResult result)
        {
            _result = result;
        }

        public Task<DiagnoseResult> DiagnoseAsync(CancellationToken cancellationToken)
            => Task.FromResult(_result);
    }

    private sealed class FakeDefaultProfileService : IDefaultProfileService
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

    }

    private sealed class FakeTokenService : ITokenService
    {
        public Task<AuthTokenGenerationResult> GenerateValidTokenAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new AuthTokenGenerationResult
            {
                Success = true,
                Message = "Token generated",
                AuthToken = "test-token-abc123",
            });
        }

        public Task<AuthTokenGenerationResult> GenerateAndStoreTokenAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new AuthTokenGenerationResult
            {
                Success = true,
                Message = "Token generated",
                AuthToken = "test-token-abc123",
            });
        }
    }

    private sealed class FakeStatisticsService : IStatisticsService
    {
        public IReadOnlyList<ViewingSession> AllSessions => [];

        public ViewingStatisticsResult GetStatistics(int days) => new();

        public void ClearStatistics()
        {
        }
    }
}
