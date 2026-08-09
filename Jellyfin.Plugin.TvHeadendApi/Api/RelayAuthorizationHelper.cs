// Centralized relay token extraction and HTTP status code mapping for public relay endpoints.

using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.TvHeadendApi.Api;

/// <summary>
/// Extracts relay tokens from HTTP requests and maps validation failure reasons to HTTP status codes.
/// Used by relay controller endpoints to enforce token-based security consistently.
/// </summary>
internal static class RelayAuthorizationHelper
{
    /// <summary>
    /// Query string parameter name for the relay token.
    /// </summary>
    internal const string TokenQueryParam = "token";

    /// <summary>
    /// Extracts the relay token from the request query string.
    /// </summary>
    /// <param name="request">The HTTP request.</param>
    /// <returns>The raw token value, or null if not present.</returns>
    public static string? ExtractToken(HttpRequest request)
    {
        return request.Query.TryGetValue(TokenQueryParam, out var tokenValues)
            ? tokenValues.ToString()
            : null;
    }

    /// <summary>
    /// Maps a <see cref="RelayTokenFailureReason"/> to an appropriate HTTP status code.
    /// </summary>
    /// <param name="reason">The validation failure reason.</param>
    /// <returns>The HTTP status code to return to the client.</returns>
    public static int MapToStatusCode(RelayTokenFailureReason reason) => reason switch
    {
        RelayTokenFailureReason.MissingToken => StatusCodes.Status401Unauthorized,
        RelayTokenFailureReason.MalformedToken => StatusCodes.Status401Unauthorized,
        RelayTokenFailureReason.TokenNotFound => StatusCodes.Status401Unauthorized,
        RelayTokenFailureReason.Expired => StatusCodes.Status410Gone,
        RelayTokenFailureReason.Revoked => StatusCodes.Status403Forbidden,
        RelayTokenFailureReason.MaxUsesExceeded => StatusCodes.Status403Forbidden,
        RelayTokenFailureReason.ScopeMismatch => StatusCodes.Status403Forbidden,
        RelayTokenFailureReason.RelayTypeMismatch => StatusCodes.Status403Forbidden,
        RelayTokenFailureReason.UserMismatch => StatusCodes.Status403Forbidden,
        RelayTokenFailureReason.DeviceMismatch => StatusCodes.Status403Forbidden,
        RelayTokenFailureReason.UnexpectedError => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status401Unauthorized,
    };
}
