// Relay result model — carries upstream response metadata and body stream without buffering.

using System;
using System.Net.Http;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// Represents the result of a relay operation, carrying the upstream response metadata
/// and body stream without buffering.
/// </summary>
public sealed class RelayResult : IDisposable
{
    /// <summary>
    /// Gets or sets the HTTP status code from the upstream TVHeadend response.
    /// </summary>
    public int StatusCode { get; set; }

    /// <summary>
    /// Gets or sets the Content-Type header value from the upstream response.
    /// </summary>
    public string? ContentType { get; set; }

    /// <summary>
    /// Gets or sets the Content-Length from the upstream response, if known.
    /// </summary>
    public long? ContentLength { get; set; }

    /// <summary>
    /// Gets or sets the ETag header from the upstream response.
    /// </summary>
    public string? ETag { get; set; }

    /// <summary>
    /// Gets or sets the Last-Modified header from the upstream response.
    /// </summary>
    public string? LastModified { get; set; }

    /// <summary>
    /// Gets or sets the Cache-Control header from the upstream response.
    /// </summary>
    public string? CacheControl { get; set; }

    /// <summary>
    /// Gets or sets the upstream response body stream. Streamed directly — never fully buffered.
    /// </summary>
    public System.IO.Stream? Body { get; set; }

    /// <summary>
    /// Gets or sets the underlying HTTP response message for proper disposal.
    /// </summary>
    internal HttpResponseMessage? ResponseMessage { get; set; }

    /// <summary>
    /// Gets or sets the timing context for metrics collection. Populated by the relay service.
    /// </summary>
    internal RelayTimingContext? TimingContext { get; set; }

    /// <inheritdoc />
    public void Dispose()
    {
        Body?.Dispose();
        ResponseMessage?.Dispose();
    }
}
