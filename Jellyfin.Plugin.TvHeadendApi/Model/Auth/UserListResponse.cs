using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Auth;

/// <summary>
/// Represents TVHeadend /api/passwd/entry/grid response.
/// Each entry contains the UUID and username of a password/user entry.
/// </summary>
internal sealed class UserListResponse
{
    /// <summary>
    /// Gets returned user entries.
    /// </summary>
    [JsonPropertyName("entries")]
    public UserListEntry[] Entries { get; init; } = Array.Empty<UserListEntry>();
}
