// Overall health classification of the TVHeadend upstream connection.

namespace Jellyfin.Plugin.TvHeadendApi.Service.Health;

/// <summary>
/// Represents the overall health status of the TVHeadend upstream connection.
/// </summary>
public enum HealthStatus
{
    /// <summary>No health data available yet.</summary>
    Unknown,

    /// <summary>TVHeadend is reachable and responding normally.</summary>
    Healthy,

    /// <summary>TVHeadend is responding but with elevated latency or intermittent errors.</summary>
    Degraded,

    /// <summary>TVHeadend is not reachable.</summary>
    Unreachable,

    /// <summary>Authentication to TVHeadend failed.</summary>
    AuthFailed,

    /// <summary>TVHeadend requests are timing out.</summary>
    Timeout,

    /// <summary>Circuit breaker is open — upstream calls are blocked.</summary>
    CircuitOpen,

    /// <summary>Recovering from failure — half-open circuit breaker trial in progress.</summary>
    Recovering,
}
