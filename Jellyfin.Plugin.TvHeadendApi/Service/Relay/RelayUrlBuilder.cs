// Builds Jellyfin-local relay URLs that point clients to the plugin's relay endpoints instead of TVHeadend.

using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
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

    /// <summary>
    /// Builds a tokenized relay URL for a live TV stream.
    /// Issues a scoped short-lived token and appends it to the URL.
    /// </summary>
    /// <param name="channelId">TVHeadend channel UUID.</param>
    /// <param name="profile">Optional streaming profile name.</param>
    /// <param name="userId">Optional Jellyfin user ID.</param>
    /// <param name="deviceId">Optional device/client identifier.</param>
    /// <param name="playbackMode">Optional playback mode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Relay URL with embedded token.</returns>
    Task<string> BuildTokenizedStreamRelayUrlAsync(
        string channelId,
        string? profile,
        string? userId,
        string? deviceId,
        string? playbackMode,
        CancellationToken cancellationToken);

    /// <summary>
    /// Builds a tokenized relay URL for an image resource.
    /// Issues a scoped token and appends it to the URL.
    /// </summary>
    /// <param name="imageId">Image resource identifier.</param>
    /// <param name="mediaKind">Optional media kind.</param>
    /// <param name="userId">Optional Jellyfin user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Relay URL with embedded token.</returns>
    Task<string> BuildTokenizedImageRelayUrlAsync(
        string imageId,
        MediaKind? mediaKind,
        string? userId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Default implementation that uses <see cref="IServerApplicationHost"/> to discover
/// the Jellyfin server's own URL (scheme + host + port) at runtime,
/// with an optional manual override from plugin configuration.
/// </summary>
internal sealed class RelayUrlBuilder : IRelayUrlBuilder
{
    private readonly IServerApplicationHost _appHost;
    private readonly ConfigurationProvider _configProvider;
    private readonly IRelayTokenService? _tokenService;
    private readonly RelayTokenOptions? _tokenOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayUrlBuilder"/> class.
    /// </summary>
    /// <param name="appHost">Jellyfin application host for URL resolution.</param>
    /// <param name="configProvider">Plugin configuration provider.</param>
    /// <param name="tokenService">Relay token service for issuing tokens (optional for backward compatibility).</param>
    /// <param name="tokenOptions">Relay token policy options (optional for backward compatibility).</param>
    public RelayUrlBuilder(
        IServerApplicationHost appHost,
        ConfigurationProvider configProvider,
        IRelayTokenService? tokenService = null,
        RelayTokenOptions? tokenOptions = null)
    {
        _appHost = appHost ?? throw new ArgumentNullException(nameof(appHost));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _tokenService = tokenService;
        _tokenOptions = tokenOptions;
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

    /// <inheritdoc />
    public async Task<string> BuildTokenizedStreamRelayUrlAsync(
        string channelId,
        string? profile,
        string? userId,
        string? deviceId,
        string? playbackMode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        if (_tokenService == null || _tokenOptions == null || !_tokenOptions.Enabled)
        {
            return BuildStreamRelayUrl(channelId, profile);
        }

        var rawToken = await _tokenService.IssueStreamTokenAsync(channelId, userId, deviceId, profile, playbackMode, cancellationToken).ConfigureAwait(false);
        var baseUrl = GetEffectiveBaseUrl();
        var url = $"{baseUrl}/api/tvheadend/relay/stream/{Uri.EscapeDataString(channelId)}";
        var separator = '?';

        if (!string.IsNullOrWhiteSpace(profile))
        {
            url += $"?profile={Uri.EscapeDataString(profile)}";
            separator = '&';
        }

        url += $"{separator}token={Uri.EscapeDataString(rawToken)}";
        return url;
    }

    /// <inheritdoc />
    public async Task<string> BuildTokenizedImageRelayUrlAsync(
        string imageId,
        MediaKind? mediaKind,
        string? userId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageId);

        if (_tokenService == null || _tokenOptions == null || !_tokenOptions.Enabled)
        {
            return BuildImageRelayUrl(imageId);
        }

        var rawToken = await _tokenService.IssueImageTokenAsync(imageId, mediaKind, userId, cancellationToken).ConfigureAwait(false);
        var baseUrl = GetEffectiveBaseUrl();
        var normalizedPath = imageId.TrimStart('/');
        return $"{baseUrl}/api/tvheadend/relay/images/{Uri.EscapeDataString(normalizedPath)}?token={Uri.EscapeDataString(rawToken)}";
    }

    /// <summary>
    /// Returns the auto-detected Jellyfin base URL via <see cref="IServerApplicationHost"/>.
    /// </summary>
    private string GetAutoDetectedBaseUrl()
    {
        return _appHost.GetApiUrlForLocalAccess().TrimEnd('/');
    }
}
