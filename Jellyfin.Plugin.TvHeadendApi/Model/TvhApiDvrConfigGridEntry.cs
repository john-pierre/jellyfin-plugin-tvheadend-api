using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model;

/// <summary>
/// Represents a recording configuration profile from TVHeadEnd.
/// Maps all fields from the `/api/dvr/config/grid` response.
/// </summary>
public class TvhApiDvrConfigGridEntry
{
    /// <summary>
    /// Gets the unique identifier (UUID) of the profile.
    /// Corresponds to the "uuid" field in the JSON response.
    /// Example: "7a5edfbe189851e5b1d1df19c93962f0".
    /// </summary>
    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    /// <summary>
    /// Gets a value indicating whether the profile is enabled.
    /// Corresponds to the "enabled" field in the JSON response.
    /// Example: true.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets the name of the profile.
    /// Corresponds to the "name" field in the JSON response.
    /// Example: "" (empty string).
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Gets the profile ID associated with the recording profile.
    /// Corresponds to the "profile" field in the JSON response.
    /// Example: "3ed2d7a0864e94746184569dbb45cead".
    /// </summary>
    [JsonPropertyName("profile")]
    public string? ProfileId { get; init; }

    /// <summary>
    /// Gets the priority of the profile.
    /// Corresponds to the "pri" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("pri")]
    public int Priority { get; init; }

    /// <summary>
    /// Gets the number of days to retain recordings.
    /// Corresponds to the "retention-days" field in the JSON response.
    /// Example: 2147483646.
    /// </summary>
    [JsonPropertyName("retention-days")]
    public int RetentionDays { get; init; }

    /// <summary>
    /// Gets the number of days before recordings are automatically removed.
    /// Corresponds to the "removal-days" field in the JSON response.
    /// Example: 2147483647.
    /// </summary>
    [JsonPropertyName("removal-days")]
    public int RemovalDays { get; init; }

    /// <summary>
    /// Gets a value indicating whether to remove recordings after playback.
    /// Corresponds to the "remove-after-playback" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("remove-after-playback")]
    public int RemoveAfterPlayback { get; init; }

    /// <summary>
    /// Gets the pre-padding time in minutes.
    /// Corresponds to the "pre-extra-time" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("pre-extra-time")]
    public int PreExtraTime { get; init; }

    /// <summary>
    /// Gets the post-padding time in minutes.
    /// Corresponds to the "post-extra-time" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("post-extra-time")]
    public int PostExtraTime { get; init; }

    /// <summary>
    /// Gets a value indicating whether cloning is enabled.
    /// Corresponds to the "clone" field in the JSON response.
    /// Example: true.
    /// </summary>
    [JsonPropertyName("clone")]
    public bool Clone { get; init; }

    /// <summary>
    /// Gets the number of times recordings with errors should be re-recorded.
    /// Corresponds to the "rerecord-errors" field in the JSON response.
    /// Example: 10.
    /// </summary>
    [JsonPropertyName("rerecord-errors")]
    public int ReRecordErrors { get; init; }

    /// <summary>
    /// Gets a value indicating whether complex scheduling is enabled.
    /// Corresponds to the "complex-scheduling" field in the JSON response.
    /// Example: false.
    /// </summary>
    [JsonPropertyName("complex-scheduling")]
    public bool ComplexScheduling { get; init; }

    /// <summary>
    /// Gets a value indicating whether to fetch artwork for recordings.
    /// Corresponds to the "fetch-artwork" field in the JSON response.
    /// Example: true.
    /// </summary>
    [JsonPropertyName("fetch-artwork")]
    public bool FetchArtwork { get; init; }

    /// <summary>
    /// Gets the storage path for recordings.
    /// Corresponds to the "storage" field in the JSON response.
    /// Example: "/recordings/".
    /// </summary>
    [JsonPropertyName("storage")]
    public string? Storage { get; init; }

    /// <summary>
    /// Gets the minimum free space in the storage in megabytes.
    /// Corresponds to the "storage-mfree" field in the JSON response.
    /// Example: 1000.
    /// </summary>
    [JsonPropertyName("storage-mfree")]
    public int StorageMinFree { get; init; }

    /// <summary>
    /// Gets the used space in the storage in megabytes.
    /// Corresponds to the "storage-mused" field in the JSON response.
    /// Example: 0.
    /// </summary>
    [JsonPropertyName("storage-mused")]
    public int StorageUsed { get; init; }

    /// <summary>
    /// Gets the file path format for recordings.
    /// Corresponds to the "pathname" field in the JSON response.
    /// Example: "$t/$t -$e -$s$-e$n.$x".
    /// </summary>
    [JsonPropertyName("pathname")]
    public string? Pathname { get; init; }

    /// <summary>
    /// Gets the number of recordings to cache.
    /// Corresponds to the "cache" field in the JSON response.
    /// Example: 2.
    /// </summary>
    [JsonPropertyName("cache")]
    public int Cache { get; init; }
}
