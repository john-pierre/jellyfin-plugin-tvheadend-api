// Dashboard API response for a single session detail view.

namespace Jellyfin.Plugin.TvHeadendApi.Model.Metrics;

/// <summary>
/// Full session detail response returned by GET /metrics/session/{id}.
/// The former per-session event timeline was removed together with the
/// <c>relay_events</c> table — the session row itself is the complete record.
/// </summary>
public sealed class SessionMetricsResponse
{
    /// <summary>Gets or sets the completed session data.</summary>
    public CompletedStreamSession? Session { get; set; }
}
