// Stream end reason classification for relay stream metrics.

namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// Describes how a relay stream session ended.
/// </summary>
public enum StreamEndedBy
{
    /// <summary>Stream completed normally.</summary>
    Completed,

    /// <summary>Client disconnected / cancelled.</summary>
    ClientCancelled,

    /// <summary>Upstream closed the connection.</summary>
    UpstreamClosed,

    /// <summary>Upstream returned an error during streaming.</summary>
    UpstreamError,

    /// <summary>Downstream write to Jellyfin client failed.</summary>
    DownstreamError,

    /// <summary>Upstream connection timed out.</summary>
    Timeout,

    /// <summary>Authentication denied by upstream.</summary>
    AuthDenied,

    /// <summary>Unknown end reason.</summary>
    Unknown,
}
