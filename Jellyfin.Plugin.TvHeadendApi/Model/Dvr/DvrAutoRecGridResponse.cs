using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Dvr;

/// <summary>
/// Represents the response model for the TVHeadEnd DVR autorec grid API.
/// Maps the JSON response from the `/api/dvr/autorec/grid` endpoint, providing details about automatic recording rules.
/// </summary>
public sealed class DvrAutoRecGridResponse
{
    /// <summary>
    /// Gets the list of DVR automatic recording rules returned by the TVHeadEnd API.
    /// Each rule is represented as a <see cref="DvrAutoRecGridEntry"/> object.
    /// </summary>
    [JsonPropertyName("entries")]
    public IReadOnlyList<DvrAutoRecGridEntry> Entries { get; init; } = new List<DvrAutoRecGridEntry>();

    /// <summary>
    /// Gets the total number of automatic recording rules available in the TVHeadEnd system.
    /// This corresponds to the "total" field in the JSON response.
    /// Example: 1.
    /// </summary>
    [JsonPropertyName("total")]
    public int Total { get; init; }
}
