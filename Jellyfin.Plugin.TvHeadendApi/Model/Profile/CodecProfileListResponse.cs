using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Profile;

/// <summary>
/// Represents the TVHeadend /api/codec_profile/list response.
/// </summary>
internal sealed class CodecProfileListResponse
{
    [JsonPropertyName("entries")]
    public CodecProfileListEntry[] Entries { get; init; } = Array.Empty<CodecProfileListEntry>();
}
