// Final outcome classification for completed stream sessions.

namespace Jellyfin.Plugin.TvHeadendApi.Model.Metrics;

/// <summary>
/// Classifies the final outcome of a relay stream session.
/// Separates normal Live TV disconnect behavior from actual failures.
/// </summary>
public enum StreamFinalOutcome
{
    /// <summary>Session is currently running (active sessions only).</summary>
    Active,

    /// <summary>Stream ended cleanly by upstream EOF or normal completion.</summary>
    Completed,

    /// <summary>Client disconnected after at least one byte was successfully sent.</summary>
    NormalDisconnect,

    /// <summary>Stream failed before first downstream byte.</summary>
    StartupFailed,

    /// <summary>TVHeadend/backend caused the failure.</summary>
    UpstreamFailed,

    /// <summary>Client/network/write caused failure after response started.</summary>
    DownstreamFailed,

    /// <summary>Unexpected or unclassified failure.</summary>
    Failed,
}
