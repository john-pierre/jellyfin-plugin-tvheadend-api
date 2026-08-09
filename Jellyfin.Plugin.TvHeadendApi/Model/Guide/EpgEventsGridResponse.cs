using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Guide;

/// <summary>
/// Represents the response model for the TVHeadEnd EPG grid API.
/// Maps the JSON response from the `/api/epg/events/grid` endpoint, providing program details for TV channels.
/// </summary>
public sealed class EpgEventsGridResponse
{
    /// <summary>
    /// Gets the list of EPG entries returned by the TVHeadEnd API.
    /// Each entry is represented as a <see cref="EpgEventsGridEntry"/> object.
    /// </summary>
    [JsonPropertyName("entries")]
    public IReadOnlyList<EpgEventsGridEntry> Entries { get; init; } = new List<EpgEventsGridEntry>();

    /// <summary>
    /// Gets the total number of entries available in the EPG system.
    /// This corresponds to the "totalCount" field in the JSON response.
    /// </summary>
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}
