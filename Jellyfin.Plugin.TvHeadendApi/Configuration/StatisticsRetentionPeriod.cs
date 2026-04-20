namespace Jellyfin.Plugin.TvHeadendApi.Configuration;

/// <summary>
/// Defines how long viewing statistics are retained before being pruned.
/// </summary>
public enum StatisticsRetentionPeriod
{
    /// <summary>Sessions are kept for 30 days.</summary>
    ThirtyDays = 30,

    /// <summary>Sessions are kept for 6 months (180 days).</summary>
    SixMonths = 180,

    /// <summary>Sessions are kept for 1 year (365 days).</summary>
    OneYear = 365,

    /// <summary>Sessions are kept for 2 years (730 days).</summary>
    TwoYears = 730,

    /// <summary>Sessions are never automatically deleted.</summary>
    Forever = 0,
}
