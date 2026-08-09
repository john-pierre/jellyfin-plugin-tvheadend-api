using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;

/// <summary>
/// Default <see cref="IPlaybackContextAccessor"/> that reads the requesting client identity
/// (client app name, device name, user id) from the current HTTP request via Jellyfin's
/// <see cref="IAuthorizationContext"/>. This is what makes client/device/user streaming-profile
/// rules actually fire — without it the resolver only ever sees the channel id.
/// </summary>
internal sealed class PlaybackContextAccessor : IPlaybackContextAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IAuthorizationContext _authorizationContext;
    private readonly ILogger<PlaybackContextAccessor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackContextAccessor"/> class.
    /// </summary>
    /// <param name="httpContextAccessor">Accessor for the current HTTP request.</param>
    /// <param name="authorizationContext">Jellyfin authorization context for client/user resolution.</param>
    /// <param name="logger">Logger instance.</param>
    public PlaybackContextAccessor(
        IHttpContextAccessor httpContextAccessor,
        IAuthorizationContext authorizationContext,
        ILogger<PlaybackContextAccessor> logger)
    {
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _authorizationContext = authorizationContext ?? throw new ArgumentNullException(nameof(authorizationContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<StreamingProfileContext> CreateContextAsync(string? channelId, CancellationToken cancellationToken)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            // No request context (e.g. background warmup) — rules that need client identity simply will not match.
            return new StreamingProfileContext { ChannelId = channelId };
        }

        try
        {
            var auth = await _authorizationContext.GetAuthorizationInfo(httpContext).ConfigureAwait(false);
            return new StreamingProfileContext
            {
                ChannelId = channelId,
                ClientName = string.IsNullOrWhiteSpace(auth.Client) ? null : auth.Client,
                DeviceName = string.IsNullOrWhiteSpace(auth.Device) ? null : auth.Device,
                UserId = auth.UserId == Guid.Empty ? null : auth.UserId.ToString("D", CultureInfo.InvariantCulture),
            };
        }
        catch (Exception ex)
        {
            // Never let identity resolution break playback — fall back to a channel-only context.
            _logger.LogDebug(ex, "Could not resolve playback client identity; using channel-only profile context.");
            return new StreamingProfileContext { ChannelId = channelId };
        }
    }
}
