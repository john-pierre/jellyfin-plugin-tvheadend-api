using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Auth;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;

/// <summary>
/// Validates the authentication token and adds the result to the diagnostic report.
/// </summary>
internal static class AuthTokenChecker
{
    /// <summary>
    /// Checks the configured auth token in the context of the active delivery mode. The token
    /// (appended as <c>?auth=</c>) is only required for <see cref="StreamDeliveryMode.DirectToTvheadend"/>,
    /// where clients/FFmpeg connect to TVHeadend directly and cannot perform HTTP digest auth. In the
    /// default Relay mode the relay authenticates with the configured username/password, so an empty
    /// token is fine and must not be reported as an error.
    /// </summary>
    /// <param name="report">The diagnostic report to populate.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The score deduction (0 when valid/not-required, 20 when invalid or missing-but-required).</returns>
    internal static int Check(DiagnoseResult report, PluginConfiguration config)
    {
        var authToken = config.AuthToken;
        var directMode = config.StreamDeliveryMode == StreamDeliveryMode.DirectToTvheadend;

        if (string.IsNullOrWhiteSpace(authToken))
        {
            if (directMode && !config.AllowAnonymousAccess)
            {
                report.Checks.Add(new DiagnoseCheck
                {
                    Category = "Authentication",
                    Name = "Auth Token Format",
                    Status = "ERROR",
                    Message = "Direct-to-TVHeadend mode is selected but no auth token is set — clients cannot authenticate to TVHeadend.",
                    Recommendation = "Generate an alphanumeric token (A-Z, a-z, 0-9), switch to Relay mode (the default), or enable anonymous access in TVHeadend.",
                });
                return 20;
            }

            report.Checks.Add(new DiagnoseCheck
            {
                Category = "Authentication",
                Name = "Auth Token",
                Status = "OK",
                Message = config.AllowAnonymousAccess
                    ? "No auth token set — not required (anonymous access is enabled)."
                    : "No auth token set — not required in Relay mode (the relay authenticates with the configured username/password).",
            });
            return 0;
        }

        if (!TokenValidator.IsValidTokenFormat(authToken))
        {
            report.Checks.Add(new DiagnoseCheck
            {
                Category = "Authentication",
                Name = "Auth Token Format",
                Status = "ERROR",
                Message = "Auth token contains unsupported characters.",
                Recommendation = "Use only letters and numbers (A-Z, a-z, 0-9) — the token is appended to stream URLs and must be FFmpeg-safe.",
            });
            return 20;
        }

        report.Checks.Add(new DiagnoseCheck
        {
            Category = "Authentication",
            Name = "Auth Token Format",
            Status = "OK",
            Message = "Auth token format is alphanumeric.",
        });
        return 0;
    }
}
