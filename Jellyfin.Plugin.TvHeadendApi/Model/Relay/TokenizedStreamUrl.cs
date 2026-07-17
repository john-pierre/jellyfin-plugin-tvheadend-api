// Result of building a tokenized stream relay URL — carries the raw token for telemetry attach.

namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// Result of building a (possibly tokenized) stream relay URL.
/// </summary>
/// <param name="Url">The client-facing stream URL.</param>
/// <param name="RawToken">
/// The raw relay token embedded in the URL, or <c>null</c> when token security is disabled.
/// Callers use it to attach stream-setup telemetry to the issued token; it must never be logged.
/// </param>
public sealed record TokenizedStreamUrl(string Url, string? RawToken);
