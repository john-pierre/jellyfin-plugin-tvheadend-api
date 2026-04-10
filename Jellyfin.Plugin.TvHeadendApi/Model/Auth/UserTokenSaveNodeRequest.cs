using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Auth;

/// <summary>
/// Represents the payload for /api/idnode/save when creating or refreshing a user auth token.
/// </summary>
internal sealed class UserTokenSaveNodeRequest
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; init; } = string.Empty;

    [JsonPropertyName("auth")]
    public string[] Auth { get; init; } = System.Array.Empty<string>();

    [JsonPropertyName("comment")]
    public string Comment { get; init; } = string.Empty;

    [JsonPropertyName("uuid")]
    public string Uuid { get; init; } = string.Empty;
}
