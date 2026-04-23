// Builds Jellyfin-local relay URLs that point clients to the plugin's relay endpoints instead of TVHeadend.

using System;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
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

    /// <summary>
    /// Returns the Jellyfin base URL currently used for relay (auto-detected or override).
    /// </summary>
    /// <returns>Base URL string.</returns>
    string GetEffectiveBaseUrl();
}

/// <summary>
/// Default implementation that uses <see cref="IServerApplicationHost"/> to discover
/// the Jellyfin server's own URL (scheme + host + port) at runtime,
/// with an optional manual override from plugin configuration.
/// </summary>
internal sealed class RelayUrlBuilder : IRelayUrlBuilder
{
    private readonly IServerApplicationHost _appHost;
    private readonly PluginConfigurationProvider _configProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayUrlBuilder"/> class.
    /// </summary>
    /// <param name="appHost">Jellyfin application host for URL resolution.</param>
    /// <param name="configProvider">Plugin configuration provider.</param>
    public RelayUrlBuilder(IServerApplicationHost appHost, PluginConfigurationProvider configProvider)
    {
        _appHost = appHost ?? throw new ArgumentNullException(nameof(appHost));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    /// <inheritdoc />
    public string BuildImageRelayUrl(string tvhImagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tvhImagePath);
        var baseUrl = GetEffectiveBaseUrl();
        var normalizedPath = tvhImagePath.TrimStart('/');
        return $"{baseUrl}/api/tvheadend/images/{Uri.EscapeDataString(normalizedPath)}";
    }

    /// <inheritdoc />
    public string BuildStreamRelayUrl(string channelId, string? profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        var baseUrl = GetEffectiveBaseUrl();
        var url = $"{baseUrl}/api/tvheadend/stream/{Uri.EscapeDataString(channelId)}";
        if (!string.IsNullOrWhiteSpace(profile))
        {
            url += $"?profile={Uri.EscapeDataString(profile)}";
        }

        return url;
    }

    /// <inheritdoc />
    public string GetEffectiveBaseUrl()
    {
        var overrideUrl = _configProvider.Configuration?.RelayHostOverride;
        if (!string.IsNullOrWhiteSpace(overrideUrl))
        {
            return overrideUrl.TrimEnd('/');
        }

        return GetAutoDetectedBaseUrl();
    }

    /// <summary>
    /// Returns the auto-detected Jellyfin base URL via <see cref="IServerApplicationHost"/>.
    /// </summary>
    private string GetAutoDetectedBaseUrl()
    {
        return _appHost.GetApiUrlForLocalAccess().TrimEnd('/');
    }
}
