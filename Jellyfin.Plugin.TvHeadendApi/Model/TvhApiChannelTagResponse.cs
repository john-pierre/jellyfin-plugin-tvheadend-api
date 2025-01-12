using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model;

/// <summary>
/// Represents the response model for the TVHeadEnd channel tag API.
/// This class maps the JSON response returned by the `/api/channeltag/list` endpoint.
/// </summary>
public class TvhApiChannelTagResponse
{
    /// <summary>
    /// Gets the list of channel tags returned by the TVHeadEnd API.
    /// Each entry contains a key-value pair representing the tag ID and its description.
    /// </summary>
    [JsonPropertyName("entries")]
    public IReadOnlyList<TvhApiChannelTagEntry> Entries { get; init; } = new List<TvhApiChannelTagEntry>();
}
