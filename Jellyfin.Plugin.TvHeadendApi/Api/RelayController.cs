// Relay API controller — exposes TVHeadend images and streams through authenticated Jellyfin endpoints.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayController"/> class.
    /// </summary>
    /// <param name="relay">The relay service for proxying TVHeadend requests.</param>
    public RelayController(IRelayService relay)
    {
        _relay = relay ?? throw new ArgumentNullException(nameof(relay));
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

        var result = await _relay.RelayImageAsync(path, cancellationToken).ConfigureAwait(false);

        if (result.Body == null)
        {
            result.Dispose();
            return StatusCode(result.StatusCode);
        }

        // FileStreamResult takes ownership of result.Body and disposes it.
        // We must also ensure the underlying HttpResponseMessage is disposed when the stream completes.
        // Wrap body in a stream that disposes the result on close.
        var wrappedStream = new RelayStreamWrapper(result.Body, result);

        SetPassthroughHeaders(result);
        return new FileStreamResult(wrappedStream, result.ContentType ?? "application/octet-stream")
        {
            EnableRangeProcessing = true,
        };
    }

    /// <summary>
    /// Relays a live TV stream from TVHeadend for the given channel.
    /// Long-running — response streams until client disconnects.
    /// </summary>
    /// <param name="channelId">TVHeadend channel UUID.</param>
    /// <param name="profile">Optional streaming profile override.</param>
    /// <param name="cancellationToken">Cancellation token — triggers upstream cancellation on disconnect.</param>
    /// <returns>The proxied stream with original Content-Type.</returns>
    [HttpGet("stream/{channelId}")]
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
                return;
            }

            Response.StatusCode = result.StatusCode;
            Response.ContentType = result.ContentType ?? "video/MP2T";

            if (result.ContentLength.HasValue)
            {
                Response.ContentLength = result.ContentLength;
            }

            // Stream directly from TVHeadend to client — zero intermediate buffering.
            await result.Body.CopyToAsync(Response.Body, StreamCopyBufferSize, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            result.Dispose();
        }
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
            // Sane default for images — cache for 1 hour, allow stale for 1 day.
            Response.Headers["Cache-Control"] = "public, max-age=3600, stale-while-revalidate=86400";
        }
    }

    /// <summary>
    /// A stream wrapper that disposes an associated <see cref="RelayResult"/> when the stream is closed.
    /// This ensures the upstream HTTP response is cleaned up after the response body is fully sent.
    /// </summary>
    private sealed class RelayStreamWrapper : Stream
    {
        private readonly Stream _inner;
        private readonly IDisposable _owner;

        public RelayStreamWrapper(Stream inner, IDisposable owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => _inner.Flush();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _owner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
