using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Auth;

/// <summary>
/// Represents one TVHeadend user list entry.
/// </summary>
internal sealed class UserListEntry
{
    [JsonPropertyName("uuid")]
    public string? Uuid { get; set; }

    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("val")]
    public string? Val { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}
