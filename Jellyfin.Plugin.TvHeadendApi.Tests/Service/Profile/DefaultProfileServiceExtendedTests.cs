using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

/// <summary>
/// Tests for the capability-driven <see cref="DefaultProfileService"/> that provisions the
/// plugin-managed "jellyfin" smart-copy streaming profile (H.264/AAC/MPEG-TS).
/// </summary>
public class DefaultProfileServiceExtendedTests
{
    private static readonly PluginConfiguration TestConfig = new();

    // codec/list entries (capability detection). Note: "codec/list" != "codec_profile/list".
    // "name" is the ffmpeg encoder name used for codec_profile/create; "class" is the codec-profile node class.
    private const string Libx264Entry = """{"class":"codec_profile_libx264","name":"libx264","props":[{"id":"name"},{"id":"profile"},{"id":"preset"},{"id":"tune"},{"id":"deinterlace"},{"id":"max_bit_rate"},{"id":"pix_fmt"}]}""";
    private const string VaapiEntry = """{"class":"codec_profile_vaapi_h264","name":"h264_vaapi","props":[{"id":"name"},{"id":"profile"},{"id":"level"},{"id":"deinterlace"},{"id":"device","enum":[{"key":"/dev/dri/renderD128","val":"card"}]}]}""";
    private const string VaapiNoDeviceEntry = """{"class":"codec_profile_vaapi_h264","name":"h264_vaapi","props":[{"id":"name"},{"id":"profile"},{"id":"level"},{"id":"deinterlace"}]}""";
    private const string AacEntry = """{"class":"codec_profile_aac","name":"aac","props":[{"id":"name"},{"id":"tracks"},{"id":"bit_rate"}]}""";

    private static string CodecList(params string[] entries) => $$"""{"entries":[{{string.Join(",", entries)}}]}""";

    private static string EmptyList => """{"entries":[]}""";

    private static string ProfileListWith(string name) => $$"""{"entries":[{"key":"uuid-stream","val":"{{name}}"}]}""";

    private static string CodecProfileListWith(string name) =>
        $$"""{"entries":[{"uuid":"uuid-{{name}}","key":"uuid-{{name}}","title":"{{name}}","val":"{{name}}"}]}""";

    private static string IdNodeLoadWithClass(string className) =>
        $$"""{"entries":[{"class":"{{className}}","params":[]}]}""";

    private sealed record PostCall(string Url, IReadOnlyList<KeyValuePair<string, string>> Form);

