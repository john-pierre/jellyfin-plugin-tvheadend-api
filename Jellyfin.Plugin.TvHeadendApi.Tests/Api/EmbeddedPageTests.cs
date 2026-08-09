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

    [Fact]
    public void DashboardPage_LoadsChartJsFromThePluginNotACdn()
    {
        var content = ReadEmbeddedResource("Page.DashboardPage.html");

        // A CDN fetch left the whole analytics section blank on air-gapped or egress-filtered
        // servers, and re-requested on every 60s refresh.
        Assert.DoesNotContain("cdn.jsdelivr.net", content);
        Assert.DoesNotContain("https://cdn.", content);
        Assert.Contains("TvHeadendChartJs", content);
    }

    [Fact]
    public void ChartJs_IsEmbeddedInTheAssembly()
    {
        var resourceName = $"{typeof(Plugin).Namespace}.Page.lib.chart.umd.min.js";
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(resourceName);

        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var content = reader.ReadToEnd();

        Assert.Contains("Chart.js v4", content);
        Assert.True(content.Length > 100_000, $"Chart.js bundle looks truncated ({content.Length} chars).");
    }

    [Fact]
    public void ChartJs_IsRegisteredAsAServedPageResource()
    {
        // Jellyfin serves a plugin resource only if GetPages() registers it, and derives the
        // content type from the resource path extension — the ".js" suffix is what makes the
        // browser execute it as a script rather than reject it.
        var page = Assert.Single(
            PluginPageNames(),
            p => string.Equals(p.Name, "TvHeadendChartJs", System.StringComparison.Ordinal));

        Assert.EndsWith(".js", page.EmbeddedResourcePath, System.StringComparison.Ordinal);
    }

    private static System.Collections.Generic.IEnumerable<MediaBrowser.Model.Plugins.PluginPageInfo> PluginPageNames()
    {
        // GetPages() is an instance method but does not touch instance state beyond the namespace,
        // so it can be invoked on an uninitialized instance without the Jellyfin host.
        var plugin = (Plugin)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        return plugin.GetPages();
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
