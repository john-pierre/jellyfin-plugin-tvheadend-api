using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model;

/// <summary>
/// Represents the response model for the TVHeadEnd content type API.
/// This class maps the JSON response returned by the `/api/epg/content_type/list` endpoint.
/// </summary>
public class TvhApiEpgContentTypeListResponse
{
    /// <summary>
    /// Gets the list of content types returned by the TVHeadEnd API.
    /// Each entry contains a key-value pair representing the content type ID and its description.
    /// </summary>
    [JsonPropertyName("entries")]
    public IReadOnlyList<TvhApiEpgContentTypeListEntry> Entries { get; init; } = new List<TvhApiEpgContentTypeListEntry>();
}
