using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Profile;

/// <summary>
/// Represents a TVHeadend /api/idnode/load response.
/// </summary>
internal sealed class IdNodeLoadResponse
{
    [JsonPropertyName("entries")]
    public IdNodeEntry[] Entries { get; init; } = Array.Empty<IdNodeEntry>();
}
