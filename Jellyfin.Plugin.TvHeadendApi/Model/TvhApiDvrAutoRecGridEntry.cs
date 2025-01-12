using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model;

/// <summary>
/// Represents an automatic recording rule in TVHeadEnd.
/// Maps the details of a DVR autorec rule as returned by the TVHeadEnd `/api/dvr/autorec/grid` endpoint.
/// </summary>
public class TvhApiDvrAutoRecGridEntry
{
    /// <summary>
    /// Gets the type of broadcast for the auto-recording rule.
    /// Corresponds to the "btype" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("btype")]
    public int BroadcastType { get; init; }

    /// <summary>
    /// Gets the channel UUID associated with the auto-recording rule.
    /// Corresponds to the "channel" field in the JSON response.
    /// Example: "cb027d8e79e8dca308f6636f79b0d47a".
    /// </summary>
    [JsonPropertyName("channel")]
    public string Channel { get; init; } = string.Empty;

    /// <summary>
    /// Gets the configuration name for the auto-recording rule.
    /// Corresponds to the "config_name" field in the JSON response.
    /// Example: "7a5edfbe189851e5b1d1df19c93962f0".
    /// </summary>
    [JsonPropertyName("config_name")]
    public string ConfigName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the content type for the auto-recording rule.
    /// Corresponds to the "content_type" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("content_type")]
    public int ContentType { get; init; }

