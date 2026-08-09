using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Subscription;

/// <summary>
/// Represents a single active subscription from the TVHeadend <c>/api/status/subscriptions</c> endpoint.
/// </summary>
public sealed class SubscriptionEntry
{
    /// <summary>
    /// Gets the subscription identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>
    /// Gets the subscription start time as a Unix epoch timestamp.
    /// </summary>
    [JsonPropertyName("start")]
    public long Start { get; init; }

    /// <summary>
    /// Gets the cumulative error count.
    /// </summary>
    [JsonPropertyName("errors")]
    public int Errors { get; init; }

    /// <summary>
    /// Gets the subscription state (Idle, Testing, Running, Bad).
    /// </summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    /// <summary>
    /// Gets the remote hostname.
    /// </summary>
    [JsonPropertyName("hostname")]
    public string Hostname { get; init; } = string.Empty;

    /// <summary>
    /// Gets the authenticated user name.
    /// </summary>
    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    /// <summary>
    /// Gets the client application identifier.
    /// </summary>
    [JsonPropertyName("client")]
    public string Client { get; init; } = string.Empty;

    /// <summary>
    /// Gets the subscription title.
    /// </summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Gets the channel name.
    /// </summary>
    [JsonPropertyName("channel")]
    public string Channel { get; init; } = string.Empty;

    /// <summary>
    /// Gets the service/adapter name.
    /// </summary>
    [JsonPropertyName("service")]
    public string Service { get; init; } = string.Empty;

    /// <summary>
    /// Gets the streaming profile name.
    /// </summary>
    [JsonPropertyName("profile")]
    public string Profile { get; init; } = string.Empty;

    /// <summary>
    /// Gets the current inbound byte rate.
    /// </summary>
    [JsonPropertyName("in")]
    public long In { get; init; }

    /// <summary>
    /// Gets the current outbound byte rate.
    /// </summary>
    [JsonPropertyName("out")]
    public long Out { get; init; }

    /// <summary>
    /// Gets the total inbound bytes.
    /// </summary>
    [JsonPropertyName("total_in")]
    public long TotalIn { get; init; }

    /// <summary>
    /// Gets the total outbound bytes.
    /// </summary>
    [JsonPropertyName("total_out")]
    public long TotalOut { get; init; }
}
