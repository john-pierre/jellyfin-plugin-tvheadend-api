// Relay API controller — exposes TVHeadend images and streams through authenticated Jellyfin endpoints.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Relay;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

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
    private readonly IRelayUrlBuilder _relayUrlBuilder;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayController"/> class.
    /// </summary>
    /// <param name="relay">The relay service for proxying TVHeadend requests.</param>
    /// <param name="metrics">The relay metrics persistence service.</param>
    /// <param name="activityTracker">Live stream activity tracker.</param>
    /// <param name="relayUrlBuilder">Relay URL builder for host resolution.</param>
    public RelayController(IRelayService relay, IRelayMetricsService metrics, RelayActivityTracker activityTracker, IRelayUrlBuilder relayUrlBuilder)
    {
        _relay = relay ?? throw new ArgumentNullException(nameof(relay));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _activityTracker = activityTracker ?? throw new ArgumentNullException(nameof(activityTracker));
        _relayUrlBuilder = relayUrlBuilder ?? throw new ArgumentNullException(nameof(relayUrlBuilder));
    }

    /// <summary>
    /// Returns the relay service status — confirms the relay endpoints are reachable and reports the effective host.
    /// </summary>
    /// <returns>Relay status object.</returns>
    [HttpGet("status")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetRelayStatus()
    {
        return Ok(new
        {
            Status = "ok",
            Enabled = true,
            ActiveStreams = _activityTracker.ActiveStreams,
            EffectiveHost = _relayUrlBuilder.GetEffectiveBaseUrl(),
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
    [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> GetImage(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return BadRequest("Image path is required.");
        }

        RelayResult? result = null;
        try
        {
            result = await _relay.RelayImageAsync(path, cancellationToken).ConfigureAwait(false);

            if (result.Body == null)
            {
                RecordAndDispose(result);
                return StatusCode(result.StatusCode);
            }

            result.TimingContext?.MarkFirstByteToClient();

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
    /// </summary>
    /// <param name="channelId">TVHeadend channel UUID.</param>
    /// <param name="profile">Optional streaming profile override.</param>
    /// <param name="cancellationToken">Cancellation token — triggers upstream cancellation on disconnect.</param>
    /// <returns>A <see cref="Task"/> representing the streaming operation.</returns>
    [HttpGet("stream/{channelId}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
    public async Task GetStream(string channelId, [FromQuery] string? profile, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var result = await _relay.RelayStreamAsync(channelId, profile, cancellationToken).ConfigureAwait(false);

        try
        {
            if (result.Body == null)
            {
                Response.StatusCode = result.StatusCode;
                RecordAndDispose(result);
                return;
            }

            Response.StatusCode = result.StatusCode;
            Response.ContentType = result.ContentType ?? "video/MP2T";

            if (result.ContentLength.HasValue)
            {
                Response.ContentLength = result.ContentLength;
            }

            result.TimingContext?.MarkFirstByteToClient();

            // Stream directly from TVHeadend to client — zero intermediate buffering.
            // Track bytes transferred for metrics.
            var buffer = new byte[StreamCopyBufferSize];
            long totalBytes = 0;
            bool firstByteMarked = false;
            int bytesRead;
            while ((bytesRead = await result.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                totalBytes += bytesRead;

                if (!firstByteMarked)
                {
                    result.TimingContext?.MarkFirstByteFromUpstream();
                    firstByteMarked = true;
                }
            }

            if (result.TimingContext != null)
            {
                result.TimingContext.BytesSent = totalBytes;
                result.TimingContext.EndedBy = StreamEndedBy.Completed;
            }
        }
        catch (OperationCanceledException)
        {
            if (result.TimingContext != null)
            {
                result.TimingContext.ClientCancelled = true;
                result.TimingContext.FailureReason = RelayFailureReason.ClientCancelled;
                result.TimingContext.EndedBy = StreamEndedBy.ClientCancelled;
            }
        }
        catch (IOException)
        {
            if (result.TimingContext != null)
            {
                result.TimingContext.FailureReason = RelayFailureReason.DownstreamWriteFailed;
                result.TimingContext.EndedBy = StreamEndedBy.DownstreamError;
            }
        }
        catch (Exception)
        {
            if (result.TimingContext != null)
            {
                result.TimingContext.FailureReason = RelayFailureReason.UnexpectedException;
                result.TimingContext.EndedBy = StreamEndedBy.Unknown;
            }
        }
        finally
        {
            if (result.TimingContext?.RelayType == RelayType.Stream)
            {
                _activityTracker.DecrementStreams();
            }

            RecordAndDispose(result);
        }
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
                    _firstByte = true;
                    _result.TimingContext?.MarkFirstByteFromUpstream();
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
