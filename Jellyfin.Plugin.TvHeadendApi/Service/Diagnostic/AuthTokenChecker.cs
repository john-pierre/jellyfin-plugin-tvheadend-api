using Jellyfin.Plugin.TvHeadendApi.Model.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Service.Auth;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;

/// <summary>
/// Validates the authentication token format and adds the result to the diagnostic report.
/// </summary>
internal static class AuthTokenChecker
{
    /// <summary>
    /// Checks whether the configured auth token is present and alphanumeric.
    /// </summary>
    /// <param name="report">The diagnostic report to populate.</param>
    /// <param name="authToken">The auth token value from configuration.</param>
    /// <returns>The score deduction (0 when valid, 20 when invalid or empty).</returns>
    internal static int Check(DiagnoseResult report, string? authToken)
    {
        if (string.IsNullOrWhiteSpace(authToken))
        {
            report.Checks.Add(new DiagnoseCheck
            {
                Category = "Authentication",
                Name = "Auth Token Format",
                Status = "ERROR",
                Message = "Auth token is empty.",
                Recommendation = "Generate a token and use only letters and numbers (A-Z, a-z, 0-9)."
            });
            return 20;
        }

        if (!TokenValidator.IsValidTokenFormat(authToken))
        {
            report.Checks.Add(new DiagnoseCheck
            {
                Category = "Authentication",
                Name = "Auth Token Format",
                Status = "ERROR",
                Message = "Auth token contains unsupported characters.",
                Recommendation = "Use only letters and numbers (A-Z, a-z, 0-9)."
            });
            return 20;
        }

        report.Checks.Add(new DiagnoseCheck
        {
            Category = "Authentication",
            Name = "Auth Token Format",
            Status = "OK",
            Message = "Auth token format is alphanumeric."
        });
        return 0;
    }
}
