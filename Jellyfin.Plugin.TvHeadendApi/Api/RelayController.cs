// Relay API controller — exposes TVHeadend images and streams through authenticated Jellyfin endpoints.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Metrics;
using Jellyfin.Plugin.TvHeadendApi.Service.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ActiveStreamSession = global::Jellyfin.Plugin.TvHeadendApi.Model.Metrics.ActiveStreamSession;
using RelayFailureReason = global::Jellyfin.Plugin.TvHeadendApi.Model.Relay.RelayFailureReason;
using RelayTimingContext = global::Jellyfin.Plugin.TvHeadendApi.Model.Relay.RelayTimingContext;
using RelayTokenRecord = global::Jellyfin.Plugin.TvHeadendApi.Model.Relay.RelayTokenRecord;
using RelayTokenValidationResult = global::Jellyfin.Plugin.TvHeadendApi.Model.Relay.RelayTokenValidationResult;
using RelayType = global::Jellyfin.Plugin.TvHeadendApi.Model.Relay.RelayType;
using StreamEndedBy = global::Jellyfin.Plugin.TvHeadendApi.Model.Relay.StreamEndedBy;

#pragma warning disable SA1117 // Parameters should be on same line or each on own line

namespace Jellyfin.Plugin.TvHeadendApi.Api;

