using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Helper;

/// <summary>
/// Fetches TVHeadend grid API responses dynamically, using a probe request to determine
/// the actual total count and then fetching all entries in a single follow-up request.
/// </summary>
internal static class GridFetcher
{
    private const int ProbeLimit = 50;

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
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<TResponse>(stream, JsonDefaults.Api, cancellationToken).ConfigureAwait(false);
    }
}
