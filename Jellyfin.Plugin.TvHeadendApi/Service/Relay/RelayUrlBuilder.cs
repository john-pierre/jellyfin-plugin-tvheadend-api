// Builds Jellyfin-local relay URLs that point clients to the plugin's relay endpoints instead of TVHeadend.

using System;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using MediaBrowser.Controller;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// Builds Jellyfin-local relay URLs for images and streams.
/// Clients receive these URLs instead of direct TVHeadend URLs,
/// ensuring credentials stay server-side and internal URLs are hidden.
/// </summary>
public interface IRelayUrlBuilder
{
    /// <summary>
    /// Builds a relay URL for an image (channel logo, EPG image, thumbnail).
    /// </summary>
    /// <param name="tvhImagePath">Relative TVHeadend image path (e.g. <c>imagecache/123</c>).</param>
    /// <returns>Jellyfin relay URL like <c>http://jellyfin:8096/api/tvheadend/images/imagecache/123</c>.</returns>
    string BuildImageRelayUrl(string tvhImagePath);

    /// <summary>
    /// Builds a relay URL for a live TV stream.
    /// </summary>
    /// <param name="channelId">TVHeadend channel UUID.</param>
    /// <param name="profile">Optional streaming profile name.</param>
    /// <returns>Jellyfin relay URL like <c>http://jellyfin:8096/api/tvheadend/stream/{channelId}?profile=pass</c>.</returns>
    string BuildStreamRelayUrl(string channelId, string? profile);
}

/// <summary>
/// Default implementation that uses <see cref="IServerApplicationHost"/> to discover
/// the Jellyfin server's own URL (scheme + host + port) at runtime.
/// </summary>
internal sealed class RelayUrlBuilder : IRelayUrlBuilder
{
    private readonly IServerApplicationHost _appHost;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayUrlBuilder"/> class.
    /// </summary>
    /// <param name="appHost">Jellyfin application host for URL resolution.</param>
    public RelayUrlBuilder(IServerApplicationHost appHost)
    {
        _appHost = appHost ?? throw new ArgumentNullException(nameof(appHost));
    }

    /// <inheritdoc />
    public string BuildImageRelayUrl(string tvhImagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tvhImagePath);
        var baseUrl = GetJellyfinBaseUrl();
        var normalizedPath = tvhImagePath.TrimStart('/');
        return $"{baseUrl}/api/tvheadend/images/{Uri.EscapeDataString(normalizedPath)}";
    }

    /// <inheritdoc />
    public string BuildStreamRelayUrl(string channelId, string? profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        var baseUrl = GetJellyfinBaseUrl();
        var url = $"{baseUrl}/api/tvheadend/stream/{Uri.EscapeDataString(channelId)}";
        if (!string.IsNullOrWhiteSpace(profile))
        {
            url += $"?profile={Uri.EscapeDataString(profile)}";
        }

        return url;
    }

    /// <summary>
    /// Returns the Jellyfin base URL (e.g. <c>http://192.168.1.10:8096</c>).
    /// Uses <see cref="IServerApplicationHost.GetApiUrlForLocalAccess"/> which automatically
    /// resolves the correct scheme (HTTP/HTTPS) and port.
    /// </summary>
    private string GetJellyfinBaseUrl()
    {
        return _appHost.GetApiUrlForLocalAccess().TrimEnd('/');
    }
}
