// Final outcome classification for completed relay stream sessions.

namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// Classifies the final outcome of a relay stream session.
/// Separates normal Live TV disconnect behavior from actual failures.
/// </summary>
public enum StreamFinalOutcome
{
    /// <summary>Stream ended cleanly by upstream EOF or normal completion.</summary>
    Completed,

    /// <summary>Client disconnected after at least one byte was successfully sent.</summary>
    NormalDisconnect,

    /// <summary>Stream failed before the first downstream byte.</summary>
    StartupFailed,

    /// <summary>TVHeadend/backend caused the failure.</summary>
    UpstreamFailed,

    /// <summary>Client/network/write caused the failure after the response started.</summary>
    DownstreamFailed,

    /// <summary>Unexpected or unclassified failure.</summary>
    Failed,
}
