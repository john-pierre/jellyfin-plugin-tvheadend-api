// Circuit breaker state for the TVHeadend upstream connection.

namespace Jellyfin.Plugin.TvHeadendApi.Service.Health;

/// <summary>
/// Represents the circuit breaker state.
/// </summary>
public enum CircuitState
{
    /// <summary>Normal operation — all requests pass through.</summary>
    Closed,

    /// <summary>Failure threshold exceeded — requests are blocked.</summary>
    Open,

    /// <summary>Break duration elapsed — limited trial requests allowed.</summary>
    HalfOpen,
}
