using System;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Auth;

/// <summary>
/// Validates TVHeadend auth token format for stream URL compatibility.
/// </summary>
internal static class TokenValidator
{
    /// <summary>
    /// Returns true when the token contains only URL-safe characters produced by TVHeadend.
    /// TVHeadend generates auth tokens using a modified base64 alphabet:
    /// A-Z, a-z, 0-9, '-' and '.' (see access.c:passwd_entry_new_auth).
    /// </summary>
    /// <param name="token">Token value to validate.</param>
    /// <returns><c>true</c> when the token is non-empty and contains only valid TVH token characters; otherwise <c>false</c>.</returns>
    public static bool IsAlphanumeric(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '.')
            {
                return false;
            }
        }

        return true;
    }
}
