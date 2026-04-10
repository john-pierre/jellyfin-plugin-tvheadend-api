using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Infrastructure;
using Jellyfin.Plugin.TvHeadendApi.Service.Stream;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class DiagnosticServiceTests
{
    [Fact]
    public void Constructor_WithNullLogger_Throws()
    {
        var sutFactory = new Func<DiagnosticService>(() => new DiagnosticService(
            null!,
            Mock.Of<IServerConfigurationManager>(),
            Mock.Of<IEncodingOptionsReader>(),
            Mock.Of<IIdNodeService>(),
            Mock.Of<IProfileResolver>(),
            Mock.Of<IApiClient>()));

        Assert.Throws<ArgumentNullException>(sutFactory);
    }

    [Fact]
    public async Task DiagnoseAsync_WhenConfigurationMissing_ReturnsErrorWithZeroScore()
    {
        var sut = CreateSut(out var apiClient);
        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns((PluginConfiguration?)null);

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Equal("ERROR", result.OverallStatus);
        Assert.Equal(0, result.CompatibilityScore);
        Assert.Contains("configuration is not available", result.Connection, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiagnoseAsync_WhenHttpClientBuildFails_ReturnsErrorCheck()
    {
        var sut = CreateSut(out var apiClient);
        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(CreateConfig());
        apiClient.Setup(x => x.BuildHttpClient(It.IsAny<PluginConfiguration>())).Throws(new InvalidOperationException("boom"));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Equal("ERROR", result.OverallStatus);
        Assert.Equal(0, result.CompatibilityScore);
        Assert.Contains(result.Checks, check =>
            check.Category == "Connection" &&
            check.Name == "HTTP Client" &&
            check.Status == "ERROR");
    }

    [Fact]
    public async Task DiagnoseAsync_WhenServerInfoRequestFails_ReturnsConnectivityError()
    {
        var sut = CreateSut(out var apiClient);
        var config = CreateConfig();
        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient());
        apiClient.Setup(x => x.GetBaseUrl(config)).Returns("http://tvh");
        apiClient.Setup(x => x.GetWebRoot(config)).Returns("/");
        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.Is<string>(u => u.Contains("api/serverinfo", StringComparison.Ordinal)), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("unreachable"));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Equal("ERROR", result.OverallStatus);
        Assert.Equal(0, result.CompatibilityScore);
        Assert.Contains(result.Checks, check =>
            check.Category == "Connection" &&
            check.Name == "TVHeadend Connectivity" &&
            check.Status == "ERROR");
    }

    [Fact]
    public async Task DiagnoseAsync_HappyPath_ReturnsOkAndProfiles()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out var idNodeService, out var encodingReader);
        var config = CreateConfig();

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient());
        apiClient.Setup(x => x.GetBaseUrl(config)).Returns("http://tvh");
        apiClient.Setup(x => x.GetWebRoot(config)).Returns("/");
        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) => Task.FromResult(GetJsonForUrl(url)));

        streamResolver
            .Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference>
            {
                new("profile-1", "pass")
            });
        streamResolver
            .Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), "http://tvh", "/", "profile-1", "pass", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDetails(
                "profile-1",
                "pass",
                "profile",
                "mpegts",
                "mpegts",
                string.Empty,
                string.Empty,
                new List<string>(),
                new List<string>(),
                true));

        idNodeService
            .Setup(x => x.LoadDvrConfigsAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonDocument.Parse("""
                                            { "entries": [ { "name": "default" } ] }
                                            """));

        encodingReader
            .Setup(x => x.ReadFfmpegSettings(It.IsAny<IServerConfigurationManager>(), It.IsAny<Microsoft.Extensions.Logging.ILogger>()))
            .Returns(("1000000", "300000"));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Equal("OK", result.OverallStatus);
        Assert.Equal(100, result.CompatibilityScore);
        Assert.Contains(result.Checks, check => check.Category == "Connection" && check.Name == "TVHeadend Connectivity" && check.Status == "OK");
        Assert.Contains(result.AvailableStreamingProfiles, name => name == "pass");
        Assert.Contains(result.AvailableRecordingProfiles, name => name == "default");
    }

    [Fact]
    public async Task DiagnoseAsync_WhenStreamingProfileMissing_AddsErrorCheckAndWarningStatus()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out var idNodeService, out _);
        var config = CreateConfig();
        config.StreamingProfile = "missing-profile";

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient());
        apiClient.Setup(x => x.GetBaseUrl(config)).Returns("http://tvh");
        apiClient.Setup(x => x.GetWebRoot(config)).Returns("/");
        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) => Task.FromResult(GetJsonForUrl(url)));

        streamResolver
            .Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference>
            {
                new("profile-1", "pass")
            });

        idNodeService
            .Setup(x => x.LoadDvrConfigsAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonDocument.Parse("""
                                            { "entries": [ { "name": "default" } ] }
                                            """));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Equal("WARNING", result.OverallStatus);
        Assert.Contains(result.Checks, check =>
            check.Category == "Streaming" &&
            check.Name == "Profile Exists" &&
            check.Status == "ERROR");
    }

    [Fact]
    public async Task DiagnoseAsync_TranscodeProfile_UsesCodecProfileLookupForDeinterlace()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out var idNodeService, out _);
        var config = CreateConfig();

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient());
        apiClient.Setup(x => x.GetBaseUrl(config)).Returns("http://tvh");
        apiClient.Setup(x => x.GetWebRoot(config)).Returns("/");
        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/codec_profile/list", StringComparison.Ordinal))
                {
                    return Task.FromResult("""
                                           { "entries": [ { "uuid": "codec-1", "title": "H264 Main (libx264)" } ] }
                                           """);
                }

                return Task.FromResult(GetJsonForUrl(url));
            });

        streamResolver
            .Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("profile-1", "pass") });
        streamResolver
            .Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), "http://tvh", "/", "profile-1", "pass", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDetails(
                "profile-1",
                "pass",
                "profile-mpegts-transcode",
                "mpegts",
                "mpegts",
                "H264 Main",
                "AAC",
                new List<string>(),
                new List<string>(),
                null));

        idNodeService
            .Setup(x => x.LoadDvrConfigsAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonDocument.Parse("""
                                            { "entries": [ { "name": "default" } ] }
                                            """));
        idNodeService
            .Setup(x => x.LoadIdNodeByUuidAsync(It.IsAny<HttpClient>(), "http://tvh", "/", "codec-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonDocument.Parse("""
                                            { "entries": [ { "params": [ { "id": "deinterlace", "value": true } ] } ] }
                                            """));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, check =>
            check.Category == "Streaming" &&
            check.Name == "Deinterlacing" &&
            check.Status == "OK");
    }

    [Fact]
    public async Task DiagnoseAsync_TranscodeProfileWithoutDeinterlace_AddsWarningCheck()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out var idNodeService, out _);
        var config = CreateConfig();

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient());
        apiClient.Setup(x => x.GetBaseUrl(config)).Returns("http://tvh");
        apiClient.Setup(x => x.GetWebRoot(config)).Returns("/");
        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) => Task.FromResult(GetJsonForUrl(url)));

        streamResolver
            .Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("profile-1", "pass") });
        streamResolver
            .Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), "http://tvh", "/", "profile-1", "pass", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDetails(
                "profile-1",
                "pass",
                "profile-mpegts-transcode",
                "mpegts",
                "mpegts",
                "H264 Main",
                "AAC",
                new List<string>(),
                new List<string>(),
                false));

        idNodeService
            .Setup(x => x.LoadDvrConfigsAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonDocument.Parse("""
                                            { "entries": [ { "name": "default" } ] }
                                            """));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, check =>
            check.Category == "Streaming" &&
            check.Name == "Deinterlacing" &&
            check.Status == "WARNING");
    }

    [Fact]
    public async Task DiagnoseAsync_TranscodeProfile_CodecProfileListKeyValFallback_ResolvesDeinterlace()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out var idNodeService, out _);
        var config = CreateConfig();

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient());
        apiClient.Setup(x => x.GetBaseUrl(config)).Returns("http://tvh");
        apiClient.Setup(x => x.GetWebRoot(config)).Returns("/");
        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/codec_profile/list", StringComparison.Ordinal))
                {
                    return Task.FromResult("""
                                           { "entries": [ { "key": "codec-k1", "val": "H264 Main (libx264)" } ] }
                                           """);
                }

                return Task.FromResult(GetJsonForUrl(url));
            });

        streamResolver
            .Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("profile-1", "pass") });
        streamResolver
            .Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), "http://tvh", "/", "profile-1", "pass", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDetails(
                "profile-1",
                "pass",
                "profile-mpegts-transcode",
                "mpegts",
                "mpegts",
                "H264 Main",
                "AAC",
                new List<string>(),
                new List<string>(),
                null));

        idNodeService
            .Setup(x => x.LoadDvrConfigsAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonDocument.Parse("""
                                            { "entries": [ { "name": "default" } ] }
                                            """));
        idNodeService
            .Setup(x => x.LoadIdNodeByUuidAsync(It.IsAny<HttpClient>(), "http://tvh", "/", "codec-k1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonDocument.Parse("""
                                            { "entries": [ { "params": [ { "id": "deinterlace", "value": true } ] } ] }
                                            """));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, check => check.Category == "Streaming" && check.Name == "Deinterlacing" && check.Status == "OK");
    }

    [Fact]
    public async Task DiagnoseAsync_TranscodeProfile_WhenCodecIdNodeEntriesEmpty_DeinterlacingWarning()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out var idNodeService, out _);
        var config = CreateConfig();

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient());
        apiClient.Setup(x => x.GetBaseUrl(config)).Returns("http://tvh");
        apiClient.Setup(x => x.GetWebRoot(config)).Returns("/");
        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/codec_profile/list", StringComparison.Ordinal))
                {
                    return Task.FromResult("""
                                           { "entries": [ { "uuid": "codec-empty", "title": "H264 Main" } ] }
                                           """);
                }

                return Task.FromResult(GetJsonForUrl(url));
            });

        streamResolver
            .Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("profile-1", "pass") });
        streamResolver
            .Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), "http://tvh", "/", "profile-1", "pass", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDetails(
                "profile-1",
                "pass",
                "profile-mpegts-transcode",
                "mpegts",
                "mpegts",
                "H264 Main",
                "AAC",
                new List<string>(),
                new List<string>(),
                null));

        idNodeService
            .Setup(x => x.LoadDvrConfigsAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonDocument.Parse("""
                                            { "entries": [ { "name": "default" } ] }
                                            """));
        idNodeService
            .Setup(x => x.LoadIdNodeByUuidAsync(It.IsAny<HttpClient>(), "http://tvh", "/", "codec-empty", It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonDocument.Parse("""
                                            { "entries": [] }
                                            """));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, check => check.Category == "Streaming" && check.Name == "Deinterlacing" && check.Status == "WARNING");
    }

    [Fact]
    public async Task DiagnoseAsync_TranscodeProfile_WhenParamsMissDeinterlace_DeinterlacingWarning()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out var idNodeService, out _);
        var config = CreateConfig();

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient());
        apiClient.Setup(x => x.GetBaseUrl(config)).Returns("http://tvh");
        apiClient.Setup(x => x.GetWebRoot(config)).Returns("/");
        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/codec_profile/list", StringComparison.Ordinal))
                {
                    return Task.FromResult("""
                                           { "entries": [ { "uuid": "codec-nodeint", "title": "H264 Main" } ] }
                                           """);
                }

                return Task.FromResult(GetJsonForUrl(url));
            });

        streamResolver
            .Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("profile-1", "pass") });
        streamResolver
            .Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), "http://tvh", "/", "profile-1", "pass", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDetails(
                "profile-1",
                "pass",
                "profile-mpegts-transcode",
                "mpegts",
                "mpegts",
                "H264 Main",
                "AAC",
                new List<string>(),
                new List<string>(),
                null));

        idNodeService
            .Setup(x => x.LoadDvrConfigsAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonDocument.Parse("""
                                            { "entries": [ { "name": "default" } ] }
                                            """));
        idNodeService
            .Setup(x => x.LoadIdNodeByUuidAsync(It.IsAny<HttpClient>(), "http://tvh", "/", "codec-nodeint", It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonDocument.Parse("""
                                            { "entries": [ { "params": [ { "id": "other", "value": true } ] } ] }
                                            """));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, check => check.Category == "Streaming" && check.Name == "Deinterlacing" && check.Status == "WARNING");
    }

    [Fact]
    public async Task DiagnoseAsync_WithHighAnalyzeDurationAndBuffer_AddsPlaybackWarnings()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out var idNodeService, out _);
        var config = CreateConfig();
        config.AnalyzeDurationMs = 1500;
        config.BufferMs = 3000;
        config.StreamingProfile = string.Empty;

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient());
        apiClient.Setup(x => x.GetBaseUrl(config)).Returns("http://tvh");
        apiClient.Setup(x => x.GetWebRoot(config)).Returns("/");
        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/serverinfo", StringComparison.Ordinal))
                {
                    return Task.FromResult("""
                                           { "sw_version": "4.3", "api_version": 18, "name": "tvh" }
                                           """);
                }

                if (url.Contains("api/channel/grid", StringComparison.Ordinal))
                {
                    return Task.FromResult("""
                                           { "total": 0, "entries": [] }
                                           """);
                }

                return Task.FromResult(GetJsonForUrl(url));
            });

        streamResolver
            .Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference>());
        idNodeService
            .Setup(x => x.LoadDvrConfigsAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonDocument.Parse("""
                                            { "entries": [ { "name": "default" } ] }
                                            """));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, check => check.Category == "Playback" && check.Name == "AnalyzeDuration" && check.Status == "WARNING");
        Assert.Contains(result.Checks, check => check.Category == "Playback" && check.Name == "Buffer Size" && check.Status == "WARNING");
        Assert.Contains(result.Recommendations, text => text.Contains("Set a Streaming Profile", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiagnoseAsync_WithLegacyAnalyzeModeAndNoProbing_AddsLegacyRecommendation()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out var idNodeService, out _);
        var config = CreateConfig();
        config.SupportsProbing = false;
        config.AnalyzeDurationMs = 0;

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient());
        apiClient.Setup(x => x.GetBaseUrl(config)).Returns("http://tvh");
        apiClient.Setup(x => x.GetWebRoot(config)).Returns("/");
        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) => Task.FromResult(GetJsonForUrl(url)));

        streamResolver
            .Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("profile-1", "pass") });
        streamResolver
            .Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), "http://tvh", "/", "profile-1", "pass", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProfileDetails?)null);
        idNodeService
            .Setup(x => x.LoadDvrConfigsAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonDocument.Parse("""
                                            { "entries": [ { "name": "default" } ] }
                                            """));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Recommendations, text =>
            text.Contains("Probing is off and AnalyzeDuration is 0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiagnoseAsync_WithNonAlphanumericAuthToken_AddsAuthenticationError()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out var idNodeService, out _);
        var config = CreateConfig();
        config.AuthToken = "abc.def-123";

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.BuildHttpClient(config)).Returns(new HttpClient());
        apiClient.Setup(x => x.GetBaseUrl(config)).Returns("http://tvh");
        apiClient.Setup(x => x.GetWebRoot(config)).Returns("/");
        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) => Task.FromResult(GetJsonForUrl(url)));

        streamResolver
            .Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("profile-1", "pass") });
        idNodeService
            .Setup(x => x.LoadDvrConfigsAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonDocument.Parse("""
                                            { "entries": [ { "name": "default" } ] }
                                            """));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, check =>
            check.Category == "Authentication" &&
            check.Name == "Auth Token Format" &&
            check.Status == "ERROR");
    }

    private static string GetJsonForUrl(string url)
    {
        if (url.Contains("api/serverinfo", StringComparison.Ordinal))
        {
            return """
                   { "sw_version": "4.3", "api_version": 19, "name": "tvh" }
                   """;
        }

        if (url.Contains("api/channel/grid", StringComparison.Ordinal))
        {
            return """
                   { "total": 1, "entries": [ { "uuid": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" } ] }
                   """;
        }

        if (url.Contains("api/dvr/entry/grid", StringComparison.Ordinal))
        {
            return """
                   { "total": 1, "entries": [] }
                   """;
        }

        if (url.Contains("api/codec_profile/list", StringComparison.Ordinal))
        {
            return """
                   { "entries": [] }
                   """;
        }

        throw new InvalidOperationException($"Unexpected URL in test: {url}");
    }

    private static PluginConfiguration CreateConfig()
    {
        return new PluginConfiguration
        {
            Host = "tvh",
            Port = 9981,
            Webroot = "/",
            AllowAnonymousAccess = true,
            StreamingProfile = "pass",
            RecordingProfile = "default",
            EnableTvhDvr = true,
            AuthToken = "abc123",
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = false,
            AnalyzeDurationMs = 200,
            BufferMs = 0
        };
    }

    private static DiagnosticService CreateSut(
        out Mock<IApiClient> apiClient,
        out Mock<IProfileResolver> streamResolver,
        out Mock<IIdNodeService> idNodeService,
        out Mock<IEncodingOptionsReader> encodingReader)
    {
        apiClient = new Mock<IApiClient>(MockBehavior.Strict);
        streamResolver = new Mock<IProfileResolver>(MockBehavior.Strict);
        idNodeService = new Mock<IIdNodeService>(MockBehavior.Strict);
        encodingReader = new Mock<IEncodingOptionsReader>(MockBehavior.Strict);

        var serverConfigManager = new Mock<IServerConfigurationManager>();

        // Default no-op values; tests can override these setups.
        streamResolver
            .Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference>());
        streamResolver
            .Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProfileDetails?)null);
        idNodeService
            .Setup(x => x.LoadDvrConfigsAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(JsonDocument.Parse("{ \"entries\": [] }"));
        encodingReader
            .Setup(x => x.ReadFfmpegSettings(It.IsAny<IServerConfigurationManager>(), It.IsAny<Microsoft.Extensions.Logging.ILogger>()))
            .Returns((null, null));

        return new DiagnosticService(
            NullLogger<DiagnosticService>.Instance,
            serverConfigManager.Object,
            encodingReader.Object,
            idNodeService.Object,
            streamResolver.Object,
            apiClient.Object);
    }

    private static DiagnosticService CreateSut(out Mock<IApiClient> apiClient)
    {
        var sut = CreateSut(out apiClient, out _, out _, out _);
        return sut;
    }
}
