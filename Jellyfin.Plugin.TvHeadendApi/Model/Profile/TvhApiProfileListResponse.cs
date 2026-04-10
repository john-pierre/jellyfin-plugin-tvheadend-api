using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Profile;

/// <summary>
/// Represents the TVHeadend /api/profile/list response.
/// </summary>
internal sealed class TvhApiProfileListResponse
{
    [JsonPropertyName("entries")]
    public TvhApiProfileListEntry[] Entries { get; init; } = Array.Empty<TvhApiProfileListEntry>();
}
