using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class ProfileMappingHelperTests
{
    [Theory]
    [InlineData("1", "matroska")]
    [InlineData("2", "mpegts")]
    [InlineData("6", "webm")]
    [InlineData("7", "matroska")]
    [InlineData("8", "webm")]
    [InlineData("9", "mp4")]
    [InlineData("mkv", "matroska")]
    [InlineData("mp4", "mp4")]
    [InlineData("webm", "webm")]
    [InlineData("avmatroska", "matroska")]
    [InlineData("avwebm", "webm")]
    [InlineData("not set", "")]
    public void MapContainer_MapsKnownValues(string raw, string expected)
    {
        var result = ProfileMappingHelper.MapContainer(raw);

        Assert.Equal(expected, result);
    }

    // Unknown container values must map to string.Empty (never the raw value): callers such as
    // ProfileResolver only apply the profile-class-based container fallback for empty results, so a
    // leaked raw value (e.g. "5") would end up as the MediaSource/MediaInfo-cache container and
    // break Direct Play container detection.
    [Theory]
    [InlineData("CustomContainer")]
    [InlineData("5")] // MC_RAW — no FFmpeg container equivalent.
    [InlineData("10")] // Audio-only muxer range (10-15).
    public void MapContainer_UnknownValue_ReturnsEmptyForClassFallback(string raw)
    {
        var result = ProfileMappingHelper.MapContainer(raw);

        Assert.Equal(string.Empty, result);
    }

    [Theory]
    [InlineData("profile-matroska", "matroska")]
    [InlineData("profile-mpegts", "mpegts")]
    [InlineData("profile-mpegts-pass", "mpegts")]
    [InlineData("profile-htsp", "mpegts")]
    [InlineData("unknown-profile", "mpegts")]
    public void MapProfileClassToContainer_MapsExpected(string profileClass, string expected)
    {
        var result = ProfileMappingHelper.MapProfileClassToContainer(profileClass);

        Assert.Equal(expected, result);
    }
}
