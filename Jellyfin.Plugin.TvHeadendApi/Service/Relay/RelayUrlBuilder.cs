// Builds Jellyfin-local relay URLs that point clients to the plugin's relay endpoints instead of TVHeadend.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;

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
    private readonly IHttpContextAccessor? _httpContextAccessor;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayUrlBuilder"/> class.
    /// </summary>
    /// <param name="appHost">Jellyfin application host for URL resolution.</param>
    /// <param name="configProvider">Plugin configuration provider.</param>
    /// <param name="tokenService">Relay token service for issuing tokens (optional for backward compatibility).</param>
    /// <param name="tokenOptions">Relay token policy options (optional for backward compatibility).</param>
    /// <param name="httpContextAccessor">
    /// Accessor for the current HTTP request, used to derive a client-reachable Jellyfin host
    /// (handles reverse proxies / published URLs). Optional for backward compatibility.
    /// </param>
    public RelayUrlBuilder(
        IServerApplicationHost appHost,
        ConfigurationProvider configProvider,
        IRelayTokenService? tokenService = null,
        RelayTokenOptions? tokenOptions = null,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _appHost = appHost ?? throw new ArgumentNullException(nameof(appHost));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _tokenService = tokenService;
        _tokenOptions = tokenOptions;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc />
    public string BuildImageRelayUrl(string tvhImagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tvhImagePath);
        var baseUrl = GetEffectiveBaseUrl();
        var normalizedPath = tvhImagePath.TrimStart('/');
        return $"{baseUrl}/api/tvheadend/images/{EscapeRelativePath(normalizedPath)}";
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
        // 1. Explicit operator override always wins (reverse-proxy / custom domains).
        var overrideUrl = _configProvider.Configuration?.RelayHostOverride;
        if (!string.IsNullOrWhiteSpace(overrideUrl))
        {
            return overrideUrl.TrimEnd('/');
        }

        // 2. Derive a client-reachable URL from the current request when one is available.
        //    GetSmartApiUrl honours the published server URL, reverse-proxy headers and the
        //    network the request came in on — so manual host configuration is rarely needed.
        var request = _httpContextAccessor?.HttpContext?.Request;
        if (request != null)
        {
            try
            {
                var smartUrl = _appHost.GetSmartApiUrl(request);
                if (!string.IsNullOrWhiteSpace(smartUrl))
                {
                    return smartUrl.TrimEnd('/');
                }
            }
            catch (Exception)
            {
                // Fall through to local-access auto-detection.
            }
        }

        // 3. Fallback: the server's local-access URL.
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
        return $"{baseUrl}/api/tvheadend/relay/images/{EscapeRelativePath(normalizedPath)}?token={Uri.EscapeDataString(rawToken)}";
    }

    /// <summary>
    /// Percent-escapes each segment of a relative path while preserving the "/" separators, so the URL
    /// stays matchable by the ASP.NET <c>{**path}</c> catch-all route. Escaping the whole path with
    /// <see cref="Uri.EscapeDataString"/> would encode "/" as "%2F", which Kestrel keeps literal and the
    /// catch-all then fails to match (HTTP 404) — TVHeadend image paths like "imagecache/1684" must keep
    /// real slashes. The relay forwards the decoded path to TVHeadend, which expects "/imagecache/1684".
    /// </summary>
    /// <param name="path">The relative path (already trimmed of any leading slash).</param>
    /// <returns>The path with each segment escaped and slashes preserved.</returns>
    private static string EscapeRelativePath(string path)
        => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    /// <summary>
    /// Returns the auto-detected Jellyfin base URL via <see cref="IServerApplicationHost"/>.
    /// </summary>
    private string GetAutoDetectedBaseUrl()
    {
        return _appHost.GetApiUrlForLocalAccess().TrimEnd('/');
    }
}
