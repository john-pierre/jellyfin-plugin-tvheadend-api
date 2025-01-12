using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model;

/// <summary>
/// Represents a single entry in the content type list.
/// </summary>
public class TvhApiEpgContentTypeListEntry
{
    /// <summary>
    /// Gets the unique key (ID) of the content type.
    /// Example: 16 (for "Film / Drama").
    /// </summary>
    [JsonPropertyName("key")]
    public int Key { get; init; }

    /// <summary>
    /// Gets the description of the content type.
    /// Example: "Film / Drama".
    /// </summary>
    [JsonPropertyName("val")]
    public string Val { get; init; } = string.Empty;
}
