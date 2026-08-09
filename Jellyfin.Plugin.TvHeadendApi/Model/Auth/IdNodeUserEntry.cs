using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Auth;

/// <summary>
/// Represents one TVHeadend passwd idnode entry as returned by /api/idnode/load.
/// Field names match the passwd_entry_class idnode property IDs (access.c).
/// </summary>
internal sealed class IdNodeUserEntry
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("authcode")]
    public string? AuthCode { get; set; }
}
