// Classifies exceptions and HTTP responses into consistent FailureReason values.

using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Resilience;

/// <summary>
/// Maps exceptions and HTTP status codes to <see cref="FailureReason"/> values.
/// Used by the health service and resilience handler for consistent failure tracking.
/// </summary>
internal static class FailureClassifier
{
    /// <summary>
    /// Classifies an exception into a <see cref="FailureReason"/>.
    /// </summary>
    /// <param name="ex">The exception to classify.</param>
    /// <returns>The classified failure reason.</returns>
    internal static FailureReason Classify(Exception ex)
    {
        return ex switch
        {
            // HttpClient.Timeout surfaces as TaskCanceledException wrapping TimeoutException
            // (.NET 5+) — that is a backend timeout, not a caller cancellation.
            TaskCanceledException tce when tce.InnerException is TimeoutException => FailureReason.Timeout,
            TaskCanceledException => FailureReason.Cancelled,
            OperationCanceledException => FailureReason.Cancelled,
            HttpRequestException httpEx => ClassifyHttpRequestException(httpEx),
            InvalidOperationException ioe when ioe.Message.Contains("Circuit breaker", StringComparison.OrdinalIgnoreCase) => FailureReason.CircuitOpen,
            _ => FailureReason.UnexpectedException,
        };
    }

    /// <summary>
    /// Classifies an HTTP status code into a <see cref="FailureReason"/>.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <returns>The classified failure reason.</returns>
    internal static FailureReason ClassifyStatusCode(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => FailureReason.AuthFailed,
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => FailureReason.Timeout,
            _ when code >= 500 => FailureReason.Upstream5xx,
            _ when code >= 400 => FailureReason.Upstream4xx,
            _ => FailureReason.None,
        };
    }

    private static FailureReason ClassifyHttpRequestException(HttpRequestException ex)
    {
        // Check inner exception for socket-level failures
        if (ex.InnerException is SocketException socketEx)
        {
            return socketEx.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => FailureReason.ConnectionRefused,
                SocketError.HostNotFound or SocketError.NoData => FailureReason.DnsFailure,
                SocketError.TimedOut => FailureReason.Timeout,
                _ => FailureReason.UpstreamUnavailable,
            };
        }

        // DNS resolution failures on some platforms
        if (ex.Message.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("No such host", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("nodename nor servname", StringComparison.OrdinalIgnoreCase))
        {
            return FailureReason.DnsFailure;
        }

        if (ex.Message.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
        {
            return FailureReason.ConnectionRefused;
        }

        // Check HTTP status code if available (.NET 7+)
        if (ex.StatusCode.HasValue)
        {
            return ClassifyStatusCode(ex.StatusCode.Value);
        }

        return FailureReason.UpstreamUnavailable;
    }
}
