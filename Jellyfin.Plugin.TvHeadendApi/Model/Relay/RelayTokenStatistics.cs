namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// Summary statistics for relay tokens, exposed via the dashboard.
/// </summary>
public sealed class RelayTokenStatistics
{
    /// <summary>Gets or sets the total number of tokens in the database.</summary>
    public int TotalCount { get; set; }

    /// <summary>Gets or sets the number of active (non-expired, non-revoked) stream tokens.</summary>
    public int ActiveStreamTokens { get; set; }

    /// <summary>Gets or sets the number of active (non-expired, non-revoked) image tokens.</summary>
    public int ActiveImageTokens { get; set; }

    /// <summary>Gets or sets the number of expired tokens awaiting cleanup.</summary>
    public int ExpiredCount { get; set; }

    /// <summary>Gets or sets the number of revoked tokens awaiting cleanup.</summary>
    public int RevokedCount { get; set; }
}
