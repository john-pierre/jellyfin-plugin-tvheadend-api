using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Status;

/// <summary>
/// Grid response wrapper for <c>/api/status/connections</c>.
/// </summary>
public sealed class ConnectionGridResponse
{
    /// <summary>Gets the list of active connections.</summary>
    [JsonPropertyName("entries")]
    public IReadOnlyList<ConnectionEntry> Entries { get; init; } = [];

    /// <summary>Gets the total number of connections.</summary>
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}
