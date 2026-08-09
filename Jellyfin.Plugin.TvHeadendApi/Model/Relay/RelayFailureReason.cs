// Failure reason classification for relay metrics.

namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// Structured classification of relay failure reasons.
/// Used consistently in persistence, API responses, and dashboard displays.
/// </summary>
public enum RelayFailureReason
{
    /// <summary>No failure — request succeeded.</summary>
    None,

    /// <summary>Upstream requires authentication.</summary>
    AuthRequired,

    /// <summary>Upstream denied authentication.</summary>
    AuthDenied,

    /// <summary>Invalid or malformed request.</summary>
    InvalidRequest,

    /// <summary>Could not establish TCP connection to upstream.</summary>
    UpstreamConnectFailed,

    /// <summary>Upstream did not respond within the timeout period.</summary>
    UpstreamTimeout,

    /// <summary>DNS resolution failed for the upstream host.</summary>
    UpstreamDnsFailure,

    /// <summary>Upstream returned HTTP 401.</summary>
    Upstream401,

    /// <summary>Upstream returned HTTP 403.</summary>
    Upstream403,

    /// <summary>Upstream returned HTTP 404.</summary>
    Upstream404,

    /// <summary>Upstream returned HTTP 5xx.</summary>
    Upstream5xx,

    /// <summary>Failed to write to downstream (Jellyfin client).</summary>
    DownstreamWriteFailed,

    /// <summary>Client cancelled the request.</summary>
    ClientCancelled,

    /// <summary>An unexpected exception occurred.</summary>
    UnexpectedException,

    /// <summary>Unknown failure reason.</summary>
    Unknown,
}
