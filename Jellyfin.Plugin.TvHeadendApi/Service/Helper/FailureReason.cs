// Consistent failure classification for all TVHeadend communication paths.

namespace Jellyfin.Plugin.TvHeadendApi.Service.Helper;

/// <summary>
/// Classifies TVHeadend upstream failure reasons for health tracking, logging, and dashboard display.
/// </summary>
public enum FailureReason
{
    /// <summary>No failure.</summary>
    None,

    /// <summary>Request timed out waiting for TVHeadend response.</summary>
    Timeout,

    /// <summary>TVHeadend returned 401/403 — credentials are invalid or insufficient.</summary>
    AuthFailed,

    /// <summary>DNS resolution failed for TVHeadend host.</summary>
    DnsFailure,

    /// <summary>TCP connection refused by TVHeadend host.</summary>
    ConnectionRefused,

    /// <summary>TVHeadend host is unreachable (network error, host down).</summary>
    UpstreamUnavailable,

    /// <summary>TVHeadend returned a 4xx client error (not auth).</summary>
    Upstream4xx,

    /// <summary>TVHeadend returned a 5xx server error.</summary>
    Upstream5xx,

    /// <summary>TVHeadend returned an unparseable or invalid response.</summary>
    InvalidResponse,

    /// <summary>Circuit breaker is open — requests are blocked without contacting TVHeadend.</summary>
    CircuitOpen,

    /// <summary>Request was cancelled by the caller.</summary>
    Cancelled,

    /// <summary>An unexpected exception occurred.</summary>
    UnexpectedException,

    /// <summary>Failure reason could not be determined.</summary>
    Unknown,
}
