using System;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Statistic;

/// <summary>
/// Represents a single health state transition event stored in SQLite for trend analysis.
/// </summary>
public sealed class HealthTransition
{
    /// <summary>Gets or sets the database ID (primary key).</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the UTC timestamp of the transition.</summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>Gets or sets the previous health status name.</summary>
    public string FromStatus { get; set; } = string.Empty;

    /// <summary>Gets or sets the new health status name.</summary>
    public string ToStatus { get; set; } = string.Empty;

    /// <summary>Gets or sets the failure reason (if applicable).</summary>
    public string? FailureReason { get; set; }

    /// <summary>Gets or sets the response time in milliseconds (if applicable).</summary>
    public int? ResponseTimeMs { get; set; }

    /// <summary>Gets or sets the consecutive failure count at the time of transition.</summary>
    public int ConsecutiveFailures { get; set; }
}
