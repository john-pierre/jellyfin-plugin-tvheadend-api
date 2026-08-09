using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Dvr;

/// <summary>
/// Represents a single TVHeadend DVR configuration entry from /api/idnode/load.
/// </summary>
internal sealed class DvrConfigListEntry
{
    /// <summary>
    /// Gets the profile name.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Gets the display text.
    /// </summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>
    /// Gets the value alias.
    /// </summary>
    [JsonPropertyName("val")]
    public string? Val { get; init; }

    /// <summary>
    /// Gets the best available display name across the possible field names.
    /// </summary>
    public string EffectiveName => Name ?? Text ?? Val ?? string.Empty;
}
