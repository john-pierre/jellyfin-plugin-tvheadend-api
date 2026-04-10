using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Profile;

/// <summary>
/// Represents the TVHeadend /api/codec_profile/list response.
/// </summary>
internal sealed class TvhApiCodecProfileListResponse
{
    [JsonPropertyName("entries")]
    public TvhApiCodecProfileListEntry[] Entries { get; init; } = Array.Empty<TvhApiCodecProfileListEntry>();
}