    private static Mock<IApiClient> CreateApi(
        Func<string, string> getStringHandler,
        List<PostCall>? captured = null,
        Func<string, HttpResponseMessage>? postFormHandler = null)
    {
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns(TestConfig);
        api.Setup(x => x.CreateApiHttpClient(It.IsAny<PluginConfiguration>())).Returns(new HttpClient());

        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) => Task.FromResult(getStringHandler(url)));

        api.Setup(x => x.PostFormAsync(
                It.IsAny<HttpClient>(), It.IsAny<string>(),
                It.IsAny<IEnumerable<KeyValuePair<string, string>>>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, IEnumerable<KeyValuePair<string, string>>, CancellationToken>((_, url, form, _) =>
            {
                captured?.Add(new PostCall(url, form.ToList()));
                var response = postFormHandler?.Invoke(url)
                    ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                return Task.FromResult(response);
            });

        return api;
    }

    private static IUrlBuilder UrlBuilder() => Mock.Of<IUrlBuilder>(u =>
        u.GetBaseUrl(It.IsAny<PluginConfiguration>()) == "http://tvh:9981"
        && u.GetWebRoot(It.IsAny<PluginConfiguration>()) == "/");

    private static DefaultProfileService Sut(Mock<IApiClient> api) =>
        new(NullLogger<DefaultProfileService>.Instance, api.Object, UrlBuilder());

    [Fact]
    public async Task DetectsLibx264_CreatesTranscodeProfile()
    {
        var posts = new List<PostCall>();
        var api = CreateApi(
            url =>
            {
                if (url.Contains("codec_profile/list")) return EmptyList;
                if (url.Contains("codec/list")) return CodecList(Libx264Entry, AacEntry);
                if (url.Contains("profile/list")) return EmptyList;
                return "{}";
            },
            posts);

        var result = await Sut(api).CreateProfileAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("mpegts", result.Container);
        Assert.Equal("h264", result.VideoCodec);
        Assert.Equal("aac", result.AudioCodec);
        Assert.Contains("libx264", result.Message, StringComparison.OrdinalIgnoreCase);

        // Video + audio codec profiles created, plus the streaming profile.
        Assert.Contains(posts, p => p.Url.Contains("codec_profile/create")
            && p.Form.Any(f => f.Key == "class" && f.Value == "libx264"));
        // AAC codec profile must force AAC-LC (profile 1, browsers cannot decode AAC Main) and a fixed
        // CBR bitrate (avoids oversized frames that trip the mpegts muxer).
        var aacConf = posts.Single(p => p.Url.Contains("codec_profile/create")
            && p.Form.Any(f => f.Key == "class" && f.Value == "aac")).Form.Single(f => f.Key == "conf").Value;
        Assert.Contains("\"profile\":1", aacConf);
        Assert.Contains("\"bit_rate\":160", aacConf);

        // Full transcode: MPEG-TS container, ALL source codecs transcoded (H264/AAC included — no copy/transcode mixing).
        var stream = posts.Single(p => p.Url.Contains("api/profile/create"));
        var conf = stream.Form.Single(f => f.Key == "conf").Value;
        Assert.Contains("\"container\":2", conf);
        Assert.Contains("\"pro_vcodec\":\"jellyfin-h264\"", conf);
        Assert.Contains("\"pro_acodec\":\"jellyfin-aac\"", conf);
        Assert.Contains("\"H264\"", conf);   // H.264 source is transcoded, not copied.
        Assert.Contains("\"AAC\"", conf);     // AAC source is transcoded, not copied.
        Assert.Contains("MPEG2VIDEO", conf);
        Assert.Contains("MPEG2AUDIO", conf);
    }

    [Fact]
    public async Task PrefersHardwareWithDevice_OverSoftware()
    {
        var api = CreateApi(url =>
        {
            if (url.Contains("codec_profile/list")) return EmptyList;
            if (url.Contains("codec/list")) return CodecList(VaapiEntry, Libx264Entry, AacEntry);
            if (url.Contains("profile/list")) return EmptyList;
            return "{}";
        });

        var result = await Sut(api).CreateProfileAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("VAAPI", result.Message);
    }

    [Fact]
    public async Task PrefersSoftware_WhenHardwareHasNoDevice()
    {
        // VAAPI present but no detectable render device -> libx264 is the safe choice.
        var api = CreateApi(url =>
        {
            if (url.Contains("codec_profile/list")) return EmptyList;
            if (url.Contains("codec/list")) return CodecList(VaapiNoDeviceEntry, Libx264Entry, AacEntry);
            if (url.Contains("profile/list")) return EmptyList;
            return "{}";
        });

        var result = await Sut(api).CreateProfileAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("libx264", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FallsBackToVaapi_WhenNoSoftwareEncoder()
    {
        var posts = new List<PostCall>();
        var api = CreateApi(
            url =>
            {
                if (url.Contains("codec_profile/list")) return EmptyList;
                if (url.Contains("codec/list")) return CodecList(VaapiEntry, AacEntry);
                if (url.Contains("profile/list")) return EmptyList;
                return "{}";
            },
            posts);

        var result = await Sut(api).CreateProfileAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("VAAPI", result.Message);

        // The VAAPI codec profile is created via the ffmpeg encoder name with the detected render device.
        var create = posts.Single(p => p.Url.Contains("codec_profile/create")
            && p.Form.Any(f => f.Key == "class" && f.Value == "h264_vaapi"));
        var conf = create.Form.Single(f => f.Key == "conf").Value;
        Assert.Contains("/dev/dri/renderD128", conf);
    }

    [Fact]
    public async Task NoVideoEncoder_UsesPassthrough_AddsWarning()
    {
        var posts = new List<PostCall>();
        var api = CreateApi(
            url =>
            {
                if (url.Contains("codec_profile/list")) return EmptyList;
                if (url.Contains("codec/list")) return CodecList(AacEntry);
                if (url.Contains("profile/list")) return EmptyList;
                return "{}";
            },
            posts);

        var result = await Sut(api).CreateProfileAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("H.264 video encoder", result.Message, StringComparison.OrdinalIgnoreCase);

        // No video encoder -> copy BOTH (never mix copy + transcode).
        var stream = posts.Single(p => p.Url.Contains("api/profile/create"));
        var conf = stream.Form.Single(f => f.Key == "conf").Value;
        Assert.Contains("\"pro_vcodec\":\"copy\"", conf);
        Assert.Contains("\"pro_acodec\":\"copy\"", conf);
    }

    [Fact]
    public async Task NoAacEncoder_UsesPassthrough_AddsWarning()
    {
        var posts = new List<PostCall>();
        var api = CreateApi(
            url =>
            {
                if (url.Contains("codec_profile/list")) return EmptyList;
                if (url.Contains("codec/list")) return CodecList(Libx264Entry);
                if (url.Contains("profile/list")) return EmptyList;
                return "{}";
            },
            posts);

        var result = await Sut(api).CreateProfileAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("AAC audio encoder", result.Message, StringComparison.OrdinalIgnoreCase);

        // No AAC encoder -> copy BOTH (never mix copy + transcode).
        var stream = posts.Single(p => p.Url.Contains("api/profile/create"));
        var conf = stream.Form.Single(f => f.Key == "conf").Value;
        Assert.Contains("\"pro_acodec\":\"copy\"", conf);
        Assert.Contains("\"pro_vcodec\":\"copy\"", conf);
    }

    [Fact]
    public async Task ExistingCodecProfile_SameClass_IsUpdatedNotRecreated()
    {
        var posts = new List<PostCall>();
        var api = CreateApi(
            url =>
            {
                if (url.Contains("codec_profile/list")) return CodecProfileListWith("jellyfin-h264");
                if (url.Contains("codec/list")) return CodecList(Libx264Entry, AacEntry);
                if (url.Contains("profile/list")) return ProfileListWith("jellyfin");
                if (url.Contains("idnode/load")) return IdNodeLoadWithClass("codec_profile_libx264");
                return "{}";
            },
            posts);

        var result = await Sut(api).CreateProfileAsync(CancellationToken.None);

        Assert.True(result.Success);

        // Existing h264 profile uses the desired class -> updated via idnode/save, never deleted.
        Assert.Contains(posts, p => p.Url.Contains("idnode/save"));
        Assert.DoesNotContain(posts, p => p.Url.Contains("idnode/delete"));
        Assert.DoesNotContain(posts, p => p.Url.Contains("codec_profile/create")
            && p.Form.Any(f => f.Key == "class" && f.Value == "libx264"));
    }

    [Fact]
    public async Task ExistingCodecProfile_DifferentClass_IsRecreated()
    {
        var posts = new List<PostCall>();
        var api = CreateApi(
            url =>
            {
                if (url.Contains("codec_profile/list")) return CodecProfileListWith("jellyfin-h264");
                if (url.Contains("codec/list")) return CodecList(VaapiEntry, AacEntry);
                if (url.Contains("profile/list")) return EmptyList;
                if (url.Contains("idnode/load")) return IdNodeLoadWithClass("codec_profile_libx264");
                return "{}";
            },
            posts);

        var result = await Sut(api).CreateProfileAsync(CancellationToken.None);

        Assert.True(result.Success);

        // Existing profile is libx264 but VAAPI is required -> delete + create with the new encoder name.
        Assert.Contains(posts, p => p.Url.Contains("idnode/delete"));
        Assert.Contains(posts, p => p.Url.Contains("codec_profile/create")
            && p.Form.Any(f => f.Key == "class" && f.Value == "h264_vaapi"));
    }

    [Fact]
    public async Task StreamingProfileCreateFails_ReturnsFailure()
    {
        var api = CreateApi(
            url =>
            {
                if (url.Contains("codec_profile/list")) return EmptyList;
                if (url.Contains("codec/list")) return CodecList(Libx264Entry, AacEntry);
                if (url.Contains("profile/list")) return EmptyList;
                return "{}";
            },
            postFormHandler: url => url.Contains("api/profile/create")
                ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("denied") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        var result = await Sut(api).CreateProfileAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("streaming profile", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExistingStreamingProfile_IsUpdatedInPlace()
    {
        var posts = new List<PostCall>();
        var api = CreateApi(
            url =>
            {
                if (url.Contains("codec_profile/list")) return EmptyList;
                if (url.Contains("codec/list")) return CodecList(Libx264Entry, AacEntry);
                if (url.Contains("profile/list")) return ProfileListWith("jellyfin");
                return "{}";
            },
            posts);

        var result = await Sut(api).CreateProfileAsync(CancellationToken.None);

        Assert.True(result.Success);
        // Existing streaming profile -> idnode/save with uuid, no profile/create.
        Assert.Contains(posts, p => p.Url.Contains("idnode/save")
            && p.Form.Any(f => f.Key == "node" && f.Value.Contains("uuid-stream")));
        Assert.DoesNotContain(posts, p => p.Url.Contains("api/profile/create"));
    }

    [Fact]
    public async Task UnexpectedException_ReturnsFailure()
    {
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns(TestConfig);
        api.Setup(x => x.CreateApiHttpClient(It.IsAny<PluginConfiguration>())).Returns(new HttpClient());
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await Sut(api).CreateProfileAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Unexpected error", result.Message);
    }

    [Fact]
    public void Constructor_NullApiClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DefaultProfileService(NullLogger<DefaultProfileService>.Instance, null!, new Mock<IUrlBuilder>().Object));
    }

    [Fact]
    public void Constructor_NullUrlBuilder_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DefaultProfileService(NullLogger<DefaultProfileService>.Instance, new Mock<IApiClient>().Object, null!));
    }
}
