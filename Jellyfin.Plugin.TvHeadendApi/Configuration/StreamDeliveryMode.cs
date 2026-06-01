// Defines how the live-stream URL handed to clients is constructed.

namespace Jellyfin.Plugin.TvHeadendApi.Configuration;

/// <summary>
/// Controls how the live-stream URL returned to Jellyfin clients is built.
/// </summary>
public enum StreamDeliveryMode
{
    /// <summary>
    /// Stream is proxied through the plugin's Jellyfin relay endpoint.
    /// TVHeadend credentials and internal URLs are never exposed to clients.
    /// The Jellyfin host is derived from the client's own request (or the optional override).
    /// This is the default and the most compatible/secure option.
    /// </summary>
    Relay = 0,

    /// <summary>
    /// Clients connect directly to TVHeadend using an auth token appended as <c>?auth=</c>.
    /// Fastest path (no extra Jellyfin hop) and requires no reachable Jellyfin host,
    /// but exposes the TVHeadend host and auth token to clients. Requires a configured
    /// <see cref="PluginConfiguration.AuthToken"/> (or anonymous access); otherwise the plugin
    /// transparently falls back to <see cref="Relay"/>.
    /// </summary>
    DirectToTvheadend = 1,
}
