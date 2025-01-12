using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model;

/// <summary>
/// Represents a TV channel from the TVHeadEnd API.
/// This model is used to map the channel information returned by the TVHeadEnd `/api/channel/grid` endpoint.
/// A TV channel includes details such as its unique identifier, name, number, status, and associated metadata.
/// </summary>
public class TvhApiChannelGridEntry
{
    /// <summary>
    /// Gets the universally unique identifier (UUID) of the channel.
    /// This UUID is used by TVHeadEnd to uniquely identify the channel in its database.
    /// Example: "b33aaa0113626f7e8e08d4c04083ea4f".
    /// </summary>
    public string Uuid { get; init; } = string.Empty;

    /// <summary>
    /// Gets the display name of the channel.
    /// This is typically the name that viewers see in the TV guide or channel list.
    /// Example: "RTL2".
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the logical number of the channel.
    /// This number is used to sort and access the channel in a TV guide or user interface.
    /// Example: 101.
    /// </summary>
    public int Number { get; init; }

    /// <summary>
    /// Gets the URL to the channel's icon image.
    /// The icon is often used in TV guides and interfaces to visually represent the channel.
    /// Example: "file:///picons/rtl2.png".
    /// </summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>
    /// Gets the public URL of the channel's icon image.
    /// This URL points to a cached version of the icon that can be served over the network.
    /// Example: "imagecache/14".
    /// </summary>
    [JsonPropertyName("icon_public_url")]
    public string IconPublicUrl { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether the channel is enabled.
    /// If set to false, the channel is considered inactive or disabled.
    /// Example: true.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets a value indicating whether the EPG (Electronic Program Guide) is automatically enabled for this channel.
    /// Example: true.
    /// </summary>
    public bool EpgAuto { get; init; }

    /// <summary>
    /// Gets the EPG limit for this channel.
    /// This value defines the maximum number of EPG entries that can be fetched for this channel.
    /// Example: 0 (no limit).
    /// </summary>
    public int EpgLimit { get; init; }

    /// <summary>
    /// Gets the list of services associated with the channel.
    /// Each service is identified by its unique string identifier.
    /// Example: ["a2251f9e8569315b8de5c8411a1d35b6"].
    /// </summary>
    public IReadOnlyList<string> Services { get; init; } = new List<string>();

    /// <summary>
    /// Gets the list of tags associated with the channel.
    /// Tags are identifiers used to group or classify channels.
    /// Example: ["cfa6fbe8db7cbef6edcfb405cee836de"].
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = new List<string>();

    /// <summary>
    /// Gets the bouquet identifier for the channel.
    /// Bouquets are used to group channels into logical collections.
    /// Example: "" (empty string if no bouquet is assigned).
    /// </summary>
    public string Bouquet { get; init; } = string.Empty;
}
