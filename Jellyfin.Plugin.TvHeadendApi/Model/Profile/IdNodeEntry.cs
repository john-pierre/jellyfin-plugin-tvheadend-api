using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Profile;

/// <summary>
/// Represents one TVHeadend idnode entry with common fields used by this plugin.
/// </summary>
internal sealed class IdNodeEntry
{
    [JsonPropertyName("name")]
    public JsonElement Name { get; init; }

    [JsonPropertyName("class")]
    public JsonElement ProfileClass { get; init; }

    [JsonPropertyName("container")]
    public JsonElement Container { get; init; }

    [JsonPropertyName("pro_vcodec")]
    public JsonElement ProVideoCodec { get; init; }

    [JsonPropertyName("vcodec")]
    public JsonElement VideoCodec { get; init; }

    [JsonPropertyName("pro_acodec")]
    public JsonElement ProAudioCodec { get; init; }

    [JsonPropertyName("acodec")]
    public JsonElement AudioCodec { get; init; }

    [JsonPropertyName("src_vcodec")]
    public JsonElement SourceVideoCodecs { get; init; }

    [JsonPropertyName("src_acodec")]
    public JsonElement SourceAudioCodecs { get; init; }

    [JsonPropertyName("deinterlace")]
    public JsonElement Deinterlace { get; init; }

    [JsonPropertyName("codec")]
    public JsonElement Codec { get; init; }

    [JsonPropertyName("enabled")]
    public JsonElement Enabled { get; init; }

    [JsonPropertyName("default")]
    public JsonElement IsDefault { get; init; }

    [JsonPropertyName("comment")]
    public JsonElement Comment { get; init; }

    [JsonPropertyName("timeout")]
    public JsonElement Timeout { get; init; }

    [JsonPropertyName("timeout_start")]
    public JsonElement TimeoutStart { get; init; }

    [JsonPropertyName("priority")]
    public JsonElement Priority { get; init; }

    [JsonPropertyName("fpriority")]
    public JsonElement FPriority { get; init; }

    [JsonPropertyName("restart")]
    public JsonElement Restart { get; init; }

    [JsonPropertyName("contaccess")]
    public JsonElement ContinuousAccess { get; init; }

    [JsonPropertyName("catimeout")]
    public JsonElement CaTimeout { get; init; }

    [JsonPropertyName("swservice")]
    public JsonElement SoftwareService { get; init; }

    [JsonPropertyName("svfilter")]
    public JsonElement ServiceVideoFilter { get; init; }

    [JsonPropertyName("pro_scodec")]
    public JsonElement ProSubtitleCodec { get; init; }

    [JsonPropertyName("src_scodec")]
    public JsonElement SourceSubtitleCodecs { get; init; }

    [JsonPropertyName("params")]
    public IReadOnlyList<IdNodeParam> Params { get; init; } = Array.Empty<IdNodeParam>();
}
