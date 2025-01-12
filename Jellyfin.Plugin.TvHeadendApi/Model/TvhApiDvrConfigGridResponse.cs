using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model;

/// <summary>
/// Represents the response from the `/api/dvr/config/grid` endpoint.
/// </summary>
public class TvhApiDvrConfigGridResponse
{
    /// <summary>
    /// Gets the list of recording profiles.
    /// Corresponds to the "entries" field in the JSON response.
    /// </summary>
    [JsonPropertyName("entries")]
    public IReadOnlyList<TvhApiDvrConfigGridEntry> Entries { get; init; } = new List<TvhApiDvrConfigGridEntry>();

    /// <summary>
    /// Gets the total number of recording profiles.
    /// Corresponds to the "total" field in the JSON response.
    /// Example: 1.
    /// </summary>
    [JsonPropertyName("total")]
    public int Total { get; init; }
}
