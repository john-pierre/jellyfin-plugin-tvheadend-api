// Stream end reason classification for relay stream telemetry.
// Single merged vocabulary — the former Model/Metrics/StreamEndedBy duplicate has been removed.

namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// Describes how a relay stream session ended.
/// Designed for Live TV where client disconnects are normal behavior, not failures.
/// Every value is set by an actual code path — no speculative vocabulary.
/// </summary>
public enum StreamEndedBy
{
    // ── Normal / Expected ────────────────────────────────────────────

    /// <summary>Client disconnected after the first byte was successfully sent (normal Live TV zapping/stop).</summary>
    ClientDisconnectAfterFirstByte,

    /// <summary>Upstream sent EOF — the stream completed naturally.</summary>
    UpstreamEof,

    // ── Failure ──────────────────────────────────────────────────────

    /// <summary>Client cancelled before any data was sent downstream.</summary>
    StartupCancelledBeforeFirstByte,

    /// <summary>Upstream connection or read timed out.</summary>
    UpstreamTimeout,

    /// <summary>Upstream returned HTTP 401, 403, 404, or 5xx.</summary>
    UpstreamHttpError,

    /// <summary>Failed to write to the downstream client after the stream started.</summary>
    DownstreamWriteError,

    /// <summary>An unexpected exception occurred.</summary>
    UnexpectedException,

    /// <summary>Unknown end reason.</summary>
    Unknown,
}
