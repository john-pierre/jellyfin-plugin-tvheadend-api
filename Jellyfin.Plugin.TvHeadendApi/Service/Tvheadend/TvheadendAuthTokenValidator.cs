using System;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;

/// <summary>
/// Validates TVHeadend auth token format for stream URL compatibility.
/// </summary>
internal static class TvheadendAuthTokenValidator
{
    /// <summary>
    /// Returns true when the token contains only ASCII letters and digits.
    /// </summary>
    /// <param name="token">Token value to validate.</param>
    /// <returns><c>true</c> when the token is non-empty and alphanumeric; otherwise <c>false</c>.</returns>
    public static bool IsAlphanumeric(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
