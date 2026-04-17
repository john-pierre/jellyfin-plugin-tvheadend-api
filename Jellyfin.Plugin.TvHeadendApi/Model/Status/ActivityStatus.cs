using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Status;

/// <summary>
/// Represents the TVHeadend <c>/api/status/activity</c> response with next-activity scheduling.
/// </summary>
public sealed class ActivityStatus
{
    /// <summary>Gets the current server time as Unix epoch.</summary>
    [JsonPropertyName("current_time")]
    public long CurrentTime { get; init; }

    /// <summary>Gets the next scheduled activity time as Unix epoch (0 = none).</summary>
    [JsonPropertyName("next_activity")]
    public long NextActivity { get; init; }

    /// <summary>Gets the number of active subscriptions.</summary>
    [JsonPropertyName("subscription_count")]
    public int SubscriptionCount { get; init; }

    /// <summary>Gets the number of active connections.</summary>
    [JsonPropertyName("connection_count")]
    public int ConnectionCount { get; init; }
}
