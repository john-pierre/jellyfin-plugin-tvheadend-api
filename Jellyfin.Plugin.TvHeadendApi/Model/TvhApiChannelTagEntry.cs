using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model;

/// <summary>
/// Represents a single entry in the channel tag list.
/// </summary>
public class TvhApiChannelTagEntry
{
    /// <summary>
    /// Gets the unique key (ID) of the channel tag.
    /// Example: "721356e8bf5129df2756012b73b11cc3".
    /// </summary>
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    /// <summary>
    /// Gets the description of the channel tag.
    /// Example: "HDTV".
    /// </summary>
    [JsonPropertyName("val")]
    public string Val { get; init; } = string.Empty;
}
