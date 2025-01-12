using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model;

/// <summary>
/// Represents the response model for the TVHeadEnd channel grid API.
/// This class maps the JSON response returned by the `/api/channel/grid` endpoint,
/// which provides a list of all available TV channels and additional metadata about the response.
/// </summary>
public class TvhApiChannelGridResponse
{
    /// <summary>
    /// Gets the list of TV channels returned by the TVHeadEnd API.
    /// Each channel is represented as a <see cref="TvhApiChannelGridEntry"/> object.
    /// The `Entries` property corresponds to the "entries" field in the JSON response.
    /// </summary>
    [JsonPropertyName("entries")]
    public IReadOnlyList<TvhApiChannelGridEntry> Entries { get; init; } = new List<TvhApiChannelGridEntry>();

    /// <summary>
    /// Gets the total number of channels available in the TVHeadEnd system.
    /// This property corresponds to the "total" field in the JSON response.
    /// It indicates the total count of channels, regardless of how many are included in the "entries" list.
    /// Example: 45.
    /// </summary>
    [JsonPropertyName("total")]
    public int Total { get; init; }
}
