using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Infrastructure;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class ProfileResolverTests
{
    [Fact]
    public void Constructor_WithNullApiClient_Throws()
    {
        var reader = new Mock<IJsonReader>();

        Assert.Throws<System.ArgumentNullException>(() =>
            new ProfileResolver(null!, reader.Object));
    }

    [Fact]
    public async Task GetProfilesAsync_WhenEntriesMissing_ReturnsEmpty()
    {
        var api = new Mock<IApiClient>();
        var reader = new Mock<IJsonReader>();
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{}");

        var sut = new ProfileResolver(api.Object, reader.Object);
        using var http = new HttpClient();
        var result = await sut.GetProfilesAsync(http, "http://tvh:9981", "/", CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetProfilesAsync_WithValidEntries_ReturnsReferences()
    {
        var api = new Mock<IApiClient>();
        var reader = new Mock<IJsonReader>();
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"entries\":[{\"key\":\"uuid-1\",\"val\":\"jellyfin\"}]}");
        reader.Setup(x => x.GetStringProp(It.IsAny<System.Text.Json.JsonElement>(), "key")).Returns("uuid-1");
        reader.Setup(x => x.GetStringProp(It.IsAny<System.Text.Json.JsonElement>(), "val")).Returns("jellyfin");

        var sut = new ProfileResolver(api.Object, reader.Object);
        using var http = new HttpClient();
        var result = await sut.GetProfilesAsync(http, "http://tvh:9981", "/", CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("uuid-1", result[0].Key);
        Assert.Equal("jellyfin", result[0].Name);
    }

    [Fact]
    public async Task GetProfileDetailsByUuidAsync_WhenEntriesEmpty_ReturnsNull()
    {
        var api = new Mock<IApiClient>();
        var reader = new Mock<IJsonReader>();
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.Is<string>(u => u.Contains("idnode/load", System.StringComparison.Ordinal)), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"entries\":[]}");

        var sut = new ProfileResolver(api.Object, reader.Object);
        using var http = new HttpClient();
        var result = await sut.GetProfileDetailsByUuidAsync(http, "http://tvh:9981", "/", "uuid-1", "jellyfin", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetProfileDetailsByUuidAsync_WithValidEntry_ReturnsDetails()
    {
        var api = new Mock<IApiClient>();
        var reader = new Mock<IJsonReader>();
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.Is<string>(u => u.Contains("idnode/load", System.StringComparison.Ordinal)), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"entries\":[{}]}");

        reader.Setup(x => x.GetStringPropOrParam(It.IsAny<System.Text.Json.JsonElement>(), "class")).Returns("profile-transcode");
        reader.Setup(x => x.GetStringPropOrParam(It.IsAny<System.Text.Json.JsonElement>(), "container")).Returns("9");
        reader.Setup(x => x.GetStringPropOrParam(It.IsAny<System.Text.Json.JsonElement>(), "pro_vcodec")).Returns("jellyfin-h264");
        reader.Setup(x => x.GetStringPropOrParam(It.IsAny<System.Text.Json.JsonElement>(), "pro_acodec")).Returns("jellyfin-aac");
        reader.Setup(x => x.GetStringArrayPropOrParam(It.IsAny<System.Text.Json.JsonElement>(), "src_vcodec")).Returns(new[] { "H264" });
        reader.Setup(x => x.GetStringArrayPropOrParam(It.IsAny<System.Text.Json.JsonElement>(), "src_acodec")).Returns(new[] { "AAC" });
        reader.Setup(x => x.GetBoolPropOrParam(It.IsAny<System.Text.Json.JsonElement>(), "deinterlace")).Returns(true);

        var sut = new ProfileResolver(api.Object, reader.Object);
        using var http = new HttpClient();
        var result = await sut.GetProfileDetailsByUuidAsync(http, "http://tvh:9981", "/", "uuid-1", "jellyfin", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("uuid-1", result.Key);
        Assert.Equal("jellyfin", result.Name);
        Assert.Equal("mp4", result.Container);
        Assert.Equal("jellyfin-h264", result.ProVideoCodec);
        Assert.Equal("jellyfin-aac", result.ProAudioCodec);
    }

    [Fact]
    public async Task ResolveProfileByNameAsync_WithCodecProfileLink_ResolvesCodecAndDeinterlace()
    {
        var api = new Mock<IApiClient>();
        var reader = new JsonReader();

        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, System.Threading.CancellationToken>((_, url, _) =>
            {
                if (url.Contains("api/profile/list", System.StringComparison.Ordinal))
                {
                    return Task.FromResult("{\"entries\":[{\"key\":\"profile-1\",\"val\":\"jellyfin\"}]}");
                }

                if (url.Contains("api/codec_profile/list", System.StringComparison.Ordinal))
                {
                    return Task.FromResult("{\"entries\":[{\"uuid\":\"codec-1\",\"title\":\"jellyfin-h264 (libx264)\"}]}");
                }

                if (url.Contains("api/idnode/load", System.StringComparison.Ordinal) && url.Contains("profile-1", System.StringComparison.Ordinal))
                {
                    return Task.FromResult("{\"entries\":[{\"class\":\"profile-transcode\",\"container\":\"9\",\"pro_vcodec\":\"jellyfin-h264\",\"pro_acodec\":\"jellyfin-aac\",\"deinterlace\":false}]}");
                }

                if (url.Contains("api/idnode/load", System.StringComparison.Ordinal) && url.Contains("codec-1", System.StringComparison.Ordinal))
                {
                    return Task.FromResult("{\"entries\":[{\"class\":\"codec_profile_libx264\",\"codec\":\"libx264\",\"deinterlace\":true}]}");
                }

                return Task.FromResult("{}");
            });

        var sut = new ProfileResolver(api.Object, reader);
        using var http = new HttpClient();
        var result = await sut.ResolveProfileByNameAsync(http, "http://tvh:9981", "/", "jellyfin", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("profile-1", result.Key);
        Assert.Equal("h264", result.ResolvedVideoCodec);
        Assert.Equal("aac", result.ResolvedAudioCodec);
        Assert.False(result.ProfileDeinterlace);
        Assert.True(result.VideoCodecDeinterlace);
    }
}
