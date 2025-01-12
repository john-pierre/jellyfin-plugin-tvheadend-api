using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model;

/// <summary>
/// Represents a recording from the TVHeadEnd API.
/// Maps all fields from the `/api/dvr/entry/grid` response.
/// </summary>
public class TvhApiDvrEntryGridEntry
{
    /// <summary>
    /// Gets the age rating for the recording.
    /// Corresponds to the "age_rating" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("age_rating")]
    public int AgeRating { get; init; }

    /// <summary>
    /// Gets the autorec ID associated with the recording.
    /// Corresponds to the "autorec" field in the JSON response.
    /// Example: "".
    /// </summary>
    [JsonPropertyName("autorec")]
    public string AutoRec { get; init; } = string.Empty;

    /// <summary>
    /// Gets the caption for the autorec.
    /// Corresponds to the "autorec_caption" field in the JSON response.
    /// Example: "".
    /// </summary>
    [JsonPropertyName("autorec_caption")]
    public string AutoRecCaption { get; init; } = string.Empty;

    /// <summary>
    /// Gets the broadcast ID for the recording.
    /// Corresponds to the "broadcast" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("broadcast")]
    public int Broadcast { get; init; }

    /// <summary>
    /// Gets the categories associated with the recording.
    /// Corresponds to the "category" field in the JSON response.
    /// Example: [].
    /// </summary>
    [JsonPropertyName("category")]
    public IReadOnlyList<string> Category { get; init; } = new List<string>();

    /// <summary>
    /// Gets the channel UUID associated with the recording.
    /// Corresponds to the "channel" field in the JSON response.
    /// Example: "6c038f7ea7caa46af45e943926e08bb2".
    /// </summary>
    [JsonPropertyName("channel")]
    public string Channel { get; init; } = string.Empty;

    /// <summary>
    /// Gets the channel icon URL.
    /// Corresponds to the "channel_icon" field in the JSON response.
    /// Example: "imagecache/25".
    /// </summary>
    [JsonPropertyName("channel_icon")]
    public string ChannelIcon { get; init; } = string.Empty;

    /// <summary>
    /// Gets the name of the channel.
    /// Corresponds to the "channelname" field in the JSON response.
    /// Example: "VOX".
    /// </summary>
    [JsonPropertyName("channelname")]
    public string ChannelName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the child ID for the recording, if applicable.
    /// Corresponds to the "child" field in the JSON response.
    /// Example: "".
    /// </summary>
    [JsonPropertyName("child")]
    public string Child { get; init; } = string.Empty;

    /// <summary>
    /// Gets the configuration name for the recording.
    /// Corresponds to the "config_name" field in the JSON response.
    /// Example: "7a5edfbe189851e5b1d1df19c93962f0".
    /// </summary>
    [JsonPropertyName("config_name")]
    public string ConfigName { get; init; } = string.Empty;

    /// <summary>
    /// Gets the content type of the recording.
    /// Corresponds to the "content_type" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("content_type")]
    public int ContentType { get; init; }

    /// <summary>
    /// Gets the copyright year of the recording.
    /// Corresponds to the "copyright_year" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("copyright_year")]
    public int CopyrightYear { get; init; }

    /// <summary>
    /// Gets the Unix timestamp for when the recording was created.
    /// Corresponds to the "create" field in the JSON response.
    /// Example: 1735598123.
    /// </summary>
    [JsonPropertyName("create")]
    public long Create { get; init; }

    /// <summary>
    /// Gets the creator of the recording.
    /// Corresponds to the "creator" field in the JSON response.
    /// Example: "jellyfin".
    /// </summary>
    [JsonPropertyName("creator")]
    public string Creator { get; init; } = string.Empty;

