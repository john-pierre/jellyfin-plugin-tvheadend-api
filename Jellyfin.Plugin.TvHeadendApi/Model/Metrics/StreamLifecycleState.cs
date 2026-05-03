// Defines the lifecycle states of a relay streaming session.

namespace Jellyfin.Plugin.TvHeadendApi.Model.Metrics;

/// <summary>
/// Represents the lifecycle phase of a relay streaming session.
/// Transitions: Starting → Active → Ending → Completed/Failed.
/// </summary>
public enum StreamLifecycleState
{
    /// <summary>Session created, upstream connection in progress.</summary>
    Starting,

    /// <summary>First byte sent to client, actively streaming.</summary>
    Active,

    /// <summary>Stream ending, finalizing metrics.</summary>
    Ending,

    /// <summary>Stream completed normally (upstream EOF or client stop after data).</summary>
    Completed,

    /// <summary>Stream ended due to a failure condition.</summary>
    Failed,
}