    /// <summary>
    /// Gets the creator of the auto-recording rule.
    /// Corresponds to the "creator" field in the JSON response.
    /// Example: "root".
    /// </summary>
    [JsonPropertyName("creator")]
    public string Creator { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether the auto-recording rule is enabled.
    /// Corresponds to the "enabled" field in the JSON response.
    /// Example: true.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets a value indicating whether full text search is enabled for the auto-recording rule.
    /// Corresponds to the "fulltext" field in the JSON response.
    /// Example: false.
    /// </summary>
    [JsonPropertyName("fulltext")]
    public bool FullText { get; init; }

    /// <summary>
    /// Gets the series link associated with the auto-recording rule.
    /// Corresponds to the "serieslink" field in the JSON response.
    /// Example: "crid://www.five.tv/R5HP0".
    /// </summary>
    [JsonPropertyName("serieslink")]
    public string SeriesLink { get; init; } = string.Empty;

    /// <summary>
    /// Gets the season information for the auto-recording rule.
    /// Corresponds to the "season" field in the JSON response.
    /// Example: "".
    /// </summary>
    [JsonPropertyName("season")]
    public string Season { get; init; } = string.Empty;

    /// <summary>
    /// Gets the maximum number of recordings allowed for the auto-recording rule.
    /// Corresponds to the "maxcount" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("maxcount")]
    public int MaxCount { get; init; }

    /// <summary>
    /// Gets the maximum duration of recordings allowed for the auto-recording rule.
    /// Corresponds to the "maxduration" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("maxduration")]
    public int MaxDuration { get; init; }

    /// <summary>
    /// Gets the maximum number of scheduled recordings for the auto-recording rule.
    /// Corresponds to the "maxsched" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("maxsched")]
    public int MaxScheduled { get; init; }

    /// <summary>
    /// Gets the maximum season for the auto-recording rule.
    /// Corresponds to the "maxseason" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("maxseason")]
    public int MaxSeason { get; init; }

    /// <summary>
    /// Gets the maximum year for the auto-recording rule.
    /// Corresponds to the "maxyear" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("maxyear")]
    public int MaxYear { get; init; }

    /// <summary>
    /// Gets the minimum duration of recordings allowed for the auto-recording rule.
    /// Corresponds to the "minduration" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("minduration")]
    public int MinDuration { get; init; }

    /// <summary>
    /// Gets the minimum season for the auto-recording rule.
    /// Corresponds to the "minseason" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("minseason")]
    public int MinSeason { get; init; }

    /// <summary>
    /// Gets the minimum year for the auto-recording rule.
    /// Corresponds to the "minyear" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("minyear")]
    public int MinYear { get; init; }

    /// <summary>
    /// Gets the name of the auto-recording rule.
    /// Corresponds to the "name" field in the JSON response.
    /// Example: "test".
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the owner of the auto-recording rule.
    /// Corresponds to the "owner" field in the JSON response.
    /// Example: "root".
    /// </summary>
    [JsonPropertyName("owner")]
    public string Owner { get; init; } = string.Empty;

    /// <summary>
    /// Gets the priority of the auto-recording rule.
    /// Corresponds to the "pri" field in the JSON response.
    /// Example: 6.
    /// </summary>
    [JsonPropertyName("pri")]
    public int Priority { get; init; }

    /// <summary>
    /// Gets the recording mode for the auto-recording rule.
    /// Corresponds to the "record" field in the JSON response.
    /// Example: 15.
    /// </summary>
    [JsonPropertyName("record")]
    public int RecordMode { get; init; }

    /// <summary>
    /// Gets the removal time for recordings created by the auto-recording rule.
    /// Corresponds to the "removal" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("removal")]
    public int Removal { get; init; }

    /// <summary>
    /// Gets the retention time for recordings created by the auto-recording rule.
    /// Corresponds to the "retention" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("retention")]
    public int Retention { get; init; }

    /// <summary>
    /// Gets the star rating for the auto-recording rule.
    /// Corresponds to the "star_rating" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("star_rating")]
    public int StarRating { get; init; }

    /// <summary>
    /// Gets the start time for the auto-recording rule.
    /// Corresponds to the "start" field in the JSON response.
    /// Example: "Any".
    /// </summary>
    [JsonPropertyName("start")]
    public string Start { get; init; } = string.Empty;

    /// <summary>
    /// Gets the extra start time for the auto-recording rule in seconds.
    /// Corresponds to the "start_extra" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("start_extra")]
    public int StartExtra { get; init; }

    /// <summary>
    /// Gets the start window for the auto-recording rule.
    /// Corresponds to the "start_window" field in the JSON response.
    /// Example: "Any".
    /// </summary>
    [JsonPropertyName("start_window")]
    public string StartWindow { get; init; } = string.Empty;

    /// <summary>
    /// Gets the extra stop time for the auto-recording rule in seconds.
    /// Corresponds to the "stop_extra" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("stop_extra")]
    public int StopExtra { get; init; }

    /// <summary>
    /// Gets the tag associated with the auto-recording rule.
    /// Corresponds to the "tag" field in the JSON response.
    /// Example: "".
    /// </summary>
    [JsonPropertyName("tag")]
    public string Tag { get; init; } = string.Empty;

    /// <summary>
    /// Gets the title for the auto-recording rule.
    /// Corresponds to the "title" field in the JSON response.
    /// Example: "baum".
    /// </summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Gets the comment for the auto-recording rule.
    /// Corresponds to the "comment" field in the JSON response.
    /// Example: "Created from EPG query".
    /// </summary>
    [JsonPropertyName("comment")]
    public string Comment { get; init; } = string.Empty;

    /// <summary>
    /// Gets the UUID of the auto-recording rule.
    /// Corresponds to the "uuid" field in the JSON response.
    /// Example: "6824d98d296cc926a312deed00134d65".
    /// </summary>
    [JsonPropertyName("uuid")]
    public string Uuid { get; init; } = string.Empty;

    /// <summary>
    /// Gets the weekdays associated with the auto-recording rule.
    /// Corresponds to the "weekdays" field in the JSON response.
    /// Example: [].
    /// </summary>
    [JsonPropertyName("weekdays")]
    public IReadOnlyList<int> Weekdays { get; init; } = new List<int>();

    /// <summary>
    /// Gets the brand information for the auto-recording rule.
    /// Corresponds to the "brand" field in the JSON response.
    /// Example: "".
    /// </summary>
    [JsonPropertyName("brand")]
    public string Brand { get; init; } = string.Empty;
}
