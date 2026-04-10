using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Dvr;

/// <summary>
/// Represents the TVHeadend /api/idnode/load response for the dvrconfig class.
/// </summary>
internal sealed class DvrConfigListResponse
{
    /// <summary>
    /// Gets the DVR configuration entries.
    /// </summary>
    [JsonPropertyName("entries")]
    public DvrConfigListEntry[] Entries { get; init; } = Array.Empty<DvrConfigListEntry>();
}
