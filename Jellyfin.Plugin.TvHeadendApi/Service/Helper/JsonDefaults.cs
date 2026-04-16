using System.Text.Json;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Helper;

/// <summary>
/// Shared JSON serializer options used across all TVHeadend API services.
/// </summary>
internal static class JsonDefaults
{
    /// <summary>
    /// Gets the default options for deserializing TVHeadend API responses (case-insensitive property names).
    /// </summary>
    public static JsonSerializerOptions Api { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
