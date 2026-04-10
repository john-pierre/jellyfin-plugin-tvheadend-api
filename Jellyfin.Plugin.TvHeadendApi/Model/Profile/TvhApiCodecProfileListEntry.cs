using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Profile;

/// <summary>
/// Represents one codec profile list entry from TVHeadend.
/// </summary>
internal sealed class TvhApiCodecProfileListEntry
{
    [JsonPropertyName("uuid")]
    public string Uuid { get; init; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("val")]
    public string Val { get; init; } = string.Empty;

    public string EffectiveUuid => string.IsNullOrWhiteSpace(Uuid) ? Key : Uuid;

    public string EffectiveTitle => string.IsNullOrWhiteSpace(Title) ? Val : Title;
}
