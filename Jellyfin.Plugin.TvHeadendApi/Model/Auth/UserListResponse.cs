using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Auth;

/// <summary>
/// Represents TVHeadend /api/user/list response.
/// </summary>
internal sealed class UserListResponse
{
    /// <summary>
    /// Gets returned user entries.
    /// </summary>
    [JsonPropertyName("entries")]
    public UserListEntry[] Entries { get; init; } = Array.Empty<UserListEntry>();
}
