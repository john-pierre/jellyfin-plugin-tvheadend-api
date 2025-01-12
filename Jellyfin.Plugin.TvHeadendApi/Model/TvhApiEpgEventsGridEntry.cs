using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model;

/// <summary>
/// Represents an individual program entry in the TVHeadEnd EPG.
/// This model maps the fields from the TVHeadEnd API response exactly.
/// </summary>
public class TvhApiEpgEventsGridEntry
{
    /// <summary>
    /// Gets the unique identifier for the event.
    /// Corresponds to the "EventId" field in the JSON response.
    /// Example: 510178.
    /// </summary>
    [JsonPropertyName("eventId")]
    public int EventId { get; init; }

    /// <summary>
    /// Gets the UUID of the channel associated with the event.
    /// Corresponds to the "ChannelUuid" field in the JSON response.
    /// Example: "e1b1de65605dc4e13bac6d4478e66a44".
    /// </summary>
    [JsonPropertyName("channelUuid")]
    public string ChannelUuid { get; init; } = string.Empty;

    /// <summary>
    /// Gets the channel name associated with the event.
    /// Corresponds to the "ChannelName" field in the JSON response.
    /// Example: "More 4".
    /// </summary>
    [JsonPropertyName("channelName")]
    public string ChannelName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the channel number.
    /// Corresponds to the "ChannelNumber" field in the JSON response.
    /// Example: "14".
    /// </summary>
    [JsonPropertyName("channelNumber")]
    public string ChannelNumber { get; init; } = string.Empty;

    /// <summary>
    /// Gets the channel icon URL.
    /// Corresponds to the "ChannelIcon" field in the JSON response.
    /// Example: "imagecache/37".
    /// </summary>
    [JsonPropertyName("channelIcon")]
    public string ChannelIcon { get; init; } = string.Empty;

    /// <summary>
    /// Gets the title of the program.
    /// Corresponds to the "Title" field in the JSON response.
    /// Example: "Time Team".
    /// </summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Gets the subtitle of the program.
    /// Corresponds to the "Subtitle" field in the JSON response.
    /// Example: "Tony Robinson and the Team...".
    /// </summary>
    [JsonPropertyName("subtitle")]
    public string Subtitle { get; init; } = string.Empty;

    /// <summary>
    /// Gets the description of the program.
    /// Corresponds to the "Description" field in the JSON response.
    /// Example: "Eishockey DEL 33. Spieltag".
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Gets the summary of the program.
    /// Corresponds to the "Summary" field in the JSON response.
    /// Example: "Tony Robinson examines Bronze Age villages".
    /// </summary>
    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// Gets the Unix timestamp for the start time.
    /// Corresponds to the "Start" field in the JSON response.
    /// Example: 1508760600.
    /// </summary>
    [JsonPropertyName("start")]
    public long Start { get; init; }

    /// <summary>
    /// Gets the Unix timestamp for the stop time.
    /// Corresponds to the "Stop" field in the JSON response.
    /// Example: 1508764500.
    /// </summary>
    [JsonPropertyName("stop")]
    public long Stop { get; init; }

    /// <summary>
    /// Gets the genres associated with the program.
    /// Corresponds to the "Genre" field in the JSON response.
    /// Example: [160].
    /// </summary>
    [JsonPropertyName("genre")]
    public IReadOnlyList<int> Genre { get; init; } = new List<int>();

    /// <summary>
    /// Gets the next event ID for the channel.
    /// Corresponds to the "NextEventId" field in the JSON response.
    /// Example: 510181.
    /// </summary>
    [JsonPropertyName("nextEventId")]
    public int? NextEventId { get; init; }

    /// <summary>
    /// Gets a value indicating whether the program is in widescreen format.
    /// Corresponds to the "Widescreen" field in the JSON response.
    /// Example: 1.
    /// </summary>
    [JsonPropertyName("widescreen")]
    public int? IsWidescreen { get; init; }

    /// <summary>
    /// Gets a value indicating whether the program is in HD format.
    /// Corresponds to the "Hd" field in the JSON response.
    /// Example: 1.
    /// </summary>
    [JsonPropertyName("hd")]
    public int? IsHD { get; init; }

    /// <summary>
    /// Gets a value indicating whether the program includes subtitles.
    /// Corresponds to the "Subtitled" field in the JSON response.
    /// Example: 1.
    /// </summary>
    [JsonPropertyName("subtitled")]
    public int? IsSubtitled { get; init; }

    /// <summary>
    /// Gets a value indicating whether the program includes audio description.
    /// Corresponds to the "Audiodesc" field in the JSON response.
    /// Example: 1.
    /// </summary>
    [JsonPropertyName("audiodesc")]
    public int? Audiodesc { get; init; }

    /// <summary>
    /// Gets the age rating for the program.
    /// Corresponds to the "AgeRating" field in the JSON response.
    /// Example: 9.
    /// </summary>
    [JsonPropertyName("ageRating")]
    public int? AgeRating { get; init; }

    /// <summary>
    /// Gets the rating label for the program.
    /// Corresponds to the "RatingLabel" field in the JSON response.
    /// Example: "PG".
    /// </summary>
    [JsonPropertyName("ratingLabel")]
    public string RatingLabel { get; init; } = string.Empty;

    /// <summary>
    /// Gets the URL of the rating label icon.
    /// Corresponds to the "RatingLabelIcon" field in the JSON response.
    /// Example: "imagecache/37".
    /// </summary>
    [JsonPropertyName("ratingLabelIcon")]
    public string RatingLabelIcon { get; init; } = string.Empty;

    /// <summary>
    /// Gets the series link URI for the program.
    /// Corresponds to the "SerieslinkUri" field in the JSON response.
    /// Example: "crid://www.channel4.com/M4EI0021031162146302".
    /// </summary>
    [JsonPropertyName("serieslinkUri")]
    public string SerieslinkUri { get; init; } = string.Empty;

    /// <summary>
    /// Gets the series link ID for the program.
    /// Corresponds to the "SerieslinkId" field in the JSON response.
    /// Example: 510179.
    /// </summary>
    [JsonPropertyName("serieslinkId")]
    public int? SerieslinkId { get; init; }

    /// <summary>
    /// Gets the episode ID for the program.
    /// Corresponds to the "EpisodeId" field in the JSON response.
    /// Example: 510180.
    /// </summary>
    [JsonPropertyName("episodeId")]
    public int? EpisodeId { get; init; }

    /// <summary>
    /// Gets the episode URI for the program.
    /// Corresponds to the "EpisodeUri" field in the JSON response.
    /// Example: "crid://www.channel4.com/41408/013".
    /// </summary>
    [JsonPropertyName("episodeUri")]
    public string EpisodeUri { get; init; } = string.Empty;

    /// <summary>
    /// Gets the copyright year for the event.
    /// Corresponds to the "copyright_year" field in the JSON response.
    /// Example: 2022.
    /// </summary>
    [JsonPropertyName("copyright_year")]
    public int CopyrightYear { get; init; }

    /// <summary>
    /// Gets the image URL for the event.
    /// Corresponds to the "image" field in the JSON response.
    /// Example: "https://images.de/673dfeb05fadd7f653.webp".
    /// </summary>
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    /// <summary>
    /// Gets the categories for the event.
    /// Corresponds to the "category" field in the JSON response.
    /// Example: [ "nachrichten" ].
    /// </summary>
    [JsonPropertyName("category")]
    public IReadOnlyList<string> Category { get; init; } = new List<string>();
}
