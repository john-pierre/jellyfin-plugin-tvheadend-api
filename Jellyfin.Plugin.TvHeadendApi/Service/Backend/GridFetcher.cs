using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Common;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Backend;

/// <summary>
/// Fetches TVHeadend grid API responses dynamically, using a probe request to determine
/// the actual total count and then fetching all entries in a single follow-up request.
/// </summary>
internal static class GridFetcher
{
    private const int ProbeLimit = 50;

    // Cached UTF8Encoding instances to avoid allocating on every call (hot path for grid fetches).
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding LenientUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>
    /// Fetches all entries from a TVHeadend grid endpoint by first probing for the total count.
    /// </summary>
    /// <typeparam name="TResponse">Grid response type (must have Entries and a total field).</typeparam>
    /// <param name="httpClient">Pre-configured HTTP client.</param>
    /// <param name="baseUrl">Full URL without limit/start parameters.</param>
    /// <param name="getTotalCount">Delegate that extracts the total count from the response.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The full grid response containing all entries.</returns>
    public static async Task<TResponse?> FetchAllAsync<TResponse>(
        HttpClient httpClient,
        string baseUrl,
        Func<TResponse, int> getTotalCount,
        ILogger logger,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        var separator = baseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";

        // Probe: fetch a small batch to discover the total count
        var probeUrl = $"{baseUrl}{separator}start=0&limit={ProbeLimit}";
        var probeResponse = await FetchGridAsync<TResponse>(httpClient, probeUrl, cancellationToken).ConfigureAwait(false);
        if (probeResponse == null)
        {
            return null;
        }

        var total = getTotalCount(probeResponse);
        if (total <= ProbeLimit)
        {
            // The probe already contains everything
            logger.LogDebug("Grid fetch complete in probe request: {Total} entries from {Url}", total, baseUrl);
            return probeResponse;
        }

        // Full fetch with the real total
        logger.LogDebug("Grid probe returned {ProbeCount}/{Total} entries, fetching all from {Url}", ProbeLimit, total, baseUrl);
        var fullUrl = $"{baseUrl}{separator}start=0&limit={total}";
        return await FetchGridAsync<TResponse>(httpClient, fullUrl, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TResponse?> FetchGridAsync<TResponse>(
        HttpClient httpClient,
        string url,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // TVHeadend may emit invalid UTF-8 bytes from DVB sources (e.g. truncated multi-byte sequences).
        // System.Text.Json uses DecoderExceptionFallback and throws on invalid UTF-8.
        // Read raw bytes and sanitize before deserializing.
        var rawBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var sanitized = SanitizeUtf8(rawBytes);
        using var stream = new MemoryStream(sanitized);
        return await JsonSerializer.DeserializeAsync<TResponse>(stream, JsonDefaults.Api, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces invalid UTF-8 byte sequences with the UTF-8 encoding of U+FFFD (replacement character).
    /// This prevents <see cref="JsonSerializer"/> from throwing on malformed DVB strings.
    /// </summary>
    private static byte[] SanitizeUtf8(byte[] input)
    {
        // Fast path: if the input is valid UTF-8, return it as-is.
        try
        {
            StrictUtf8.GetCharCount(input);
            return input;
        }
        catch (DecoderFallbackException)
        {
            // Slow path: re-decode with replacement fallback and re-encode.
        }

        var chars = LenientUtf8.GetChars(input);
        return LenientUtf8.GetBytes(chars);
    }
}
