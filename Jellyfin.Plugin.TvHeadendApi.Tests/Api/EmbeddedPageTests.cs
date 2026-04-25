using System.IO;
using System.Reflection;
using Jellyfin.Plugin.TvHeadendApi;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Api;

/// <summary>
/// Tests embedded admin pages against the runtime contracts they depend on.
/// </summary>
public class EmbeddedPageTests
{
    [Fact]
    public void ConfigPage_UsesCurrentStatisticsRetentionSetting()
    {
        var content = ReadEmbeddedResource("Configuration.ConfigPage.html");

        Assert.Contains("StatisticsRetentionPeriod", content);
        Assert.DoesNotContain("StatisticsRetentionDays", content);
        Assert.DoesNotContain("StatisticsSaveIntervalMinutes", content);
    }

    [Fact]
    public void DashboardPage_DoesNotContainBrokenStatisticsSnippet()
    {
        var content = ReadEmbeddedResource("Page.DashboardPage.html");

        Assert.DoesNotContain("allSessions.forEach(function (s) {", content);
        Assert.Contains("document.getElementById('kpiWatchTime').textContent", content);
        Assert.Contains("chartInstances.playMethod = new Chart", content);
    }

    private static string ReadEmbeddedResource(string suffix)
    {
        var resourceName = $"{typeof(Plugin).Namespace}.{suffix}";
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
