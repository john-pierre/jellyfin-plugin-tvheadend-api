// Defines structured failure reasons for relay token validation.

namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// Structured classification of relay token validation failure reasons.
/// Used in validation results, logging, and metrics.
/// </summary>
public enum RelayTokenFailureReason
{
    /// <summary>No failure — token validation succeeded.</summary>
    None,

    /// <summary>No token was provided in the request.</summary>
    MissingToken,

    /// <summary>The token is malformed (empty, too long, or invalid format).</summary>
    MalformedToken,

    /// <summary>No matching token hash found in the database.</summary>
    TokenNotFound,

    /// <summary>The token has expired beyond its TTL and clock skew tolerance.</summary>
    Expired,

    /// <summary>The token has been explicitly revoked.</summary>
    Revoked,

    /// <summary>The token has exceeded its maximum allowed uses.</summary>
    MaxUsesExceeded,

    /// <summary>The token scope does not match the requested resource (wrong channel or image).</summary>
    ScopeMismatch,

    /// <summary>The token relay type does not match the endpoint (e.g. image token on stream endpoint).</summary>
    RelayTypeMismatch,

    /// <summary>The token was issued for a different user than the requester.</summary>
    UserMismatch,

    /// <summary>The token was issued for a different device than the requester.</summary>
    DeviceMismatch,

    /// <summary>Relay token security is disabled — validation skipped.</summary>
    SecurityDisabled,

    /// <summary>An unexpected error occurred during validation.</summary>
    UnexpectedError,
}
