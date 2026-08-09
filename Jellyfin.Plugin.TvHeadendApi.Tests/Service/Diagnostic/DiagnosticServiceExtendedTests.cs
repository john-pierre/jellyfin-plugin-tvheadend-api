using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Jellyfin.Plugin.TvHeadendApi.Service.Storage;
using Jellyfin.Plugin.TvHeadendApi.Service.Stream;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

/// <summary>
/// Extended DiagnosticService tests covering GetCodecProfileBoolSettingAsync branches,
/// ReadBool string variants, and GetIdNodeProperty switch cases.
/// </summary>
public class DiagnosticServiceExtendedTests
{
    /// <summary>
    /// When codec_profile/list returns empty entries, GetCodecProfileBoolSettingAsync returns null,
    /// so deinterlace falls back to the profile-level value (false → WARNING).
    /// </summary>
    [Fact]
    public async Task DiagnoseAsync_CodecProfileListEmpty_DeinterlaceWarning()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver);

        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/serverinfo")) return Task.FromResult(ServerInfoJson());
                if (url.Contains("api/channel/grid")) return Task.FromResult(ChannelGridJson());
                if (url.Contains("api/dvr/entry/grid")) return Task.FromResult(DvrGridJson());
                if (url.Contains("api/codec_profile/list")) return Task.FromResult("""{"entries":[]}""");
                return Task.FromResult("{}");
            });

        streamResolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("p1", "pass") });
        streamResolver.Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), "http://tvh", "/", "p1", "pass", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDetails("p1", "pass", "profile-transcode", "mpegts", "mpegts", "mycodec", "aac", new List<string>(), new List<string>(), null));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Name == "Deinterlacing" && c.Status == "WARNING");
    }

    /// <summary>
    /// When idnode/load for codec profile returns empty entries, deinterlace remains null → WARNING.
    /// </summary>
    [Fact]
    public async Task DiagnoseAsync_CodecIdNodeEntriesEmpty_DeinterlaceWarning()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver);

        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/serverinfo")) return Task.FromResult(ServerInfoJson());
                if (url.Contains("api/channel/grid")) return Task.FromResult(ChannelGridJson());
                if (url.Contains("api/dvr/entry/grid")) return Task.FromResult(DvrGridJson());
                if (url.Contains("api/codec_profile/list"))
                    return Task.FromResult("""{"entries":[{"uuid":"c1","title":"mycodec (libx264)"}]}""");
                if (url.Contains("api/idnode/load") && url.Contains("c1"))
                    return Task.FromResult("""{"entries":[]}""");
                return Task.FromResult("{}");
            });

        streamResolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("p1", "pass") });
        streamResolver.Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), "http://tvh", "/", "p1", "pass", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDetails("p1", "pass", "profile-transcode", "mpegts", "mpegts", "mycodec", "aac", new List<string>(), new List<string>(), null));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Name == "Deinterlacing" && c.Status == "WARNING");
    }

    /// <summary>
    /// ReadBool handles string "1" as true → deinterlace OK.
    /// </summary>
    [Fact]
    public async Task DiagnoseAsync_DeinterlaceParamStringOne_ResolvesAsTrue()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver);

        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/serverinfo")) return Task.FromResult(ServerInfoJson());
                if (url.Contains("api/channel/grid")) return Task.FromResult(ChannelGridJson());
                if (url.Contains("api/dvr/entry/grid")) return Task.FromResult(DvrGridJson());
                if (url.Contains("api/codec_profile/list"))
                    return Task.FromResult("""{"entries":[{"uuid":"c1","title":"mycodec (libx264)"}]}""");
                if (url.Contains("api/idnode/load") && url.Contains("c1"))
                    return Task.FromResult("""{"entries":[{"params":[{"id":"deinterlace","value":"1"}]}]}""");
                return Task.FromResult("{}");
            });

        streamResolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("p1", "pass") });
        streamResolver.Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), "http://tvh", "/", "p1", "pass", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDetails("p1", "pass", "profile-transcode", "mpegts", "mpegts", "mycodec", "aac", new List<string>(), new List<string>(), null));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Name == "Deinterlacing" && c.Status == "OK");
    }

    /// <summary>
    /// ReadBool handles string "0" as false → deinterlace WARNING.
    /// </summary>
    [Fact]
    public async Task DiagnoseAsync_DeinterlaceParamStringZero_ResolvesAsFalse()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver);

        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/serverinfo")) return Task.FromResult(ServerInfoJson());
                if (url.Contains("api/channel/grid")) return Task.FromResult(ChannelGridJson());
                if (url.Contains("api/dvr/entry/grid")) return Task.FromResult(DvrGridJson());
                if (url.Contains("api/codec_profile/list"))
                    return Task.FromResult("""{"entries":[{"uuid":"c1","title":"mycodec"}]}""");
                if (url.Contains("api/idnode/load") && url.Contains("c1"))
                    return Task.FromResult("""{"entries":[{"params":[{"id":"deinterlace","value":"0"}]}]}""");
                return Task.FromResult("{}");
            });

        streamResolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("p1", "pass") });
        streamResolver.Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), "http://tvh", "/", "p1", "pass", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDetails("p1", "pass", "profile-transcode", "mpegts", "mpegts", "mycodec", "aac", new List<string>(), new List<string>(), null));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Name == "Deinterlacing" && c.Status == "WARNING");
    }

    /// <summary>
    /// ReadBool handles string "true" as true → deinterlace OK.
    /// </summary>
    [Fact]
    public async Task DiagnoseAsync_DeinterlaceParamStringTrue_ResolvesAsTrue()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver);

        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/serverinfo")) return Task.FromResult(ServerInfoJson());
                if (url.Contains("api/channel/grid")) return Task.FromResult(ChannelGridJson());
                if (url.Contains("api/dvr/entry/grid")) return Task.FromResult(DvrGridJson());
                if (url.Contains("api/codec_profile/list"))
                    return Task.FromResult("""{"entries":[{"uuid":"c1","title":"mycodec"}]}""");
                if (url.Contains("api/idnode/load") && url.Contains("c1"))
                    return Task.FromResult("""{"entries":[{"params":[{"id":"deinterlace","value":"true"}]}]}""");
                return Task.FromResult("{}");
            });

        streamResolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("p1", "pass") });
        streamResolver.Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), "http://tvh", "/", "p1", "pass", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDetails("p1", "pass", "profile-transcode", "mpegts", "mpegts", "mycodec", "aac", new List<string>(), new List<string>(), null));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Name == "Deinterlacing" && c.Status == "OK");
    }

    /// <summary>
    /// ReadBool handles string "false" as false → deinterlace WARNING.
    /// </summary>
    [Fact]
    public async Task DiagnoseAsync_DeinterlaceParamStringFalse_ResolvesAsFalse()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver);

        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/serverinfo")) return Task.FromResult(ServerInfoJson());
                if (url.Contains("api/channel/grid")) return Task.FromResult(ChannelGridJson());
                if (url.Contains("api/dvr/entry/grid")) return Task.FromResult(DvrGridJson());
                if (url.Contains("api/codec_profile/list"))
                    return Task.FromResult("""{"entries":[{"uuid":"c1","title":"mycodec"}]}""");
                if (url.Contains("api/idnode/load") && url.Contains("c1"))
                    return Task.FromResult("""{"entries":[{"params":[{"id":"deinterlace","value":"false"}]}]}""");
                return Task.FromResult("{}");
            });

        streamResolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("p1", "pass") });
        streamResolver.Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), "http://tvh", "/", "p1", "pass", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDetails("p1", "pass", "profile-transcode", "mpegts", "mpegts", "mycodec", "aac", new List<string>(), new List<string>(), null));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Name == "Deinterlacing" && c.Status == "WARNING");
    }

    /// <summary>
    /// ReadBool handles numeric 1 as true → deinterlace OK.
    /// </summary>
    [Fact]
    public async Task DiagnoseAsync_DeinterlaceParamNumericOne_ResolvesAsTrue()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver);

        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/serverinfo")) return Task.FromResult(ServerInfoJson());
                if (url.Contains("api/channel/grid")) return Task.FromResult(ChannelGridJson());
                if (url.Contains("api/dvr/entry/grid")) return Task.FromResult(DvrGridJson());
                if (url.Contains("api/codec_profile/list"))
                    return Task.FromResult("""{"entries":[{"uuid":"c1","title":"mycodec"}]}""");
                if (url.Contains("api/idnode/load") && url.Contains("c1"))
                    return Task.FromResult("""{"entries":[{"params":[{"id":"deinterlace","value":1}]}]}""");
                return Task.FromResult("{}");
            });

        streamResolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference> { new("p1", "pass") });
        streamResolver.Setup(x => x.GetProfileDetailsByUuidAsync(It.IsAny<HttpClient>(), "http://tvh", "/", "p1", "pass", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileDetails("p1", "pass", "profile-transcode", "mpegts", "mpegts", "mycodec", "aac", new List<string>(), new List<string>(), null));

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Name == "Deinterlacing" && c.Status == "OK");
    }

    /// <summary>
    /// When serverinfo throws a non-HTTP exception, it should be caught as a warning, not ERROR.
    /// </summary>
    [Fact]
    public async Task DiagnoseAsync_ServerInfoGenericException_AddsWarningNotError()
    {
        var sut = CreateSut(out var apiClient, out var streamResolver);

        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.Is<string>(u => u.Contains("api/serverinfo")), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("parse fail"));
        apiClient.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.Is<string>(u => !u.Contains("api/serverinfo")), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/channel/grid")) return Task.FromResult(ChannelGridJson());
                if (url.Contains("api/dvr/entry/grid")) return Task.FromResult(DvrGridJson());
                return Task.FromResult("{}");
            });

        streamResolver.Setup(x => x.GetProfilesAsync(It.IsAny<HttpClient>(), "http://tvh", "/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProfileReference>());

        var result = await sut.DiagnoseAsync(CancellationToken.None);

        Assert.Contains(result.Checks, c => c.Name == "Server Info" && c.Status == "WARNING");
        Assert.NotEqual("ERROR", result.OverallStatus);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string ServerInfoJson() => """{"sw_version":"4.3","api_version":19,"name":"tvh"}""";
    private static string ChannelGridJson() => """{"total":1,"entries":[{"uuid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}""";
    private static string DvrGridJson() => """{"total":0,"entries":[]}""";

    private static DiagnosticService CreateSut(out Mock<IApiClient> apiClient, out Mock<IProfileResolver> streamResolver)
    {
        apiClient = new Mock<IApiClient>(MockBehavior.Strict);
        streamResolver = new Mock<IProfileResolver>(MockBehavior.Strict);
        var encodingReader = new Mock<IEncodingOptionsReader>();
        var serverConfigManager = new Mock<IServerConfigurationManager>();

        var config = new PluginConfiguration
        {
            Host = "tvh",
            Port = 9981,
            Webroot = "/",
            AllowAnonymousAccess = true,
            StreamingProfile = "pass",
            RecordingProfile = "default",
            AuthToken = "abc123",
            AnalyzeDurationMs = 200,
        };

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(config);
        apiClient.Setup(x => x.CreateApiHttpClient(config)).Returns(new HttpClient());



        apiClient.Setup(x => x.PostFormAsync(
                It.IsAny<HttpClient>(),
                It.Is<string>(u => u.Contains("idnode/load")),
                It.IsAny<IEnumerable<KeyValuePair<string, string>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"entries":[]}"""),
            });

        streamResolver.Setup(x => x.GetProfileDetailsByUuidAsync(
                It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProfileDetails?)null);

        encodingReader
            .Setup(x => x.ReadFfmpegSettings(It.IsAny<IServerConfigurationManager>(), It.IsAny<ILogger>()))
            .Returns((null, null));

        var urlBuilder = new Mock<IUrlBuilder>();
        urlBuilder.Setup(u => u.GetBaseUrl(It.IsAny<PluginConfiguration>())).Returns("http://tvh");
        urlBuilder.Setup(u => u.GetWebRoot(It.IsAny<PluginConfiguration>())).Returns("/");
        urlBuilder.Setup(u => u.BuildApiUrl(It.IsAny<PluginConfiguration>(), It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) => "http://tvh/" + endpoint.TrimStart('/'));

        return new DiagnosticService(
            NullLogger<DiagnosticService>.Instance,
            serverConfigManager.Object,
            encodingReader.Object,
            streamResolver.Object,
            apiClient.Object,
            urlBuilder.Object,
            new CachePathProvider(() => null));
    }
}
