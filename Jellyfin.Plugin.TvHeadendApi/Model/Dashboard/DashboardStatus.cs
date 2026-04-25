using System;
using System.Collections.Generic;
using Jellyfin.Plugin.TvHeadendApi.Model.Input;
using Jellyfin.Plugin.TvHeadendApi.Model.Status;
using Jellyfin.Plugin.TvHeadendApi.Model.Subscription;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Dashboard;

/// <summary>
/// Aggregated dashboard status returned by the dashboard API endpoint.
/// Contains connection health, tuner state, subscriptions, and summary statistics.
/// </summary>
public sealed class DashboardStatus
{
    // ── Connection / Backend ────────────────────────────────────────────

    /// <summary>
    /// Gets or sets a value indicating whether the TVHeadend backend is reachable.
    /// </summary>
    public bool IsReachable { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether authentication succeeded.
    /// </summary>
    public bool IsAuthenticated { get; set; }

    /// <summary>
    /// Gets or sets the configured TVHeadend base URL (scheme + host + port).
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the TVHeadend server version string.
    /// </summary>
    public string ServerVersion { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the network latency to TVHeadend in milliseconds.
    /// </summary>
    public int LatencyMs { get; set; }

    /// <summary>
    /// Gets or sets the last error message if the connection failed.
    /// </summary>
    public string? ConnectionError { get; set; }

    /// <summary>
    /// Gets or sets the plugin version string.
    /// </summary>
    public string PluginVersion { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the UTC timestamp of this dashboard snapshot.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    // ── Activity ────────────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the server activity status (connection/subscription counts).
    /// </summary>
    public ActivityStatus? Activity { get; set; }

    // ── Tuners / Inputs ─────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the list of active TV inputs (tuners/adapters).
    /// </summary>
    public IReadOnlyList<InputStatusEntry> Inputs { get; set; } = Array.Empty<InputStatusEntry>();

    /// <summary>
    /// Gets or sets the error message if tuner query failed.
    /// </summary>
    public string? InputsError { get; set; }

    // ── Subscriptions ───────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the list of active streaming subscriptions.
    /// </summary>
    public IReadOnlyList<SubscriptionEntry> Subscriptions { get; set; } = Array.Empty<SubscriptionEntry>();

    /// <summary>
    /// Gets or sets the error message if subscription query failed.
    /// </summary>
    public string? SubscriptionsError { get; set; }

    // ── Connections ─────────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the list of active client connections.
    /// </summary>
    public IReadOnlyList<ConnectionEntry> Connections { get; set; } = Array.Empty<ConnectionEntry>();

    /// <summary>
    /// Gets or sets the error message if connection query failed.
    /// </summary>
    public string? ConnectionsError { get; set; }

    // ── Channel / EPG summary ───────────────────────────────────────────

    /// <summary>
    /// Gets or sets the total channel count.
    /// </summary>
    public int ChannelCount { get; set; }

    /// <summary>
    /// Gets or sets the DVR entry count.
    /// </summary>
    public int DvrEntryCount { get; set; }

    /// <summary>
    /// Gets or sets the compatibility score from diagnostics (0–100).
    /// </summary>
    public int CompatibilityScore { get; set; }

    /// <summary>
    /// Gets or sets the overall diagnostic status (OK / WARNING / ERROR).
    /// </summary>
    public string DiagnosticStatus { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets any warnings from the diagnostic check.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();

    // ── TVHeadend Health ─────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the TVHeadend upstream health snapshot.
    /// </summary>
    public HealthSnapshot? UpstreamHealth { get; set; }
}
