using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Input;

/// <summary>
/// Grid response wrapper for <c>/api/status/inputs</c>.
/// </summary>
public sealed class InputGridResponse
{
    /// <summary>
    /// Gets the list of input status entries.
    /// </summary>
    [JsonPropertyName("entries")]
    public IReadOnlyList<InputStatusEntry> Entries { get; init; } = [];

    /// <summary>
    /// Gets the total number of input entries.
    /// </summary>
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}
