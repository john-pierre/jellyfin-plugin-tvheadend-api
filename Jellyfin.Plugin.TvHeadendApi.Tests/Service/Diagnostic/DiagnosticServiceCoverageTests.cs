using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Diagnostic;

/// <summary>
/// Coverage gap tests for DiagnosticService — targets uncovered branches in
/// FetchChannelGridAsync, CheckDvrProfilesAsync, CheckPlaybackSettings, CheckFfmpegSettings,
/// CheckProbeCacheStatus, and InspectStreamProfileDetailsAsync.
/// </summary>
public class DiagnosticServiceCoverageTests
{
    // ── Channel grid exception ──────────────────────────────────────────
    [Fact]
    public async Task DiagnoseAsync_ChannelGridThrows_AddsWarning()
    {
        var sut = CreateSut(out var api, out var resolver, out _, config: DefaultConfig());

        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/serverinfo")) return Task.FromResult(ServerInfoJson());
                if (url.Contains("api/channel/grid")) throw new HttpRequestException("ch fail");
                if (url.Contains("api/dvr/entry/grid")) return Task.FromResult("""{"total":0,"entries":[]}""");
                return Task.FromResult("{}");
            });

        resolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference>());

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Warnings, w => w.Contains("channel count"));
    }

    // ── DVR profiles with entries, matched ──────────────────────────────
    [Fact]
    public async Task DiagnoseAsync_DvrProfileMatched_ReturnsOkCheck()
    {
        var config = DefaultConfig();
        config.RecordingProfile = "Default DVR Profile";
        var sut = CreateSut(out var api, out var resolver, out _, config: config);

        SetupStandardApiResponses(api, resolver, dvrConfigJson: """{"entries":[{"key":"uuid-d","val":"Default DVR Profile"}]}""");

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Category == "Recording" && c.Status == "OK");
    }

    // ── DVR profile not matched ─────────────────────────────────────────
    [Fact]
    public async Task DiagnoseAsync_DvrProfileNotMatched_ReturnsWarning()
    {
        var config = DefaultConfig();
        config.RecordingProfile = "missing-profile";
        var sut = CreateSut(out var api, out var resolver, out _, config: config);

        SetupStandardApiResponses(api, resolver, dvrConfigJson: """{"entries":[{"key":"uuid-d","val":"Default DVR Profile"}]}""");

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Category == "Recording" && c.Status == "WARNING");
    }

    // ── DVR entries exception ───────────────────────────────────────────
    [Fact]
    public async Task DiagnoseAsync_DvrEntriesThrow_AddsWarning()
    {
        var config = DefaultConfig();
        var sut = CreateSut(out var api, out var resolver, out _, config: config);

        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/serverinfo")) return Task.FromResult(ServerInfoJson());
                if (url.Contains("api/channel/grid")) return Task.FromResult("""{"total":0,"entries":[]}""");
                if (url.Contains("api/dvr/entry/grid")) throw new HttpRequestException("dvr fail");
                return Task.FromResult("{}");
            });

        resolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference>());

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Warnings, w => w.Contains("DVR entries"));
    }

    // ── DVR config POST throws ──────────────────────────────────────────
    [Fact]
    public async Task DiagnoseAsync_DvrConfigPostThrows_AddsWarning()
    {
        var config = DefaultConfig();
        var sut = CreateSut(out var api, out var resolver, out _, config: config, dvrPostThrows: true);

        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/serverinfo")) return Task.FromResult(ServerInfoJson());
                if (url.Contains("api/channel/grid")) return Task.FromResult("""{"total":0,"entries":[]}""");
                if (url.Contains("api/dvr/entry/grid")) return Task.FromResult("""{"total":0,"entries":[]}""");
                return Task.FromResult("{}");
            });

        resolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference>());

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Warnings, w => w.Contains("DVR profiles"));
    }

    // ── AnalyzeDuration very high ───────────────────────────────────────
    [Fact]
    public async Task DiagnoseAsync_AnalyzeDurationVeryHigh_ReturnsWarning()
    {
        var config = DefaultConfig();
        config.AnalyzeDurationMs = 5000;
        var sut = CreateSut(out var api, out var resolver, out _, config: config);
        SetupStandardApiResponses(api, resolver);

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Name == "AnalyzeDuration" && c.Status == "WARNING" && c.Message.Contains("very high"));
    }

    // ── AnalyzeDuration very low ────────────────────────────────────────
    [Fact]
    public async Task DiagnoseAsync_AnalyzeDurationVeryLow_ReturnsWarning()
    {
        var config = DefaultConfig();
        config.AnalyzeDurationMs = 10;
        var sut = CreateSut(out var api, out var resolver, out _, config: config);
        SetupStandardApiResponses(api, resolver);

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Name == "AnalyzeDuration" && c.Status == "WARNING" && c.Message.Contains("very low"));
    }

    // ── BufferMs high ───────────────────────────────────────────────────
    [Fact]
    public async Task DiagnoseAsync_BufferMsHigh_ReturnsWarning()
    {
        var config = DefaultConfig();
        config.BufferMs = 5000;
        var sut = CreateSut(out var api, out var resolver, out _, config: config);
        SetupStandardApiResponses(api, resolver);

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Name == "Buffer Size" && c.Status == "WARNING");
    }

    // ── Probing off + AnalyzeDuration 0 → recommendation ───────────────
    [Fact]
    public async Task DiagnoseAsync_ProbingOffAnalyzeZero_AddsRecommendation()
    {
        var config = DefaultConfig();
        config.SupportsProbing = false;
        config.AnalyzeDurationMs = 0;
        var sut = CreateSut(out var api, out var resolver, out _, config: config);
        SetupStandardApiResponses(api, resolver);

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Recommendations, r => r.Contains("legacy auto mode"));
    }

    // ── No streaming profile set → recommendation ──────────────────────
    [Fact]
    public async Task DiagnoseAsync_NoStreamingProfile_AddsRecommendation()
    {
        var config = DefaultConfig();
        config.StreamingProfile = "";
        var sut = CreateSut(out var api, out var resolver, out _, config: config);
        SetupStandardApiResponses(api, resolver);

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Recommendations, r => r.Contains("Streaming Profile"));
    }

    // ── No channels → recommendation ───────────────────────────────────
    [Fact]
    public async Task DiagnoseAsync_NoChannels_AddsRecommendation()
    {
        var config = DefaultConfig();
        var sut = CreateSut(out var api, out var resolver, out _, config: config);

        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/serverinfo")) return Task.FromResult(ServerInfoJson());
                if (url.Contains("api/channel/grid")) return Task.FromResult("""{"total":0,"entries":[]}""");
                if (url.Contains("api/dvr/entry/grid")) return Task.FromResult("""{"total":0,"entries":[]}""");
                return Task.FromResult("{}");
            });

        resolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference>());

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Recommendations, r => r.Contains("No channels"));
    }

    // ── Streaming profile not found in TVHeadend ────────────────────────
    [Fact]
    public async Task DiagnoseAsync_StreamingProfileNotFound_ReturnsError()
    {
        var config = DefaultConfig();
        config.StreamingProfile = "nonexistent";
        var sut = CreateSut(out var api, out var resolver, out _, config: config);

        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/serverinfo")) return Task.FromResult(ServerInfoJson());
                if (url.Contains("api/channel/grid")) return Task.FromResult("""{"total":0,"entries":[]}""");
                if (url.Contains("api/dvr/entry/grid")) return Task.FromResult("""{"total":0,"entries":[]}""");
                return Task.FromResult("{}");
            });

        resolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("p1", "pass") });

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Category == "Streaming" && c.Name == "Profile Exists" && c.Status == "ERROR");
    }

    // ── Passthrough profile detected ────────────────────────────────────
    [Fact]
    public async Task DiagnoseAsync_PassthroughProfile_ReturnsInfoCheck()
    {
        var config = DefaultConfig();
        config.StreamingProfile = "pass";
        var sut = CreateSut(out var api, out var resolver, out _, config: config);

        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/serverinfo")) return Task.FromResult(ServerInfoJson());
                if (url.Contains("api/channel/grid")) return Task.FromResult("""{"total":1,"entries":[{"uuid":"aaa"}]}""");
                if (url.Contains("api/dvr/entry/grid")) return Task.FromResult("""{"total":0,"entries":[]}""");
                return Task.FromResult("{}");
            });

        resolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("p1", "pass") });
        resolver.Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), "p1", "pass", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDetails("p1", "pass", "profile-passthrough", null, null, null, null, new List<string>(), new List<string>(), null));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Name == "Profile Type" && c.Status == "INFO");
    }

    // ── Transcode profile with all codec links ─────────────────────────
    [Fact]
    public async Task DiagnoseAsync_TranscodeProfileWithCodecLinks_AllOk()
    {
        var config = DefaultConfig();
        config.StreamingProfile = "jellyfin";
        var sut = CreateSut(out var api, out var resolver, out _, config: config);

        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/serverinfo")) return Task.FromResult(ServerInfoJson());
                if (url.Contains("api/channel/grid")) return Task.FromResult("""{"total":1,"entries":[{"uuid":"aaa"}]}""");
                if (url.Contains("api/dvr/entry/grid")) return Task.FromResult("""{"total":0,"entries":[]}""");
                if (url.Contains("api/codec_profile/list"))
                    return Task.FromResult("""{"entries":[{"uuid":"c1","title":"jellyfin-h264 (libx264)"}]}""");
                if (url.Contains("api/idnode/load") && url.Contains("c1"))
                    return Task.FromResult("""{"entries":[{"deinterlace":true,"params":[]}]}""");
                return Task.FromResult("{}");
            });

        resolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("p1", "jellyfin") });
        resolver.Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), "p1", "jellyfin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDetails("p1", "jellyfin", "profile-transcode", "mpegts", "mpegts", "jellyfin-h264", "jellyfin-aac", new List<string>(), new List<string>(), true));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Name == "Profile Type" && c.Status == "OK");
        Assert.Contains(result.Checks, c => c.Name == "Codec Profiles" && c.Status == "OK");
        Assert.Contains(result.Checks, c => c.Name == "Deinterlacing" && c.Status == "OK");
    }

    // ── Transcode profile missing video codec ──────────────────────────
    [Fact]
    public async Task DiagnoseAsync_TranscodeProfileMissingVideoCodec_Warning()
    {
        var config = DefaultConfig();
        config.StreamingProfile = "jellyfin";
        var sut = CreateSut(out var api, out var resolver, out _, config: config);

        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/serverinfo")) return Task.FromResult(ServerInfoJson());
                if (url.Contains("api/channel/grid")) return Task.FromResult("""{"total":0,"entries":[]}""");
                if (url.Contains("api/dvr/entry/grid")) return Task.FromResult("""{"total":0,"entries":[]}""");
                return Task.FromResult("{}");
            });

        resolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("p1", "jellyfin") });
        resolver.Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), "p1", "jellyfin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDetails("p1", "jellyfin", "profile-transcode", "mpegts", "mpegts", null, null, new List<string>(), new List<string>(), false));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Name == "Video Codec Link" && c.Status == "WARNING");
        Assert.Contains(result.Checks, c => c.Name == "Audio Codec Link" && c.Status == "WARNING");
    }

    // ── Profile inspection exception ────────────────────────────────────
    [Fact]
    public async Task DiagnoseAsync_ProfileInspectionThrows_AddsWarning()
    {
        var config = DefaultConfig();
        config.StreamingProfile = "pass";
        var sut = CreateSut(out var api, out var resolver, out _, config: config);

        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/serverinfo")) return Task.FromResult(ServerInfoJson());
                if (url.Contains("api/channel/grid")) return Task.FromResult("""{"total":0,"entries":[]}""");
                if (url.Contains("api/dvr/entry/grid")) return Task.FromResult("""{"total":0,"entries":[]}""");
                return Task.FromResult("{}");
            });

        resolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("p1", "pass") });
        resolver.Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), "p1", "pass", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("inspect fail"));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Warnings, w => w.Contains("inspect stream profile"));
    }

    // ── Profile list exception ──────────────────────────────────────────
    [Fact]
    public async Task DiagnoseAsync_ProfileListThrows_AddsWarning()
    {
        var config = DefaultConfig();
        config.StreamingProfile = "pass";
        var sut = CreateSut(out var api, out var resolver, out _, config: config);

        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/serverinfo")) return Task.FromResult(ServerInfoJson());
                if (url.Contains("api/channel/grid")) return Task.FromResult("""{"total":0,"entries":[]}""");
                if (url.Contains("api/dvr/entry/grid")) return Task.FromResult("""{"total":0,"entries":[]}""");
                return Task.FromResult("{}");
            });

        resolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("profile list fail"));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Warnings, w => w.Contains("profile list"));
    }

    // ── FFmpeg global analyzeduration set → INFO check ──────────────────
    [Fact]
    public async Task DiagnoseAsync_FfmpegGlobalAnalyzeDuration_AddsInfoCheck()
    {
        var config = DefaultConfig();
        var sut = CreateSut(out var api, out var resolver, out var encodingReader, config: config);
        encodingReader.Setup(x => x.ReadFfmpegSettings(It.IsAny<IServerConfigurationManager>(), It.IsAny<ILogger>()))
            .Returns(("5000000", "3000000"));
        SetupStandardApiResponses(api, resolver);

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Category == "FFmpeg" && c.Status == "INFO");
    }

    // ── Auth token empty → ERROR check ──────────────────────────────────
    [Fact]
    public async Task DiagnoseAsync_EmptyAuthToken_ReturnsAuthError()
    {
        var config = DefaultConfig();
        config.AuthToken = "";
        var sut = CreateSut(out var api, out var resolver, out _, config: config);
        SetupStandardApiResponses(api, resolver);

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Category == "Authentication" && c.Status == "ERROR");
    }

    // ── Auth token invalid format → ERROR check ─────────────────────────
    [Fact]
    public async Task DiagnoseAsync_InvalidAuthToken_ReturnsAuthError()
    {
        var config = DefaultConfig();
        config.AuthToken = "abc-def.ghi";
        var sut = CreateSut(out var api, out var resolver, out _, config: config);
        SetupStandardApiResponses(api, resolver);

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Category == "Authentication" && c.Status == "ERROR" && c.Message.Contains("unsupported"));
    }

    // ── Old API version → WARNING ───────────────────────────────────────
    [Fact]
    public async Task DiagnoseAsync_OldApiVersion_ReturnsWarning()
    {
        var config = DefaultConfig();
        var sut = CreateSut(out var api, out var resolver, out _, config: config);

        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/serverinfo")) return Task.FromResult("""{"sw_version":"4.0","api_version":15,"name":"tvh"}""");
                if (url.Contains("api/channel/grid")) return Task.FromResult("""{"total":0,"entries":[]}""");
                if (url.Contains("api/dvr/entry/grid")) return Task.FromResult("""{"total":0,"entries":[]}""");
                return Task.FromResult("{}");
            });

        resolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference>());

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Name == "API Version" && c.Status == "WARNING");
    }

    // ── HTTP client creation throws ─────────────────────────────────────
    [Fact]
    public async Task DiagnoseAsync_HttpClientCreationThrows_ReturnsError()
    {
        var config = DefaultConfig();
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        api.Setup(x => x.CreateApiHttpClient(config)).Throws(new InvalidOperationException("no handler"));

        var urlBuilder = new Mock<IUrlBuilder>();
        var encodingReader = new Mock<IEncodingOptionsReader>();
        encodingReader.Setup(x => x.ReadFfmpegSettings(It.IsAny<IServerConfigurationManager>(), It.IsAny<ILogger>())).Returns((null, null));

        var sut = new DiagnosticService(
            NullLogger<DiagnosticService>.Instance,
            new Mock<IServerConfigurationManager>().Object,
            encodingReader.Object,
            new Mock<IProfileResolver>().Object,
            api.Object,
            urlBuilder.Object,
            new CachePathProvider(() => null));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Equal("ERROR", result.OverallStatus);
        Assert.Equal(0, result.CompatibilityScore);
    }

    // ── Probe cache with temp dir ───────────────────────────────────────
    [Fact]
    public async Task DiagnoseAsync_ProbeCacheChecksMediaInfoDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "diag_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            // Create a fake mediainfo directory with a cache file
            var mediaInfoDir = Path.Combine(tempDir, "mediainfo");
            Directory.CreateDirectory(mediaInfoDir);
            var cacheFile = Path.Combine(mediaInfoDir, "test.json");
            File.WriteAllText(cacheFile, """{"Path":"http://tvh:9981/stream/channel/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""");

            var config = DefaultConfig();
            var sut = CreateSut(out var api, out var resolver, out _, config: config, cachePath: tempDir);

            api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
                {
                    if (url.Contains("api/serverinfo")) return Task.FromResult(ServerInfoJson());
                    if (url.Contains("api/channel/grid")) return Task.FromResult("""{"total":1,"entries":[{"uuid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}""");
                    if (url.Contains("api/dvr/entry/grid")) return Task.FromResult("""{"total":0,"entries":[]}""");
                    return Task.FromResult("{}");
                });

            resolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<ProfileReference>());

            var result = await sut.DiagnoseAsync(CancellationToken.None);

            Assert.Contains(result.Checks, c => c.Category == "Cache" && c.Name == "Probe Cache Coverage");
            Assert.Contains("1/1", result.CacheStatus);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // ── Probe cache dir does not exist ──────────────────────────────────
    [Fact]
    public async Task DiagnoseAsync_ProbeCacheDirMissing_InfoStatus()
    {
        var config = DefaultConfig();
        var sut = CreateSut(out var api, out var resolver, out _, config: config, cachePath: Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid().ToString("N")));

        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/serverinfo")) return Task.FromResult(ServerInfoJson());
                if (url.Contains("api/channel/grid")) return Task.FromResult("""{"total":1,"entries":[{"uuid":"aaa"}]}""");
                if (url.Contains("api/dvr/entry/grid")) return Task.FromResult("""{"total":0,"entries":[]}""");
                return Task.FromResult("{}");
            });

        resolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference>());

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Category == "Cache" && c.Status == "INFO");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static PluginConfiguration DefaultConfig() => new()
    {
        Host = "tvh", Port = 9981, Webroot = "/", AllowAnonymousAccess = true,
        StreamingProfile = "pass", RecordingProfile = "", AuthToken = "abc123",
        AnalyzeDurationMs = 200, BufferMs = 0,
    };

    private static string ServerInfoJson() => """{"sw_version":"4.3","api_version":19,"name":"tvh"}""";

    private static void SetupStandardApiResponses(Mock<IApiClient> api, Mock<IProfileResolver> resolver, string? dvrConfigJson = null)
    {
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/serverinfo")) return Task.FromResult(ServerInfoJson());
                if (url.Contains("api/channel/grid")) return Task.FromResult("""{"total":1,"entries":[{"uuid":"aaa"}]}""");
                if (url.Contains("api/dvr/entry/grid")) return Task.FromResult("""{"total":0,"entries":[]}""");
                return Task.FromResult("{}");
            });

        resolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("p1", "pass") });
        resolver.Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), "p1", "pass", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProfileDetails?)null);

        if (dvrConfigJson != null)
        {
            api.Setup(x => x.PostFormAsync(It.IsAny<HttpClient>(), It.Is<string>(u => u.Contains("idnode/load")),
                    It.IsAny<IEnumerable<KeyValuePair<string, string>>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(dvrConfigJson) });
        }
    }

    private static DiagnosticService CreateSut(
        out Mock<IApiClient> apiClient,
        out Mock<IProfileResolver> resolver,
        out Mock<IEncodingOptionsReader> encodingReader,
        PluginConfiguration config,
        string? cachePath = null,
        bool dvrPostThrows = false)
    {
        apiClient = new Mock<IApiClient>();
        resolver = new Mock<IProfileResolver>();
        encodingReader = new Mock<IEncodingOptionsReader>();

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());

        if (dvrPostThrows)
        {
            apiClient.Setup(x => x.PostFormAsync(It.IsAny<HttpClient>(), It.Is<string>(u => u.Contains("idnode/load")),
                    It.IsAny<IEnumerable<KeyValuePair<string, string>>>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("dvr post fail"));
        }
        else
        {
            apiClient.Setup(x => x.PostFormAsync(It.IsAny<HttpClient>(), It.Is<string>(u => u.Contains("idnode/load")),
                    It.IsAny<IEnumerable<KeyValuePair<string, string>>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"entries":[]}""") });
        }

        encodingReader.Setup(x => x.ReadFfmpegSettings(It.IsAny<IServerConfigurationManager>(), It.IsAny<ILogger>()))
            .Returns((null, null));

        var urlBuilder = new Mock<IUrlBuilder>();
        urlBuilder.Setup(u => u.GetBaseUrl(It.IsAny<PluginConfiguration>())).Returns("http://tvh");
        urlBuilder.Setup(u => u.GetWebRoot(It.IsAny<PluginConfiguration>())).Returns("/");
        urlBuilder.Setup(u => u.BuildApiUrl(It.IsAny<PluginConfiguration>(), It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, ep) => "http://tvh/" + ep.TrimStart('/'));

        return new DiagnosticService(
            NullLogger<DiagnosticService>.Instance,
            new Mock<IServerConfigurationManager>().Object,
            encodingReader.Object,
            resolver.Object,
            apiClient.Object,
            urlBuilder.Object,
            new CachePathProvider(() => cachePath));
    }
}

