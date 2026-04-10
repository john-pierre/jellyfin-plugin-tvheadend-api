using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Profile;

/// <summary>
/// Represents one profile entry from TVHeadend /api/profile/list.
/// </summary>
internal sealed class ProfileListEntry
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("val")]
    public string Val { get; init; } = string.Empty;
}