/// <summary>
/// Relays TVHeadend image and stream requests through authenticated Jellyfin endpoints.
/// Clients never see TVHeadend credentials or internal URLs.
/// </summary>
[ApiController]
[Route("api/tvheadend")]
[Authorize(Policy = Policies.LiveTvAccess)]
public class RelayController : ControllerBase
{
    private const int StreamCopyBufferSize = 81920; // 80 KB — optimal for network I/O
    private readonly IRelayService _relay;
    private readonly IRelayMetricsService _metrics;
    private readonly RelayActivityTracker _activityTracker;
    private readonly SessionTracker _sessionTracker;
    private readonly IRelayUrlBuilder _relayUrlBuilder;
    private readonly IRelayTokenValidator _tokenValidator;
    private readonly RelayTokenOptions _tokenOptions;
    private readonly IHealthService _healthService;
    private readonly ConfigurationProvider _configProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayController"/> class.
    /// </summary>
    /// <param name="relay">The relay service for proxying TVHeadend requests.</param>
    /// <param name="metrics">The relay metrics persistence service.</param>
    /// <param name="activityTracker">Live stream activity tracker.</param>
    /// <param name="sessionTracker">Real-time streaming session tracker.</param>
    /// <param name="relayUrlBuilder">Relay URL builder for host resolution.</param>
    /// <param name="tokenValidator">Relay token validator for public endpoints.</param>
    /// <param name="tokenOptions">Relay token policy options.</param>
    /// <param name="healthService">TVHeadend health tracking service.</param>
    /// <param name="configProvider">Plugin configuration provider.</param>
    public RelayController(
        IRelayService relay,
        IRelayMetricsService metrics,
        RelayActivityTracker activityTracker,
        SessionTracker sessionTracker,
        IRelayUrlBuilder relayUrlBuilder,
        IRelayTokenValidator tokenValidator,
        RelayTokenOptions tokenOptions,
        IHealthService healthService,
        ConfigurationProvider configProvider)
    {
        _relay = relay ?? throw new ArgumentNullException(nameof(relay));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _activityTracker = activityTracker ?? throw new ArgumentNullException(nameof(activityTracker));
        _sessionTracker = sessionTracker ?? throw new ArgumentNullException(nameof(sessionTracker));
        _relayUrlBuilder = relayUrlBuilder ?? throw new ArgumentNullException(nameof(relayUrlBuilder));
        _tokenValidator = tokenValidator ?? throw new ArgumentNullException(nameof(tokenValidator));
        _tokenOptions = tokenOptions ?? throw new ArgumentNullException(nameof(tokenOptions));
        _healthService = healthService ?? throw new ArgumentNullException(nameof(healthService));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    /// <summary>
    /// Returns the relay service status including a real upstream connectivity check.
    /// Verifies that the relay host resolves and that TVHeadend is reachable.
    /// </summary>
    /// <returns>Relay status with health information.</returns>
    [HttpGet("status")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetRelayStatus()
    {
        var config = _configProvider.Configuration;
        var relayEnabled = config?.RelayEnabled ?? false;
        var effectiveHost = _relayUrlBuilder.GetEffectiveBaseUrl();
        var healthSnapshot = _healthService.GetSnapshot();

        var tvhConfigured = config != null
            && !string.IsNullOrWhiteSpace(config.Host)
            && config.Port > 0;

        var tvhHealthy = healthSnapshot.Status == HealthStatus.Healthy;
        var tvhDegraded = healthSnapshot.Status == HealthStatus.Degraded
            || healthSnapshot.Status == HealthStatus.Recovering;
        var tvhUnreachable = healthSnapshot.Status == HealthStatus.Unreachable
            || healthSnapshot.Status == HealthStatus.CircuitOpen
            || healthSnapshot.Status == HealthStatus.Timeout
            || healthSnapshot.Status == HealthStatus.AuthFailed;

        // The circuit breaker can trip on a brief burst of failures even though the backend is fine.
        // If we succeeded very recently, present that as "recovering" rather than a hard red
        // "not reachable" (which contradicts the live diagnostics panel). Auth failures are a real
        // configuration problem, not transient, so they always surface as an error.
        var recentlySucceeded = healthSnapshot.LastSuccessUtc.HasValue
            && DateTimeOffset.UtcNow - healthSnapshot.LastSuccessUtc.Value < TimeSpan.FromMinutes(2)
            && healthSnapshot.Status != HealthStatus.AuthFailed;

        string status;
        string? message = null;
        if (!relayEnabled)
        {
            status = "disabled";
            message = "Relay service is disabled in plugin configuration.";
        }
        else if (!tvhConfigured)
        {
            status = "error";
            message = "TVHeadend connection is not configured (host/port missing).";
        }
        else if (tvhHealthy)
        {
            status = "ok";
        }
        else if (tvhDegraded)
        {
            status = "degraded";
            message = $"TVHeadend is responding but with issues: {healthSnapshot.Status}";
        }
        else if (tvhUnreachable && recentlySucceeded)
        {
            status = "degraded";
            message = $"TVHeadend connection is recovering ({healthSnapshot.Status}); a request succeeded in the last 2 minutes.";
        }
        else if (tvhUnreachable)
        {
            status = "error";
            message = $"TVHeadend is not reachable: {healthSnapshot.Status}"
                + (healthSnapshot.LastFailureReason != FailureReason.None
                    ? $" ({healthSnapshot.LastFailureReason})"
                    : string.Empty);
        }
        else
        {
            // Unknown — no TVHeadend request has been observed yet (e.g. right after startup).
            // Keep the wording neutral: this is a "not checked yet" state, not a problem.
            status = "unknown";
            message = "Checking TVHeadend connectivity — no requests have been recorded yet.";
        }

        return Ok(new
        {
            Status = status,
            Enabled = relayEnabled,
            ActiveStreams = _activityTracker.ActiveStreams,
            EffectiveHost = effectiveHost,
            TvHeadendHealth = healthSnapshot.Status.ToString(),
            LastSuccessUtc = healthSnapshot.LastSuccessUtc,
            LastResponseTimeMs = healthSnapshot.LastResponseTimeMs,
            ConsecutiveFailures = healthSnapshot.ConsecutiveFailures,
            Message = message,
        });
    }

    /// <summary>
    /// Relays an image request (channel logo, EPG image, thumbnail) from TVHeadend.
    /// </summary>
    /// <param name="path">Relative TVHeadend image path (e.g. <c>imagecache/123</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The proxied image with original headers.</returns>
    [HttpGet("images/{**path}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> GetImage(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("Image path is required.");
        }

        var config = _configProvider.Configuration;
        if (config == null || !config.RelayEnabled)
        {
            // Relay is disabled in plugin configuration — refuse to proxy images too,
            // mirroring the stream endpoints so the setting acts as a full kill-switch.
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        RelayResult? result = null;
        try
        {
            result = await _relay.RelayImageAsync(path, cancellationToken).ConfigureAwait(false);
            if (result.TimingContext != null)
            {
                // Record the real request method instead of a hard-coded "GET".
                result.TimingContext.RequestMethod = Request.Method;
            }

            if (result.Body == null)
            {
                RecordAndDispose(result);
                return StatusCode(result.StatusCode);
            }

            var wrappedStream = new MetricsRelayStreamWrapper(result.Body, result, _metrics);
            SetPassthroughHeaders(result);
            return new FileStreamResult(wrappedStream, result.ContentType ?? "application/octet-stream")
            {
                EnableRangeProcessing = true,
            };
        }
        catch (OperationCanceledException)
        {
            RecordAndDispose(result);
            throw;
        }
        catch (Exception)
        {
            RecordAndDispose(result);
            throw;
        }
    }

    /// <summary>
    /// Relays a live TV stream from TVHeadend for the given channel.
    /// Long-running — response streams until client disconnects.
    /// Supports both GET (full stream) and HEAD (capability discovery for Apple AVPlayer).
    /// </summary>
    /// <param name="channelId">TVHeadend channel UUID.</param>
    /// <param name="profile">Optional streaming profile override.</param>
    /// <param name="cancellationToken">Cancellation token — triggers upstream cancellation on disconnect.</param>
    /// <returns>A <see cref="Task"/> representing the streaming operation.</returns>
    [HttpGet("stream/{channelId}")]
    [HttpHead("stream/{channelId}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
    public async Task GetStream(string channelId, [FromQuery] string? profile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var config = _configProvider.Configuration;
        if (config == null || !config.RelayEnabled)
        {
            // Relay is disabled in plugin configuration — refuse to proxy (incl. HEAD probes).
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        // SECURITY: when relay token security is enabled, this open endpoint must NOT serve
        // anonymously — real clients receive tokenized /relay/stream URLs. Require a valid token
        // here too (for HEAD as well) so the token gate cannot be bypassed via /stream/{channelId}.
        RelayTokenValidationResult? validation = null;
        if (config.EnableRelayTokenSecurity)
        {
            validation = await ValidateStreamTokenAsync(channelId, cancellationToken).ConfigureAwait(false);
            if (validation == null)
            {
                return;
            }
        }

        // HEAD requests (after the gates): return headers only — never open a long-running stream.
        // Apple AVPlayer uses HEAD to discover Content-Type / Accept-Ranges before playback.
        if (HttpMethods.IsHead(Request.Method))
        {
            WriteStreamHeadResponse();
            return;
        }

        await StreamChannelCoreAsync(channelId, ResolveEffectiveProfile(validation, profile), validation?.TokenRecord, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates the relay token for a stream request. On failure, sets the appropriate response
    /// status code and returns <c>null</c>; on success returns the validated result (carrying the
    /// matched token record, including the rule-resolved profile).
    /// </summary>
    /// <param name="channelId">TVHeadend channel UUID the token must be scoped to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The validation result when the token is valid; otherwise <c>null</c> (status already set).</returns>
    private async Task<RelayTokenValidationResult?> ValidateStreamTokenAsync(string channelId, CancellationToken cancellationToken)
    {
        var tokenValidation = await _tokenValidator.ValidateAsync(
            RelayAuthorizationHelper.ExtractToken(Request),
            RelayType.Stream,
            channelId,
            cancellationToken).ConfigureAwait(false);

        if (tokenValidation.IsValid)
        {
            return tokenValidation;
        }

        Response.StatusCode = RelayAuthorizationHelper.MapToStatusCode(tokenValidation.FailureReason);
        return null;
    }

    /// <summary>
    /// Determines the streaming profile to use. When the request was token-secured, the profile
    /// issued in the token (the rule-resolved profile) is authoritative — the client cannot override
    /// it by tampering with the <c>?profile=</c> query parameter. Falls back to the requested profile
    /// only when no token scoped a profile (e.g. token security disabled).
    /// </summary>
    /// <param name="validation">The token validation result, or <c>null</c> when no token was required.</param>
    /// <param name="requestedProfile">The profile from the query string.</param>
    /// <returns>The effective profile to stream.</returns>
    private static string? ResolveEffectiveProfile(RelayTokenValidationResult? validation, string? requestedProfile)
        => !string.IsNullOrWhiteSpace(validation?.TokenRecord?.SelectedProfile)
            ? validation.TokenRecord.SelectedProfile
            : requestedProfile;

    /// <summary>
    /// Streams a live TV channel from TVHeadend to the client. Assumes the request is already
    /// authorized (token-validated, or token security disabled) and that the method is GET.
    /// <para>
    /// SINGLE MEASUREMENT POINT: the in-memory session tracker only feeds the live dashboard
    /// view; the one persistent record per stream request is the relay timing context, which
    /// is stamped with the session identity and token telemetry here and persisted exactly
    /// once in the <c>finally</c> block (or by the relay service on aborted connects).
    /// </para>
    /// </summary>
    /// <param name="channelId">TVHeadend channel UUID.</param>
    /// <param name="profile">Optional streaming profile override.</param>
    /// <param name="tokenRecord">The validated relay token record carrying the stream-setup telemetry, if any.</param>
    /// <param name="cancellationToken">Cancellation token — triggers upstream cancellation on disconnect.</param>
    /// <returns>A <see cref="Task"/> representing the streaming operation.</returns>
    private async Task StreamChannelCoreAsync(string channelId, string? profile, RelayTokenRecord? tokenRecord, CancellationToken cancellationToken)
    {
        // Start telemetry session immediately — visible in dashboard from this point.
        var hasRange = Request.Headers.ContainsKey("Range");
        var userAgent = Request.Headers.UserAgent.ToString();
        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        var session = _sessionTracker.StartSession(channelId, Request.Method, userAgent, remoteIp, hasRange);
        var sessionId = session.SessionId;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var bitrates = new StreamBitrateTracker();
        RelayResult? result = null;
        long totalBytes = 0;
        bool firstByteSent = false;
        var endedBy = StreamEndedBy.Unknown;

        try
        {
            // RelayStreamAsync increments the active-stream counter as its first step. Keeping the
            // call INSIDE this try guarantees the finally always releases that slot and finalizes the
            // session — even when the client cancels during the upstream connect (the call re-throws;
            // the relay service persists the aborted metric itself on that path).
            result = await _relay.RelayStreamAsync(channelId, profile, cancellationToken).ConfigureAwait(false);
            StampStreamTelemetry(result.TimingContext, session, tokenRecord, profile);

            if (result.Body == null)
            {
                // Upstream failed before any data — classify from the timing context.
                Response.StatusCode = result.StatusCode;
                endedBy = result.TimingContext?.UpstreamTimedOut == true
                    ? StreamEndedBy.UpstreamTimeout
                    : StreamEndedBy.UpstreamHttpError;
                return;
            }

            Response.StatusCode = result.StatusCode;
            Response.ContentType = result.ContentType ?? "video/mp2t";
            Response.Headers["Accept-Ranges"] = !string.IsNullOrEmpty(result.AcceptRanges)
                ? result.AcceptRanges
                : "none";

            // Startup latency is recorded when the FIRST byte is actually written to the client
            // (inside the loop below), not at header time, so the metric reflects real
            // time-to-first-byte rather than time-to-headers.

            // Stream directly from TVHeadend to client — zero intermediate buffering.
            var buffer = new byte[StreamCopyBufferSize];
            var lastUpdateTime = sw.ElapsedMilliseconds;
            int bytesRead;
            while ((bytesRead = await result.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (!firstByteSent)
                {
                    // Mark BEFORE the client write: this is the instant the first byte arrived
                    // from upstream. The to-client mark below fires only after the write completes,
                    // so the delta between the two reflects real downstream write latency.
                    result.TimingContext?.MarkFirstByteFromUpstream();
                }

                await Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                totalBytes += bytesRead;

                if (!firstByteSent)
                {
                    result.TimingContext?.MarkFirstByteToClient();
                    _sessionTracker.MarkFirstByteSent(sessionId, sw.Elapsed.TotalMilliseconds);
                    firstByteSent = true;
                }

                // Periodic session update every ~5 seconds — no DB write per packet.
                // Rolling = last ~10s window, Peak = max rolling sample, Average = cumulative.
                var elapsed = sw.ElapsedMilliseconds;
                if (elapsed - lastUpdateTime >= 5000)
                {
                    var rolling = bitrates.AddSample(elapsed, totalBytes);
                    var average = ComputeAverageBitrate(totalBytes, sw.Elapsed);
                    _sessionTracker.UpdateSession(sessionId, totalBytes, rolling, average, bitrates.PeakBitrate);
                    lastUpdateTime = elapsed;
                }
            }

            // Normal upstream EOF — stream completed.
            endedBy = StreamEndedBy.UpstreamEof;
        }
        catch (OperationCanceledException)
        {
            // Correct classification: disconnect after first byte = normal Live TV behavior.
            endedBy = firstByteSent
                ? StreamEndedBy.ClientDisconnectAfterFirstByte
                : StreamEndedBy.StartupCancelledBeforeFirstByte;
            if (result?.TimingContext != null)
            {
                result.TimingContext.ClientCancelled = true;
                result.TimingContext.FailureReason = RelayFailureReason.ClientCancelled;
            }
        }
        catch (IOException)
        {
            endedBy = StreamEndedBy.DownstreamWriteError;
            if (result?.TimingContext != null)
            {
                result.TimingContext.FailureReason = RelayFailureReason.DownstreamWriteFailed;
            }
        }
        catch (Exception)
        {
            endedBy = StreamEndedBy.UnexpectedException;
            if (result?.TimingContext != null)
            {
                result.TimingContext.FailureReason = RelayFailureReason.UnexpectedException;
            }
        }
        finally
        {
            // Always release the activity slot: RelayStreamAsync increments the counter as its
            // first step, so every entry here — success, upstream failure, or client-cancel during
            // connect — must release exactly once. DecrementStreams floors at 0.
            _activityTracker.DecrementStreams();

            // Final rolling sample so short sessions still yield an honest peak value.
            if (totalBytes > 0)
            {
                bitrates.AddSample(sw.Elapsed.TotalMilliseconds, totalBytes);
            }

            // Remove the in-memory session (live view only — no persistence here).
            _sessionTracker.FinalizeSession(sessionId, endedBy, firstByteSent);

            if (result?.TimingContext != null)
            {
                result.TimingContext.BytesSent = totalBytes;
                result.TimingContext.EndedBy = endedBy;
                result.TimingContext.PeakBitrate = totalBytes > 0 ? bitrates.PeakBitrate : null;
            }

            RecordAndDispose(result);
        }
    }

    /// <summary>
    /// Copies the session identity and the token-carried stream-setup telemetry onto the
    /// relay timing context — the single persistent record of the stream request.
    /// </summary>
    /// <param name="timing">The timing context created by the relay service.</param>
    /// <param name="session">The in-memory session started for this request.</param>
    /// <param name="tokenRecord">The validated relay token record, if any.</param>
    /// <param name="requestedProfile">The effective profile passed to the relay (query/token resolved).</param>
    private static void StampStreamTelemetry(
        RelayTimingContext? timing,
        ActiveStreamSession session,
        RelayTokenRecord? tokenRecord,
        string? requestedProfile)
    {
        if (timing == null)
        {
            return;
        }

        timing.SessionId = session.SessionId;
        timing.ChannelName = session.ChannelName;
        timing.ClientName = session.ClientName;
        timing.UserAgent = session.UserAgent;
        timing.RequestMethod = session.RequestMethod;
        timing.WasRangeRequest = session.RangeRequested;
        timing.EffectiveProfile = tokenRecord?.SelectedProfile ?? requestedProfile;
        timing.ResolutionSource = tokenRecord?.ResolutionSource;
        timing.MediaInfoCacheStatus = tokenRecord?.MediaInfoCacheStatus ?? "unknown";
        timing.StreamSetupMs = tokenRecord?.StreamSetupMs;
    }

    /// <summary>
    /// Public token-secured relay endpoint for live TV streams.
    /// Validates the plugin-issued relay token before proxying to TVHeadend.
    /// Token is checked only at request start — active streams are not killed by token expiry.
    /// </summary>
    /// <param name="channelId">TVHeadend channel UUID.</param>
    /// <param name="profile">Optional streaming profile override.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the streaming operation.</returns>
    [HttpGet("relay/stream/{channelId}")]
    [HttpHead("relay/stream/{channelId}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task GetTokenSecuredStream(string channelId, [FromQuery] string? profile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var config = _configProvider.Configuration;
        if (config == null || !config.RelayEnabled)
        {
            Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        // Validate the relay token at request start — for HEAD as well as GET, since legitimate
        // clients (e.g. Apple AVPlayer) always have the token embedded in the MediaSource URL.
        var validation = await ValidateStreamTokenAsync(channelId, cancellationToken).ConfigureAwait(false);
        if (validation == null)
        {
            return;
        }

        // HEAD capability discovery (after token validation).
        if (HttpMethods.IsHead(Request.Method))
        {
            WriteStreamHeadResponse();
            return;
        }

        // Delegate to the shared streaming core. Use the profile issued in the token (rule-resolved)
        // so a client cannot override it by tampering with ?profile=.
        await StreamChannelCoreAsync(channelId, ResolveEffectiveProfile(validation, profile), validation.TokenRecord, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Public token-secured relay endpoint for images (channel logos, EPG images, thumbnails).
    /// Validates the plugin-issued relay token before proxying to TVHeadend.
    /// </summary>
    /// <param name="path">Relative TVHeadend image path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The proxied image with original headers.</returns>
    [HttpGet("relay/images/{**path}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetTokenSecuredImage(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("Image path is required.");
        }

        var config = _configProvider.Configuration;
        if (config == null || !config.RelayEnabled)
        {
            // Relay is disabled in plugin configuration — refuse before token validation,
            // mirroring GetTokenSecuredStream.
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        // Validate relay token
        var tokenValidation = await _tokenValidator.ValidateAsync(
            RelayAuthorizationHelper.ExtractToken(Request),
            RelayType.Image,
            path,
            cancellationToken).ConfigureAwait(false);

        if (!tokenValidation.IsValid)
        {
            return StatusCode(RelayAuthorizationHelper.MapToStatusCode(tokenValidation.FailureReason));
        }

        // Delegate to existing image relay logic
        return await GetImage(path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Computes the cumulative average bitrate in bits per second since stream start.
    /// </summary>
    private static double ComputeAverageBitrate(long totalBytes, TimeSpan elapsed)
        => elapsed.TotalSeconds > 0.001 ? (totalBytes * 8.0) / elapsed.TotalSeconds : 0;

    /// <summary>
    /// Writes the HEAD response for stream capability discovery (Apple AVPlayer).
    /// Returns only the content type and range support — no channel data, no upstream connection.
    /// </summary>
    private void WriteStreamHeadResponse()
    {
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "video/mp2t";
        Response.Headers["Accept-Ranges"] = "none";
    }

    private void RecordAndDispose(RelayResult? result)
    {
        if (result?.TimingContext != null)
        {
            try
            {
                _metrics.RecordMetric(result.TimingContext.ToMetric());
            }
            catch (Exception)
            {
                // Best-effort — never let metrics recording break the relay.
            }
        }

        result?.Dispose();
    }

    /// <summary>
    /// Copies relevant cache/metadata headers from the upstream response to the client response.
    /// </summary>
    private void SetPassthroughHeaders(RelayResult result)
    {
        if (!string.IsNullOrEmpty(result.ETag))
        {
            Response.Headers["ETag"] = result.ETag;
        }

        if (!string.IsNullOrEmpty(result.LastModified))
        {
            Response.Headers["Last-Modified"] = result.LastModified;
        }

        if (!string.IsNullOrEmpty(result.CacheControl))
        {
            Response.Headers["Cache-Control"] = result.CacheControl;
        }
        else
        {
            Response.Headers["Cache-Control"] = "public, max-age=3600, stale-while-revalidate=86400";
        }
    }

    /// <summary>
    /// Stream wrapper that tracks bytes read and records metrics on disposal.
    /// Used for image relay where ASP.NET Core owns the stream lifecycle.
    /// </summary>
    private sealed class MetricsRelayStreamWrapper : Stream
    {
        private readonly Stream _inner;
        private readonly RelayResult _result;
        private readonly IRelayMetricsService _metrics;
        private long _totalBytes;
        private bool _firstByte;

        public MetricsRelayStreamWrapper(Stream inner, RelayResult result, IRelayMetricsService metrics)
        {
            _inner = inner;
            _result = result;
            _metrics = metrics;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            TrackBytes(read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            TrackBytes(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            TrackBytes(read);
            return read;
        }

        private void TrackBytes(int read)
        {
            if (read > 0)
            {
                _totalBytes += read;
                if (!_firstByte)
                {
                    // Reads from this wrapper are performed by ASP.NET's FileStreamResult writer
                    // copying bytes into the client response, so the first successful read is the
                    // moment the first byte flows to the client. The first-byte-from-upstream mark
                    // is recorded by the relay service when upstream bytes actually arrive.
                    _firstByte = true;
                    _result.TimingContext?.MarkFirstByteToClient();
                }
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => _inner.Flush();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_result.TimingContext != null)
                {
                    _result.TimingContext.BytesSent = _totalBytes;
                }

                try
                {
                    if (_result.TimingContext != null)
                    {
                        _metrics.RecordMetric(_result.TimingContext.ToMetric());
                    }
                }
                catch (Exception)
                {
                    // Best-effort metrics recording.
                }

                _inner.Dispose();
                _result.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
