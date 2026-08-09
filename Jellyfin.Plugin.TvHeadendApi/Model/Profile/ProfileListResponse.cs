using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Profile;

/// <summary>
/// Represents the TVHeadend /api/profile/list response.
/// </summary>
internal sealed class ProfileListResponse
{
    [JsonPropertyName("entries")]
    public ProfileListEntry[] Entries { get; init; } = Array.Empty<ProfileListEntry>();
}
