using Jellyfin.Plugin.TvHeadendApi.Model.Diagnostic;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;

/// <summary>
/// Checks the relay service configuration and adds the result to the diagnostic report.
/// </summary>
internal static class RelayChecker
{
    /// <summary>
    /// Checks whether the relay service is enabled and configured correctly.
    /// </summary>
    /// <param name="report">The diagnostic report to populate.</param>
    /// <param name="relayEnabled">Whether the relay service is enabled.</param>
    /// <param name="relayHostOverride">The optional relay host override value.</param>
    internal static void Check(DiagnoseResult report, bool relayEnabled, string? relayHostOverride)
    {
        if (!relayEnabled)
        {
            report.Checks.Add(new DiagnoseCheck
            {
                Category = "Relay",
                Name = "Relay Service",
                Status = "WARNING",
                Message = "Relay service is disabled. Images and streams are served directly from TVHeadend.",
                Recommendation = "Enable the relay service to hide TVHeadend credentials and internal URLs from clients.",
            });
            report.Warnings.Add("Relay service is disabled — TVHeadend credentials may be exposed to clients.");
            return;
        }

        var hasOverride = !string.IsNullOrWhiteSpace(relayHostOverride);
        report.Checks.Add(new DiagnoseCheck
        {
            Category = "Relay",
            Name = "Relay Service",
            Status = "OK",
            Message = hasOverride
                ? $"Relay enabled with custom host: {relayHostOverride}"
                : "Relay enabled with auto-detected Jellyfin host.",
        });

        if (!hasOverride)
        {
            report.Checks.Add(new DiagnoseCheck
            {
                Category = "Relay",
                Name = "Jellyfin Host Override",
                Status = "WARNING",
                Message = "No Jellyfin Host Override configured. The plugin will auto-detect the host, which may be incorrect in Docker or reverse-proxy setups.",
                Recommendation = "Set 'Jellyfin Host Override' in the Relay &amp; Security tab to the externally reachable Jellyfin URL (e.g. http://192.168.1.10:8096).",
            });
            report.Warnings.Add("Jellyfin Host Override is not set — auto-detected host may be wrong in Docker or reverse-proxy environments.");
        }
    }
}
