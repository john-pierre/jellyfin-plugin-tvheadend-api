using System;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Auth;

/// <summary>
/// Validates TVHeadend auth token format for stream URL compatibility.
/// </summary>
internal static class TokenValidator
{
    /// <summary>
    /// Returns true when the token contains only alphanumeric ASCII characters (A-Z, a-z, 0-9).
    /// <para>
    /// TVHeadend generates auth tokens using a modified base64 alphabet that also includes
    /// <c>'-'</c> and <c>'.'</c> (see <c>access.c:passwd_entry_new_auth</c>). However, these
    /// characters can break FFmpeg stream URL parsing, so the plugin rejects tokens containing
    /// them and asks TVHeadend to regenerate until a purely alphanumeric token is produced.
    /// </para>
    /// </summary>
    /// <param name="token">Token value to validate.</param>
    /// <returns><c>true</c> when the token is non-empty and contains only <c>A-Za-z0-9</c>; otherwise <c>false</c>.</returns>
    public static bool IsValidTokenFormat(string? token)
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
