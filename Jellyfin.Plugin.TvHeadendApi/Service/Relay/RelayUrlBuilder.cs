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
