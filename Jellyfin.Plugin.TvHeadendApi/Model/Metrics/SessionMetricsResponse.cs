// Dashboard API response for a single session detail view.

using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Metrics;

/// <summary>
/// Full session detail response returned by GET /metrics/session/{id}.
/// Includes all lifecycle timestamps, metrics, and event timeline.
/// </summary>
public sealed class SessionMetricsResponse
{
    /// <summary>Gets or sets the completed session data.</summary>
    public CompletedStreamSession? Session { get; set; }

    /// <summary>Gets or sets the event timeline for this session.</summary>
    public List<RelayEvent> Events { get; set; } = new();
}
