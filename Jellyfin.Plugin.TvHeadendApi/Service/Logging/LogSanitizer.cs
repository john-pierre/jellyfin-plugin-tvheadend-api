using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Logging;

/// <summary>
/// Centralized log sanitization. Removes or masks sensitive data (tokens, passwords,
/// Authorization headers, query-string secrets) from log messages before persistence or output.
/// </summary>
public static class LogSanitizer
{
    private const string Redacted = "***REDACTED***";

    // auth=<token>, token=<value>, password=<value> in query strings
    private static readonly Regex QuerySecretRegex = new(
        @"(?<=([\?&])(auth|token|password|secret|key|api_key|apikey)=)[^&\s""']+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Authorization: Bearer <token> or Basic <base64>
    private static readonly Regex AuthHeaderRegex = new(
        @"(Authorization\s*[:=]\s*)(Bearer\s+|Basic\s+|Digest\s+)?\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // password=... or Password=... in key-value contexts
    private static readonly Regex PasswordKvRegex = new(
        @"(?<=(password|passwd|pwd)\s*[:=]\s*)\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Username/user in connection strings or config dumps
    private static readonly Regex UsernameKvRegex = new(
        @"(?<=(username|user)\s*[:=]\s*)\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // TVHeadend log pattern: "using auth <TOKEN> for ..." or "auth <TOKEN>"
    private static readonly Regex TvhAuthTokenRegex = new(
        @"(?<=\bauth\s+)[A-Za-z0-9]{8,}\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Sanitizes a log message by masking sensitive data.
    /// </summary>
    /// <param name="message">The raw log message.</param>
    /// <returns>The sanitized message safe for persistence and display.</returns>
    public static string Sanitize(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var result = QuerySecretRegex.Replace(message, Redacted);
        result = AuthHeaderRegex.Replace(result, "$1" + Redacted);
        result = PasswordKvRegex.Replace(result, Redacted);
        result = UsernameKvRegex.Replace(result, Redacted);
        result = TvhAuthTokenRegex.Replace(result, Redacted);

        return result;
    }

    /// <summary>
    /// Checks whether a message contains potentially sensitive content.
    /// </summary>
    /// <param name="message">The message to check.</param>
    /// <returns><c>true</c> if the message contains patterns that look like secrets.</returns>
    public static bool ContainsSensitiveData(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        return QuerySecretRegex.IsMatch(message)
               || AuthHeaderRegex.IsMatch(message)
               || PasswordKvRegex.IsMatch(message)
               || TvhAuthTokenRegex.IsMatch(message);
    }
}