    /// <summary>
    /// Gets the credits associated with the recording.
    /// Corresponds to the "credits" field in the JSON response.
    /// Example: {}.
    /// </summary>
    [JsonPropertyName("credits")]
    public IReadOnlyDictionary<string, string> Credits { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Gets the data errors associated with the recording.
    /// Corresponds to the "data_errors" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("data_errors")]
    public int DataErrors { get; init; }

    /// <summary>
    /// Gets the localized description of the recording.
    /// Corresponds to the "description" field in the JSON response.
    /// Example: { "eng": "Description text." }.
    /// </summary>
    [JsonPropertyName("description")]
    public IReadOnlyDictionary<string, string> Description { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Gets the human-readable description of the recording.
    /// Corresponds to the "disp_description" field in the JSON response.
    /// Example: "Detailed description text.".
    /// </summary>
    [JsonPropertyName("disp_description")]
    public string DispDescription { get; init; } = string.Empty;

    /// <summary>
    /// Gets the extra text for the recording display.
    /// Corresponds to the "disp_extratext" field in the JSON response.
    /// Example: "Extra details about the recording.".
    /// </summary>
    [JsonPropertyName("disp_extratext")]
    public string DispExtraText { get; init; } = string.Empty;

    /// <summary>
    /// Gets the subtitle of the recording.
    /// Corresponds to the "disp_subtitle" field in the JSON response.
    /// Example: "Subtitle text.".
    /// </summary>
    [JsonPropertyName("disp_subtitle")]
    public string DispSubtitle { get; init; } = string.Empty;

    /// <summary>
    /// Gets the summary for the recording display.
    /// Corresponds to the "disp_summary" field in the JSON response.
    /// Example: "Short summary text.".
    /// </summary>
    [JsonPropertyName("disp_summary")]
    public string DispSummary { get; init; } = string.Empty;

    /// <summary>
    /// Gets the title for the recording display.
    /// Corresponds to the "disp_title" field in the JSON response.
    /// Example: "Medical Detectives - Geheimnisse der Gerichtsmedizin".
    /// </summary>
    [JsonPropertyName("disp_title")]
    public string DispTitle { get; init; } = string.Empty;

    /// <summary>
    /// Gets the duplicate flag for the recording.
    /// Corresponds to the "duplicate" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("duplicate")]
    public int Duplicate { get; init; }

    /// <summary>
    /// Gets the duration of the recording in seconds.
    /// Corresponds to the "duration" field in the JSON response.
    /// Example: 2700.
    /// </summary>
    [JsonPropertyName("duration")]
    public int Duration { get; init; }

    /// <summary>
    /// Gets the DVB Event ID for the recording.
    /// Corresponds to the "dvb_eid" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("dvb_eid")]
    public int DvbEid { get; init; }

    /// <summary>
    /// Gets a value indicating whether the recording is enabled.
    /// Corresponds to the "enabled" field in the JSON response.
    /// Example: true.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets the displayed episode information for the recording.
    /// Corresponds to the "episode_disp" field in the JSON response.
    /// Example: "".
    /// </summary>
    [JsonPropertyName("episode_disp")]
    public string EpisodeDisp { get; init; } = string.Empty;

    /// <summary>
    /// Gets the error code for the recording.
    /// Corresponds to the "errorcode" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("errorcode")]
    public int ErrorCode { get; init; }

    /// <summary>
    /// Gets the total number of errors for the recording.
    /// Corresponds to the "errors" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("errors")]
    public int Errors { get; init; }

    /// <summary>
    /// Gets the fanart image URL for the recording.
    /// Corresponds to the "fanart_image" field in the JSON response.
    /// Example: "".
    /// </summary>
    [JsonPropertyName("fanart_image")]
    public string FanartImage { get; init; } = string.Empty;

    /// <summary>
    /// Gets the file path where the recording is stored.
    /// Corresponds to the "filename" field in the JSON response.
    /// Example: "/recordings/file.ts".
    /// </summary>
    [JsonPropertyName("filename")]
    public string Filename { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether the recording file was removed.
    /// Corresponds to the "fileremoved" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("fileremoved")]
    public int FileRemoved { get; init; }

    /// <summary>
    /// Gets the size of the recording file in bytes.
    /// Corresponds to the "filesize" field in the JSON response.
    /// Example: 60315100 (approximately 57.5 MB).
    /// </summary>
    [JsonPropertyName("filesize")]
    public long FileSize { get; init; }

    /// <summary>
    /// Gets the first aired date for the recording.
    /// Corresponds to the "first_aired" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("first_aired")]
    public int FirstAired { get; init; }

    /// <summary>
    /// Gets the genres associated with the recording.
    /// Corresponds to the "genre" field in the JSON response.
    /// Example: [].
    /// </summary>
    [JsonPropertyName("genre")]
    public IReadOnlyList<int> Genre { get; init; } = new List<int>();

    /// <summary>
    /// Gets the image URL for the recording.
    /// Corresponds to the "image" field in the JSON response.
    /// Example: "".
    /// </summary>
    [JsonPropertyName("image")]
    public string Image { get; init; } = string.Empty;

    /// <summary>
    /// Gets the keywords associated with the recording.
    /// Corresponds to the "keyword" field in the JSON response.
    /// Example: [].
    /// </summary>
    [JsonPropertyName("keyword")]
    public IReadOnlyList<string> Keyword { get; init; } = new List<string>();

    /// <summary>
    /// Gets a value indicating whether re-recording is not allowed.
    /// Corresponds to the "norerecord" field in the JSON response.
    /// Example: false.
    /// </summary>
    [JsonPropertyName("norerecord")]
    public bool NoReRecord { get; init; }

    /// <summary>
    /// Gets a value indicating whether rescheduling is not allowed for the recording.
    /// Corresponds to the "noresched" field in the JSON response.
    /// Example: true.
    /// </summary>
    [JsonPropertyName("noresched")]
    public bool NoResched { get; init; }

    /// <summary>
    /// Gets the owner of the recording.
    /// Corresponds to the "owner" field in the JSON response.
    /// Example: "jellyfin".
    /// </summary>
    [JsonPropertyName("owner")]
    public string Owner { get; init; } = string.Empty;

    /// <summary>
    /// Gets the parent ID associated with the recording.
    /// Corresponds to the "parent" field in the JSON response.
    /// Example: "".
    /// </summary>
    [JsonPropertyName("parent")]
    public string Parent { get; init; } = string.Empty;

    /// <summary>
    /// Gets the play count for the recording.
    /// Corresponds to the "playcount" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("playcount")]
    public int PlayCount { get; init; }

    /// <summary>
    /// Gets the play position for the recording.
    /// Corresponds to the "playposition" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("playposition")]
    public int PlayPosition { get; init; }

    /// <summary>
    /// Gets the priority of the recording.
    /// Corresponds to the "pri" field in the JSON response.
    /// Example: 2.
    /// </summary>
    [JsonPropertyName("pri")]
    public int Priority { get; init; }

    /// <summary>
    /// Gets the authority responsible for the recording's rating.
    /// Corresponds to the "rating_authority" field in the JSON response.
    /// Example: "".
    /// </summary>
    [JsonPropertyName("rating_authority")]
    public string RatingAuthority { get; init; } = string.Empty;

    /// <summary>
    /// Gets the country associated with the recording's rating.
    /// Corresponds to the "rating_country" field in the JSON response.
    /// Example: "".
    /// </summary>
    [JsonPropertyName("rating_country")]
    public string RatingCountry { get; init; } = string.Empty;

    /// <summary>
    /// Gets the rating icon URL for the recording.
    /// Corresponds to the "rating_icon" field in the JSON response.
    /// Example: "".
    /// </summary>
    [JsonPropertyName("rating_icon")]
    public string RatingIcon { get; init; } = string.Empty;

    /// <summary>
    /// Gets the rating label for the recording.
    /// Corresponds to the "rating_label" field in the JSON response.
    /// Example: "".
    /// </summary>
    [JsonPropertyName("rating_label")]
    public string RatingLabel { get; init; } = string.Empty;

    /// <summary>
    /// Gets the UUID of the rating label for the recording.
    /// Corresponds to the "rating_label_uuid" field in the JSON response.
    /// Example: "".
    /// </summary>
    [JsonPropertyName("rating_label_uuid")]
    public string RatingLabelUuid { get; init; } = string.Empty;

    /// <summary>
    /// Gets the removal time for the recording in days.
    /// Corresponds to the "removal" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("removal")]
    public int Removal { get; init; }

    /// <summary>
    /// Gets the retention time for the recording in days.
    /// Corresponds to the "retention" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("retention")]
    public int Retention { get; init; }

    /// <summary>
    /// Gets the status of the recording schedule.
    /// Corresponds to the "sched_status" field in the JSON response.
    /// Example: "completed".
    /// </summary>
    [JsonPropertyName("sched_status")]
    public string SchedStatus { get; init; } = string.Empty;

    /// <summary>
    /// Gets the start time of the recording as a Unix timestamp.
    /// Corresponds to the "start" field in the JSON response.
    /// Example: 1735603200.
    /// </summary>
    [JsonPropertyName("start")]
    public long Start { get; init; }

    /// <summary>
    /// Gets the extra start time for the recording in seconds.
    /// Corresponds to the "start_extra" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("start_extra")]
    public int StartExtra { get; init; }

    /// <summary>
    /// Gets the real start time of the recording as a Unix timestamp.
    /// Corresponds to the "start_real" field in the JSON response.
    /// Example: 1735603170.
    /// </summary>
    [JsonPropertyName("start_real")]
    public long StartReal { get; init; }

    /// <summary>
    /// Gets the status of the recording.
    /// Corresponds to the "status" field in the JSON response.
    /// Example: "Completed OK".
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>
    /// Gets the stop time of the recording as a Unix timestamp.
    /// Corresponds to the "stop" field in the JSON response.
    /// Example: 1735605900.
    /// </summary>
    [JsonPropertyName("stop")]
    public long Stop { get; init; }

    /// <summary>
    /// Gets the extra stop time for the recording in seconds.
    /// Corresponds to the "stop_extra" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("stop_extra")]
    public int StopExtra { get; init; }

    /// <summary>
    /// Gets the real stop time of the recording as a Unix timestamp.
    /// Corresponds to the "stop_real" field in the JSON response.
    /// Example: 1735605900.
    /// </summary>
    [JsonPropertyName("stop_real")]
    public long StopReal { get; init; }

    /// <summary>
    /// Gets the timerec ID for the recording.
    /// Corresponds to the "timerec" field in the JSON response.
    /// Example: "".
    /// </summary>
    [JsonPropertyName("timerec")]
    public string TimeRec { get; init; } = string.Empty;

    /// <summary>
    /// Gets the timerec caption for the recording.
    /// Corresponds to the "timerec_caption" field in the JSON response.
    /// Example: "".
    /// </summary>
    [JsonPropertyName("timerec_caption")]
    public string TimeRecCaption { get; init; } = string.Empty;

    /// <summary>
    /// Gets the localized title for the recording.
    /// Corresponds to the "title" field in the JSON response.
    /// Example: { "eng": "Medical Detectives - Geheimnisse der Gerichtsmedizin" }.
    /// </summary>
    [JsonPropertyName("title")]
    public IReadOnlyDictionary<string, string> Title { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Gets the recording's playback URL.
    /// Corresponds to the "url" field in the JSON response.
    /// Example: "dvrfile/5577325d4d8bbb8e6f6b03bc98794a77".
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    /// <summary>
    /// Gets the universally unique identifier (UUID) of the recording.
    /// Corresponds to the "uuid" field in the JSON response.
    /// Example: "5577325d4d8bbb8e6f6b03bc98794a77".
    /// </summary>
    [JsonPropertyName("uuid")]
    public string Uuid { get; init; } = string.Empty;

    /// <summary>
    /// Gets the number of times the recording has been watched.
    /// Corresponds to the "watched" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("watched")]
    public int Watched { get; init; }
}
