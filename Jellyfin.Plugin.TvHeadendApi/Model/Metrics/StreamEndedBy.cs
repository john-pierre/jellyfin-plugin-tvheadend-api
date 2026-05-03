// Classifies how a relay stream session ended — distinguishes normal Live TV behavior from real failures.

namespace Jellyfin.Plugin.TvHeadendApi.Model.Metrics;

/// <summary>
/// Describes the reason a relay stream session ended.
/// Designed for Live TV where client disconnects are normal behavior.
/// </summary>
public enum StreamEndedBy
{
    // ── Normal / Expected ────────────────────────────────────────────

    /// <summary>Client disconnected after first byte was successfully sent (normal Live TV behavior).</summary>
    ClientDisconnectAfterFirstByte,

    /// <summary>User stopped playback or switched channel.</summary>
    UserStopOrChannelSwitch,

    /// <summary>Upstream sent EOF — stream completed naturally.</summary>
    UpstreamEof,

    // ── Failure ──────────────────────────────────────────────────────

    /// <summary>Client cancelled before any data was sent to downstream.</summary>
    StartupCancelledBeforeFirstByte,

    /// <summary>Upstream connection or read timed out.</summary>
    UpstreamTimeout,

    /// <summary>Upstream returned HTTP 401, 403, 404, or 5xx.</summary>
    UpstreamHttpError,

    /// <summary>Failed to write to downstream client after stream started.</summary>
    DownstreamWriteError,

    /// <summary>Plugin is shutting down.</summary>
    PluginShutdown,

    /// <summary>An unexpected exception occurred.</summary>
    UnexpectedException,

    /// <summary>Unknown end reason.</summary>
    Unknown,
}
