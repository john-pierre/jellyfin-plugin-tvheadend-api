using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class TvheadendStreamProfileResolverTests
{
    [Fact]
    public void Constructor_WithNullApiClient_Throws()
    {
        var idNode = new Mock<ITvheadendIdNodeService>();
        var reader = new Mock<ITvheadendJsonReader>();

        Assert.Throws<System.ArgumentNullException>(() =>
            new TvheadendStreamProfileResolver(null!, idNode.Object, reader.Object));
    }

    [Fact]
    public async Task GetProfilesAsync_WhenEntriesMissing_ReturnsEmpty()
    {
        var api = new Mock<ITvheadendApiClient>();
        var idNode = new Mock<ITvheadendIdNodeService>();
        var reader = new Mock<ITvheadendJsonReader>();
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{}");

        var sut = new TvheadendStreamProfileResolver(api.Object, idNode.Object, reader.Object);
        using var http = new HttpClient();
        var result = await sut.GetProfilesAsync(http, "http://tvh:9981", "/", CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetProfilesAsync_WithValidEntries_ReturnsReferences()
    {
        var api = new Mock<ITvheadendApiClient>();
        var idNode = new Mock<ITvheadendIdNodeService>();
        var reader = new Mock<ITvheadendJsonReader>();
        api.Setup(x => x.GetStringAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"entries\":[{\"key\":\"uuid-1\",\"val\":\"jellyfin\"}]}");
        reader.Setup(x => x.GetStringProp(It.IsAny<System.Text.Json.JsonElement>(), "key")).Returns("uuid-1");
        reader.Setup(x => x.GetStringProp(It.IsAny<System.Text.Json.JsonElement>(), "val")).Returns("jellyfin");

        var sut = new TvheadendStreamProfileResolver(api.Object, idNode.Object, reader.Object);
        using var http = new HttpClient();
        var result = await sut.GetProfilesAsync(http, "http://tvh:9981", "/", CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("uuid-1", result[0].Key);
        Assert.Equal("jellyfin", result[0].Name);
    }

    [Fact]
    public async Task GetProfileDetailsByUuidAsync_WhenEntriesEmpty_ReturnsNull()
    {
        var api = new Mock<ITvheadendApiClient>();
        var idNode = new Mock<ITvheadendIdNodeService>();
        var reader = new Mock<ITvheadendJsonReader>();
        idNode.Setup(x => x.LoadIdNodeByUuidAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Text.Json.JsonDocument.Parse("{\"entries\":[]}"));

        var sut = new TvheadendStreamProfileResolver(api.Object, idNode.Object, reader.Object);
        using var http = new HttpClient();
        var result = await sut.GetProfileDetailsByUuidAsync(http, "http://tvh:9981", "/", "uuid-1", "jellyfin", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetProfileDetailsByUuidAsync_WithValidEntry_ReturnsDetails()
    {
        var api = new Mock<ITvheadendApiClient>();
        var idNode = new Mock<ITvheadendIdNodeService>();
        var reader = new Mock<ITvheadendJsonReader>();
        idNode.Setup(x => x.LoadIdNodeByUuidAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Text.Json.JsonDocument.Parse("{\"entries\":[{}]}"));

        reader.Setup(x => x.GetStringPropOrParam(It.IsAny<System.Text.Json.JsonElement>(), "class")).Returns("profile-transcode");
        reader.Setup(x => x.GetStringPropOrParam(It.IsAny<System.Text.Json.JsonElement>(), "container")).Returns("9");
        reader.Setup(x => x.GetStringPropOrParam(It.IsAny<System.Text.Json.JsonElement>(), "pro_vcodec")).Returns("jellyfin-h264");
        reader.Setup(x => x.GetStringPropOrParam(It.IsAny<System.Text.Json.JsonElement>(), "pro_acodec")).Returns("jellyfin-aac");
        reader.Setup(x => x.GetStringArrayPropOrParam(It.IsAny<System.Text.Json.JsonElement>(), "src_vcodec")).Returns(new[] { "H264" });
        reader.Setup(x => x.GetStringArrayPropOrParam(It.IsAny<System.Text.Json.JsonElement>(), "src_acodec")).Returns(new[] { "AAC" });
        reader.Setup(x => x.GetBoolPropOrParam(It.IsAny<System.Text.Json.JsonElement>(), "deinterlace")).Returns(true);

        var sut = new TvheadendStreamProfileResolver(api.Object, idNode.Object, reader.Object);
        using var http = new HttpClient();
        var result = await sut.GetProfileDetailsByUuidAsync(http, "http://tvh:9981", "/", "uuid-1", "jellyfin", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("uuid-1", result.Key);
        Assert.Equal("jellyfin", result.Name);
        Assert.Equal("mp4", result.Container);
        Assert.Equal("jellyfin-h264", result.ProVideoCodec);
        Assert.Equal("jellyfin-aac", result.ProAudioCodec);
    }
}
