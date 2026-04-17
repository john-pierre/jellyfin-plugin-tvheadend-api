using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

/// <summary>
/// Extended DefaultProfileService tests covering codec creation, streaming profile, linking, and error paths.
/// </summary>
public class DefaultProfileServiceExtendedTests
{
    private static readonly PluginConfiguration TestConfig = new();

    private static Mock<IApiClient> CreateApi(
        Func<string, string>? getStringHandler = null,
        Func<string, HttpResponseMessage>? postFormHandler = null)
    {
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns(TestConfig);
        api.Setup(x => x.BuildHttpClient(It.IsAny<PluginConfiguration>())).Returns(new HttpClient());
        api.Setup(x => x.GetBaseUrl(It.IsAny<PluginConfiguration>())).Returns("http://tvh:9981");
        api.Setup(x => x.GetWebRoot(It.IsAny<PluginConfiguration>())).Returns("/");

        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, CancellationToken>((_, url, _) =>
            {
                var result = getStringHandler?.Invoke(url) ?? "{}";
                return Task.FromResult(result);
            });

        api.Setup(x => x.PostFormAsync(
                It.IsAny<HttpClient>(), It.IsAny<string>(),
                It.IsAny<IEnumerable<KeyValuePair<string, string>>>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, IEnumerable<KeyValuePair<string, string>>, CancellationToken>((_, url, _, _) =>
            {
                var response = postFormHandler?.Invoke(url)
                    ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                return Task.FromResult(response);
            });

        return api;
    }

    private static string CodecListWith(string name) =>
        $$"""{"entries":[{"uuid":"uuid-{{name}}","key":"uuid-{{name}}","title":"{{name}} (libx264)","val":"{{name}}"}]}""";

    private static string EmptyList => """{"entries":[]}""";
    private static string ProfileListWith(string name) => $$"""{"entries":[{"key":"uuid-s","val":"{{name}}"}]}""";

    private static string IdNodeLoad =>
        """{"entries":[{"name":"jellyfin","enabled":true,"container":9,"src_vcodec":["H264"],"src_acodec":["AAC"],"src_scodec":[],"params":[]}]}""";

    [Fact]
    public async Task AllProfilesExist_ReturnsSuccess()
    {
        var api = CreateApi(url =>
        {
            if (url.Contains("codec_profile/list")) return CodecListWith("jellyfin-h264");
            if (url.Contains("profile/list")) return ProfileListWith("jellyfin");
            if (url.Contains("idnode/load")) return IdNodeLoad;
            return "{}";
        });

        var sut = new DefaultProfileService(NullLogger<DefaultProfileService>.Instance, api.Object);
        var result = await sut.CreateProfileAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("already exists", result.Message);
    }

    [Fact]
    public async Task CreatesCodecProfiles_WhenMissing()
    {
        var codecCreates = 0;
        var api = CreateApi(
            url =>
            {
                if (url.Contains("codec_profile/list")) return EmptyList;
                if (url.Contains("profile/list")) return ProfileListWith("jellyfin");
                if (url.Contains("idnode/load")) return IdNodeLoad;
                return "{}";
            },
            url =>
            {
                if (url.Contains("codec_profile/create")) codecCreates++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });

        var sut = new DefaultProfileService(NullLogger<DefaultProfileService>.Instance, api.Object);
        var result = await sut.CreateProfileAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, codecCreates);
    }

    [Fact]
    public async Task CreatesStreamingProfile_WhenMissing()
    {
        var profileCreated = false;
        var api = CreateApi(
            url =>
            {
                if (url.Contains("codec_profile/list")) return CodecListWith("jellyfin-h264");
                if (url.Contains("profile/list")) return EmptyList;
                if (url.Contains("idnode/load")) return IdNodeLoad;
                return "{}";
            },
            url =>
            {
                if (url.Contains("profile/create")) profileCreated = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });

        var sut = new DefaultProfileService(NullLogger<DefaultProfileService>.Instance, api.Object);
        await sut.CreateProfileAsync(CancellationToken.None);

        Assert.True(profileCreated);
    }

    [Fact]
    public async Task StreamingProfileFirstFails_RetriesMinimal()
    {
        var streamCalls = 0;
        var api = CreateApi(
            url =>
            {
                if (url.Contains("codec_profile/list")) return CodecListWith("jellyfin-h264");
                if (url.Contains("profile/list")) return EmptyList;
                if (url.Contains("idnode/load")) return IdNodeLoad;
                return "{}";
            },
            url =>
            {
                // Only count calls to /api/profile/create (not codec_profile/create)
                if (url.Contains("api/profile/create"))
                {
                    streamCalls++;
                    return streamCalls == 1
                        ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("err") }
                        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });

        var sut = new DefaultProfileService(NullLogger<DefaultProfileService>.Instance, api.Object);
        var result = await sut.CreateProfileAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, streamCalls);
    }

    [Fact]
    public async Task BothStreamingProfileAttemptsFail_ReturnsFailure()
    {
        var api = CreateApi(
            url =>
            {
                if (url.Contains("codec_profile/list")) return CodecListWith("jellyfin-h264");
                if (url.Contains("profile/list")) return EmptyList;
                if (url.Contains("idnode/load")) return IdNodeLoad;
                return "{}";
            },
            url =>
            {
                if (url.Contains("api/profile/create"))
                    return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("denied") };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });

        var sut = new DefaultProfileService(NullLogger<DefaultProfileService>.Instance, api.Object);
        var result = await sut.CreateProfileAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("streaming profile creation failed", result.Message);
    }

    [Fact]
    public async Task UnexpectedException_ReturnsFailure()
    {
        var api = new Mock<IApiClient>();
        api.Setup(x => x.GetCurrentConfiguration()).Returns(TestConfig);
        api.Setup(x => x.BuildHttpClient(It.IsAny<PluginConfiguration>())).Returns(new HttpClient());
        api.Setup(x => x.GetBaseUrl(It.IsAny<PluginConfiguration>())).Returns("http://tvh:9981");
        api.Setup(x => x.GetWebRoot(It.IsAny<PluginConfiguration>())).Returns("/");
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var sut = new DefaultProfileService(NullLogger<DefaultProfileService>.Instance, api.Object);
        var result = await sut.CreateProfileAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Unexpected error", result.Message);
    }

    [Fact]
    public async Task CodecCreationFails_ContinuesGracefully()
    {
        var api = CreateApi(
            url =>
            {
                if (url.Contains("codec_profile/list")) return EmptyList;
                if (url.Contains("profile/list")) return ProfileListWith("jellyfin");
                if (url.Contains("idnode/load")) return IdNodeLoad;
                return "{}";
            },
            url =>
            {
                if (url.Contains("codec_profile/create"))
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("fail") };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });

        var sut = new DefaultProfileService(NullLogger<DefaultProfileService>.Instance, api.Object);
        var result = await sut.CreateProfileAsync(CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task CodecCreationException_ContinuesGracefully()
    {
        var api = CreateApi(
            url =>
            {
                if (url.Contains("codec_profile/list")) return EmptyList;
                if (url.Contains("profile/list")) return ProfileListWith("jellyfin");
                if (url.Contains("idnode/load")) return IdNodeLoad;
                return "{}";
            },
            url =>
            {
                if (url.Contains("codec_profile/create")) throw new HttpRequestException("fail");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });

        var sut = new DefaultProfileService(NullLogger<DefaultProfileService>.Instance, api.Object);
        var result = await sut.CreateProfileAsync(CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task LinkingEmptyIdNode_AddsWarning()
    {
        var api = CreateApi(url =>
        {
            if (url.Contains("codec_profile/list")) return CodecListWith("jellyfin-h264");
            if (url.Contains("profile/list")) return ProfileListWith("jellyfin");
            if (url.Contains("idnode/load")) return """{"entries":[]}""";
            return "{}";
        });

        var sut = new DefaultProfileService(NullLogger<DefaultProfileService>.Instance, api.Object);
        var result = await sut.CreateProfileAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("WARNING", result.Message);
    }

    [Fact]
    public async Task LinkingSaveFails_AddsWarning()
    {
        var api = CreateApi(
            url =>
            {
                if (url.Contains("codec_profile/list")) return CodecListWith("jellyfin-h264");
                if (url.Contains("profile/list")) return ProfileListWith("jellyfin");
                if (url.Contains("idnode/load")) return IdNodeLoad;
                return "{}";
            },
            url =>
            {
                if (url.Contains("idnode/save"))
                    return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("denied") };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            });

        var sut = new DefaultProfileService(NullLogger<DefaultProfileService>.Instance, api.Object);
        var result = await sut.CreateProfileAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("WARNING", result.Message);
    }

    [Fact]
    public async Task LinkingException_AddsWarning()
    {
        var api = CreateApi(url =>
        {
            if (url.Contains("codec_profile/list")) return CodecListWith("jellyfin-h264");
            if (url.Contains("profile/list")) return ProfileListWith("jellyfin");
            if (url.Contains("idnode/load")) throw new HttpRequestException("fail");
            return "{}";
        });

        var sut = new DefaultProfileService(NullLogger<DefaultProfileService>.Instance, api.Object);
        var result = await sut.CreateProfileAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("WARNING", result.Message);
    }

    [Fact]
    public async Task CodecCheckException_TreatsAsNotExisting()
    {
        var api = CreateApi(url =>
        {
            if (url.Contains("codec_profile/list")) throw new HttpRequestException("fail");
            if (url.Contains("profile/list")) return ProfileListWith("jellyfin");
            if (url.Contains("idnode/load")) return IdNodeLoad;
            return "{}";
        });

        var sut = new DefaultProfileService(NullLogger<DefaultProfileService>.Instance, api.Object);
        var result = await sut.CreateProfileAsync(CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task StreamProfileNotResolvable_AddsWarning()
    {
        var api = CreateApi(
            url =>
            {
                if (url.Contains("codec_profile/list")) return CodecListWith("jellyfin-h264");
                if (url.Contains("profile/list")) return EmptyList;
                if (url.Contains("idnode/load")) return IdNodeLoad;
                return "{}";
            });

        var sut = new DefaultProfileService(NullLogger<DefaultProfileService>.Instance, api.Object);
        var result = await sut.CreateProfileAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("WARNING", result.Message);
    }

    [Fact]
    public void Constructor_NullApiClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DefaultProfileService(NullLogger<DefaultProfileService>.Instance, null!));
    }
}



