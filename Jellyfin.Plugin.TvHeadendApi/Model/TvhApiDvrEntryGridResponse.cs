using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model;

/// <summary>
/// Represents the response model for the TVHeadEnd recording grid API.
/// Maps the JSON response from the `/api/dvr/entry/grid` endpoint, providing details about recordings.
/// </summary>
public class TvhApiDvrEntryGridResponse
{
    /// <summary>
    /// Gets the list of recordings returned by the TVHeadEnd API.
    /// Each recording is represented as a <see cref="TvhApiDvrEntryGridEntry"/> object.
    /// </summary>
    [JsonPropertyName("entries")]
    public IReadOnlyList<TvhApiDvrEntryGridEntry> Entries { get; init; } = new List<TvhApiDvrEntryGridEntry>();

    /// <summary>
    /// Gets the total number of recordings available in the TVHeadEnd system.
    /// This corresponds to the "total" field in the JSON response.
    /// </summary>
    [JsonPropertyName("total")]
    public int Total { get; init; }
}
