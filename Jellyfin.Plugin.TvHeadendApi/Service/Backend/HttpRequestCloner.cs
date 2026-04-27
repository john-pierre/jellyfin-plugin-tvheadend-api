using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Backend;

/// <summary>
/// Provides a shared helper for cloning <see cref="HttpRequestMessage"/> instances.
/// Used by <see cref="Auth.DigestAuthHandler"/> and <see cref="Resilience.ResilienceHandler"/>
/// when requests must be retried (the original content stream may already be consumed).
/// </summary>
internal static class HttpRequestCloner
{
    /// <summary>
    /// Creates a deep clone of the given <see cref="HttpRequestMessage"/>,
    /// including content bytes, headers, version, and options.
    /// </summary>
    /// <param name="request">The original request to clone.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A new <see cref="HttpRequestMessage"/> with the same method, URI, headers, content, and options.</returns>
    internal static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
        };

        if (request.Content is not null)
        {
            var contentBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            clone.Content = new ByteArrayContent(contentBytes);
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in request.Options)
        {
            ((IDictionary<string, object?>)clone.Options).Add(option);
        }

        return clone;
    }
}
