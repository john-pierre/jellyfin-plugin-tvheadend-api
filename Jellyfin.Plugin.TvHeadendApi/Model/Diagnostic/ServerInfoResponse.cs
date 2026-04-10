using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Diagnostic;

/// <summary>
/// Represents the TVHeadend /api/serverinfo response.
/// </summary>
internal sealed class ServerInfoResponse
{
    [JsonPropertyName("sw_version")]
    public string SwVersion { get; init; } = string.Empty;

    [JsonPropertyName("api_version")]
    public int? ApiVersion { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}
