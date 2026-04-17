using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Status;

/// <summary>
/// Represents a single active connection from the TVHeadend <c>/api/status/connections</c> endpoint.
/// </summary>
public sealed class ConnectionEntry
{
    /// <summary>Gets the connection identifier.</summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>Gets the server-side IP address.</summary>
    [JsonPropertyName("server")]
    public string Server { get; init; } = string.Empty;

    /// <summary>Gets the server-side port number.</summary>
    [JsonPropertyName("server_port")]
    public int ServerPort { get; init; }

    /// <summary>Gets the remote peer IP address.</summary>
    [JsonPropertyName("peer")]
    public string Peer { get; init; } = string.Empty;

    /// <summary>Gets the remote peer port number.</summary>
    [JsonPropertyName("peer_port")]
    public int PeerPort { get; init; }

    /// <summary>Gets the connection start time as a Unix epoch timestamp.</summary>
    [JsonPropertyName("started")]
    public long Started { get; init; }

    /// <summary>Gets a value indicating whether the connection is actively streaming.</summary>
    [JsonPropertyName("streaming")]
    public int Streaming { get; init; }

    /// <summary>Gets the connection type (e.g. HTTP, HTSP).</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>Gets the connected user name.</summary>
    [JsonPropertyName("user")]
    public string User { get; init; } = string.Empty;
}
