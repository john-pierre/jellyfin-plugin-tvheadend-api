using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Auth;

/// <summary>
/// Represents TVHeadend /api/idnode/load response for user nodes.
/// </summary>
internal sealed class IdNodeUserLoadResponse
{
    /// <summary>
    /// Gets returned user node entries.
    /// </summary>
    [JsonPropertyName("entries")]
    public IdNodeUserEntry[] Entries { get; init; } = Array.Empty<IdNodeUserEntry>();
}
