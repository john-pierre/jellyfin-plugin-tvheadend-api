// Relay event record — lifecycle and diagnostic events for a streaming session.

using System;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Metrics;

/// <summary>
/// An individual relay lifecycle or diagnostic event attached to a session.
/// Stored in SQLite for audit trail and troubleshooting.
/// </summary>
public sealed class RelayEvent
{
    /// <summary>Gets or sets the auto-increment event ID.</summary>
    public long EventId { get; set; }

    /// <summary>Gets or sets when this event occurred (UTC).</summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>Gets or sets the session ID this event belongs to.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the event type (e.g. "session_started", "first_byte", "ended").</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Gets or sets the severity level (info, warning, error).</summary>
    public string Severity { get; set; } = "info";

    /// <summary>Gets or sets a human-readable message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets upstream status at event time.</summary>
    public string? UpstreamStatus { get; set; }

    /// <summary>Gets or sets downstream status at event time.</summary>
    public string? DownstreamStatus { get; set; }

    /// <summary>Gets or sets bytes sent snapshot at event time.</summary>
    public long? BytesSentSnapshot { get; set; }
}
