using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class ProfileMappingHelperTests
{
    [Theory]
    [InlineData("1", "matroska")]
    [InlineData("2", "mpegts")]
    [InlineData("9", "mp4")]
    [InlineData("mkv", "matroska")]
    [InlineData("mp4", "mp4")]
    [InlineData("not set", "")]
    public void MapContainer_MapsKnownValues(string raw, string expected)
    {
        var result = ProfileMappingHelper.MapContainer(raw);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void MapContainer_UnknownValue_ReturnsLowercaseOriginal()
    {
        var result = ProfileMappingHelper.MapContainer("CustomContainer");

        Assert.Equal("customcontainer", result);
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
