// Tests for GuideService.HasCategoryFlag — whole-word category matching.

using Jellyfin.Plugin.TvHeadendApi.Service.Guide;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Guide;

public class GuideCategoryFlagTests
{
    [Theory]
    [InlineData("New", true)]
    [InlineData("new episode", true)]
    [InlineData("Premiere", true)]
    [InlineData("First Run", true)]
    [InlineData("Series premiere", true)]
    public void HasCategoryFlag_PremierePatterns_MatchWholeWords(string category, bool expected)
    {
        var result = GuideService.HasCategoryFlag(new[] { category }, "premiere", "first run", "new");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("News")] // regression: "News" contains "new" as a substring but is not a premiere
    [InlineData("Newsmagazine")]
    [InlineData("Renewed drama")]
    [InlineData("Documentary")]
    public void HasCategoryFlag_NonPremiereCategories_DoNotMatch(string category)
    {
        var result = GuideService.HasCategoryFlag(new[] { category }, "premiere", "first run", "new");
        Assert.False(result, $"Category '{category}' must not be flagged as premiere.");
    }

    [Fact]
    public void HasCategoryFlag_EmptyAndWhitespaceCategories_AreIgnored()
    {
        Assert.False(GuideService.HasCategoryFlag(new[] { string.Empty, "   " }, "live"));
    }

    [Fact]
    public void HasCategoryFlag_MultiplePatternsAcrossCategories_Match()
    {
        Assert.True(GuideService.HasCategoryFlag(new[] { "Sports", "Live" }, "live"));
        Assert.True(GuideService.HasCategoryFlag(new[] { "Wiederholung" }, "repeat", "rerun", "wiederholung"));
    }
}
