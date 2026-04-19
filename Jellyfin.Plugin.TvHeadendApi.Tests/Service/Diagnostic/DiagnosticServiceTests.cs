using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
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
            Mock.Of<IProfileResolver>(),
            Mock.Of<IApiClient>(),
            Mock.Of<IUrlBuilder>(),
            new CachePathProvider(() => null)));

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
        apiClient.Setup(x => x.CreateApiHttpClient(It.IsAny<PluginConfiguration>())).Throws(new InvalidOperationException("boom"));

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
        apiClient.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());


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
        var sut = CreateSut(out var apiClient, out var streamResolver, out var encodingReader);
        var config = CreateConfig();

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());


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

        apiClient
            .Setup(x => x.PostFormAsync(
                It.IsAny<HttpClient>(),
                It.Is<string>(u => u.Contains("idnode/load", StringComparison.Ordinal)),
                It.IsAny<IEnumerable<KeyValuePair<string, string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{ \"entries\": [ { \"name\": \"default\" } ] }"),
            });

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
        var sut = CreateSut(out var apiClient, out var streamResolver, out _);
        var config = CreateConfig();
        config.StreamingProfile = "missing-profile";

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());


        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) => Task.FromResult(GetJsonForUrl(url)));

        streamResolver
            .Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference>
            {
                new("profile-1", "pass")
            });

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
        var sut = CreateSut(out var apiClient, out var streamResolver, out _);
        var config = CreateConfig();

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());


        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/codec_profile/list", StringComparison.Ordinal))
                {
                    return Task.FromResult("""
                                           { "entries": [ { "uuid": "codec-1", "title": "H264 Main (libx264)" } ] }
                                           """);
                }

                if (url.Contains("api/idnode/load", StringComparison.Ordinal) && url.Contains("codec-1", StringComparison.Ordinal))
                {
                    return Task.FromResult("""
                                           { "entries": [ { "params": [ { "id": "deinterlace", "value": true } ] } ] }
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

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, check =>
            check.Category == "Streaming" &&
            check.Name == "Deinterlacing" &&
            check.Status == "OK");
    }

    [Fact]
    public async Task DiagnoseAsync_TranscodeProfileWithoutDeinterlace_AddsWarningCheck()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out _);
        var config = CreateConfig();

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());


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

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, check =>
            check.Category == "Streaming" &&
            check.Name == "Deinterlacing" &&
            check.Status == "WARNING");
    }

    [Fact]
    public async Task DiagnoseAsync_TranscodeProfile_CodecProfileListKeyValFallback_ResolvesDeinterlace()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out _);
        var config = CreateConfig();

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());


        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/codec_profile/list", StringComparison.Ordinal))
                {
                    return Task.FromResult("""
                                           { "entries": [ { "key": "codec-k1", "val": "H264 Main (libx264)" } ] }
                                           """);
                }

                if (url.Contains("api/idnode/load", StringComparison.Ordinal) && url.Contains("codec-k1", StringComparison.Ordinal))
                {
                    return Task.FromResult("""
                                           { "entries": [ { "params": [ { "id": "deinterlace", "value": true } ] } ] }
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

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, check => check.Category == "Streaming" && check.Name == "Deinterlacing" && check.Status == "OK");
    }

    [Fact]
    public async Task DiagnoseAsync_TranscodeProfile_WhenCodecIdNodeEntriesEmpty_DeinterlacingWarning()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out _);
        var config = CreateConfig();

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());


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

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, check => check.Category == "Streaming" && check.Name == "Deinterlacing" && check.Status == "WARNING");
    }

    [Fact]
    public async Task DiagnoseAsync_TranscodeProfile_WhenParamsMissDeinterlace_DeinterlacingWarning()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out _);
        var config = CreateConfig();

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());


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

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, check => check.Category == "Streaming" && check.Name == "Deinterlacing" && check.Status == "WARNING");
    }

    [Fact]
    public async Task DiagnoseAsync_WithHighAnalyzeDurationAndBuffer_AddsPlaybackWarnings()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out _);
        var config = CreateConfig();
        config.AnalyzeDurationMs = 1500;
        config.BufferMs = 3000;
        config.StreamingProfile = string.Empty;

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());


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

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, check => check.Category == "Playback" && check.Name == "AnalyzeDuration" && check.Status == "WARNING");
        Assert.Contains(result.Checks, check => check.Category == "Playback" && check.Name == "Buffer Size" && check.Status == "WARNING");
        Assert.Contains(result.Recommendations, text => text.Contains("Set a Streaming Profile", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiagnoseAsync_WithLegacyAnalyzeModeAndNoProbing_AddsLegacyRecommendation()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out _);
        var config = CreateConfig();
        config.SupportsProbing = false;
        config.AnalyzeDurationMs = 0;

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());


        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) => Task.FromResult(GetJsonForUrl(url)));

        streamResolver
            .Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("profile-1", "pass") });
        streamResolver
            .Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), "http://tvh", "/", "profile-1", "pass", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProfileDetails?)null);

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Recommendations, text =>
            text.Contains("Probing is off and AnalyzeDuration is 0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiagnoseAsync_WithNonAlphanumericAuthToken_AddsAuthenticationError()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out _);
        var config = CreateConfig();
        config.AuthToken = "abc_def-123";

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());


        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) => Task.FromResult(GetJsonForUrl(url)));

        streamResolver
            .Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("profile-1", "pass") });

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, check =>
            check.Category == "Authentication" &&
            check.Name == "Auth Token Format" &&
            check.Status == "ERROR");
    }

    [Fact]
    public async Task DiagnoseAsync_WithEmptyAuthToken_AddsAuthTokenError()
    {
        var sut = CreateSut(out var apiClient, out _, out _);
        var config = CreateConfig();
        config.AuthToken = string.Empty;

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());


        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) => Task.FromResult(GetJsonForUrl(url)));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, check =>
            check.Category == "Authentication" &&
            check.Name == "Auth Token Format" &&
            check.Status == "ERROR" &&
            check.Message!.Contains("empty", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DiagnoseAsync_WithOldApiVersion_AddsApiVersionWarning()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out _);
        var config = CreateConfig();

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());


        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/serverinfo", StringComparison.Ordinal))
                    return Task.FromResult("""{ "sw_version": "4.2", "api_version": 10, "name": "tvh" }""");
                return Task.FromResult(GetJsonForUrl(url));
            });
        streamResolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference>());

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, check =>
            check.Category == "Connection" &&
            check.Name == "API Version" &&
            check.Status == "WARNING");
    }

    [Fact]
    public async Task DiagnoseAsync_WhenJellyfinAnalyzeDurationSet_AddsFFmpegInfoCheck()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out var encodingReader);
        var config = CreateConfig();

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());


        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) => Task.FromResult(GetJsonForUrl(url)));
        streamResolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference>());
        encodingReader
            .Setup(x => x.ReadFfmpegSettings(It.IsAny<IServerConfigurationManager>(), It.IsAny<Microsoft.Extensions.Logging.ILogger>()))
            .Returns((null, "5000000"));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, check =>
            check.Category == "FFmpeg" &&
            check.Name == "AnalyzeDuration (global)" &&
            check.Status == "INFO");
    }

    [Fact]
    public async Task DiagnoseAsync_WithVeryLowAnalyzeDuration_AddsLowDurationWarning()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out _);
        var config = CreateConfig();
        config.AnalyzeDurationMs = 20;

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());


        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) => Task.FromResult(GetJsonForUrl(url)));
        streamResolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference>());

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, check =>
            check.Category == "Playback" &&
            check.Name == "AnalyzeDuration" &&
            check.Status == "WARNING" &&
            check.Message!.Contains("very low", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DiagnoseAsync_TranscodeProfile_WithBothCodecProfilesLinked_AddsOkCheck()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out _);
        var config = CreateConfig();

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());


        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) => Task.FromResult(GetJsonForUrl(url)));
        streamResolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("profile-1", "pass") });
        streamResolver.Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), "http://tvh", "/", "profile-1", "pass", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDetails("profile-1", "pass", "profile-mpegts-transcode", "mpegts", "mpegts", "h264-codec", "aac-codec", new List<string>(), new List<string>(), true));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, check =>
            check.Category == "Streaming" &&
            check.Name == "Codec Profiles" &&
            check.Status == "OK");
    }

    [Fact]
    public async Task DiagnoseAsync_TranscodeProfile_WithMissingVideoCodec_AddsVideoCodecWarning()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out _);
        var config = CreateConfig();

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());


        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) => Task.FromResult(GetJsonForUrl(url)));
        streamResolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("profile-1", "pass") });
        streamResolver.Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), "http://tvh", "/", "profile-1", "pass", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDetails("profile-1", "pass", "profile-mpegts-transcode", "mpegts", "mpegts", string.Empty, "aac-codec", new List<string>(), new List<string>(), true));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, check =>
            check.Category == "Streaming" &&
            check.Name == "Video Codec Link" &&
            check.Status == "WARNING");
    }

    [Fact]
    public async Task DiagnoseAsync_TranscodeProfile_WithEmptySrcCodecFilters_AddsInfoChecks()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out _);
        var config = CreateConfig();

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());


        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) => Task.FromResult(GetJsonForUrl(url)));
        streamResolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("profile-1", "pass") });
        streamResolver.Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), "http://tvh", "/", "profile-1", "pass", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDetails("profile-1", "pass", "profile-mpegts-transcode", "mpegts", "mpegts", "h264", "aac", new List<string>(), new List<string>(), true));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, check =>
            check.Category == "Streaming" &&
            check.Name == "Source Video Codecs" &&
            check.Status == "INFO");
        Assert.Contains(result.Checks, check =>
            check.Category == "Streaming" &&
            check.Name == "Source Audio Codecs" &&
            check.Status == "INFO");
    }

    [Fact]
    public async Task DiagnoseAsync_DvrProfile_WhenConfiguredAndExists_AddsOkCheck()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out _);
        var config = CreateConfig();
        config.RecordingProfile = "myprofile";

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());


        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) => Task.FromResult(GetJsonForUrl(url)));
        streamResolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference>());
        apiClient.Setup(x => x.PostFormAsync(
                It.IsAny<HttpClient>(),
                It.Is<string>(u => u.Contains("idnode/load", StringComparison.Ordinal)),
                It.IsAny<IEnumerable<KeyValuePair<string, string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{ \"entries\": [ { \"name\": \"myprofile\" } ] }"),
            });

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, check =>
            check.Category == "Recording" &&
            check.Name == "DVR Profile" &&
            check.Status == "OK");
    }

    [Fact]
    public async Task DiagnoseAsync_DvrProfile_WhenConfiguredButMissing_AddsWarning()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver, out _);
        var config = CreateConfig();
        config.RecordingProfile = "missing-dvr-profile";

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());


        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) => Task.FromResult(GetJsonForUrl(url)));
        streamResolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference>());
        apiClient.Setup(x => x.PostFormAsync(
                It.IsAny<HttpClient>(),
                It.Is<string>(u => u.Contains("idnode/load", StringComparison.Ordinal)),
                It.IsAny<IEnumerable<KeyValuePair<string, string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{ \"entries\": [ { \"name\": \"other-profile\" } ] }"),
            });

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, check =>
            check.Category == "Recording" &&
            check.Name == "DVR Profile" &&
            check.Status == "WARNING");
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

        if (url.Contains("api/idnode/load", StringComparison.Ordinal))
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
        out Mock<IEncodingOptionsReader> encodingReader)
    {
        apiClient = new Mock<IApiClient>(MockBehavior.Strict);
        streamResolver = new Mock<IProfileResolver>(MockBehavior.Strict);
        encodingReader = new Mock<IEncodingOptionsReader>(MockBehavior.Strict);

        var serverConfigManager = new Mock<IServerConfigurationManager>();
        var urlBuilder = new Mock<IUrlBuilder>();
        urlBuilder.Setup(x => x.GetBaseUrl(It.IsAny<PluginConfiguration>())).Returns("http://tvh");
        urlBuilder.Setup(x => x.GetWebRoot(It.IsAny<PluginConfiguration>())).Returns("/");
        urlBuilder.Setup(x => x.BuildApiUrl(It.IsAny<PluginConfiguration>(), It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, ep) => $"http://tvh/{ep.TrimStart('/')}");

        // Default no-op values; tests can override these setups.
        streamResolver
            .Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference>());
        streamResolver
            .Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProfileDetails?)null);

        // Default: DVR config POST returns empty entries.
        apiClient
            .Setup(x => x.PostFormAsync(
                It.IsAny<HttpClient>(),
                It.Is<string>(u => u.Contains("idnode/load", StringComparison.Ordinal)),
                It.IsAny<IEnumerable<KeyValuePair<string, string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{ \"entries\": [] }"),
            });

        encodingReader
            .Setup(x => x.ReadFfmpegSettings(It.IsAny<IServerConfigurationManager>(), It.IsAny<Microsoft.Extensions.Logging.ILogger>()))
            .Returns((null, null));

        return new DiagnosticService(
            NullLogger<DiagnosticService>.Instance,
            serverConfigManager.Object,
            encodingReader.Object,
            streamResolver.Object,
            apiClient.Object,
            urlBuilder.Object,
            new CachePathProvider(() => null));
    }

    private static DiagnosticService CreateSut(out Mock<IApiClient> apiClient)
    {
        var sut = CreateSut(out apiClient, out _, out _);
        return sut;
    }
}
