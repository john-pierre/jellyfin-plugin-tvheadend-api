using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Subscription;

/// <summary>
/// Grid response wrapper for <c>/api/status/subscriptions</c>.
/// </summary>
public sealed class SubscriptionGridResponse
{
    /// <summary>
    /// Gets the list of active subscriptions.
    /// </summary>
    [JsonPropertyName("entries")]
    public IReadOnlyList<SubscriptionEntry> Entries { get; init; } = [];

    /// <summary>
    /// Gets the total number of subscriptions.
    /// </summary>
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}
