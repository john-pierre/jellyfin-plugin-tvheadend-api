using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Dvr;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Dvr;

/// <summary>
/// Orchestrates DVR timer operations by delegating to <see cref="SingleTimerService"/> and <see cref="SeriesTimerService"/>.
/// Holds shared infrastructure (API client, URL builder, config) used by both sub-services.
/// </summary>
internal sealed class DvrService : IDvrService
{
    private readonly SingleTimerService _singleTimer;
    private readonly SeriesTimerService _seriesTimer;
    private readonly ILogger<DvrService> _logger;
    private readonly IApiClient _apiClient;
    private readonly IUrlBuilder _urlBuilder;

    /// <summary>
    /// Initializes a new instance of the <see cref="DvrService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="tvheadendApiClient">TVHeadend API client.</param>
    /// <param name="tvheadendUrlBuilder">TVHeadend URL builder.</param>
    public DvrService(
        ILogger<DvrService> logger,
        IApiClient tvheadendApiClient,
        IUrlBuilder tvheadendUrlBuilder)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiClient = tvheadendApiClient ?? throw new ArgumentNullException(nameof(tvheadendApiClient));
        _urlBuilder = tvheadendUrlBuilder ?? throw new ArgumentNullException(nameof(tvheadendUrlBuilder));

        _singleTimer = new SingleTimerService(this, logger);
        _seriesTimer = new SeriesTimerService(this, logger);
    }

    /// <summary>Gets the API client for sub-services.</summary>
    internal IApiClient ApiClient => _apiClient;

    /// <summary>Gets the URL builder for sub-services.</summary>
    internal IUrlBuilder UrlBuilder => _urlBuilder;

    /// <inheritdoc />
    public async Task<string> GetRecordingProfileUuidAsync(string profileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profileName);

        var config = GetConfig();
        var url = _urlBuilder.BuildApiUrl(config, "api/dvr/config/grid");
        using var httpClient = _apiClient.CreateApiHttpClient(config);
        var result = await GridFetcher.FetchAllAsync<DvrConfigGridResponse>(
            httpClient,
            url,
            r => r.Total,
            _logger,
            cancellationToken).ConfigureAwait(false);

        var matchingProfile = result?.Entries?.FirstOrDefault(profile =>
            string.Equals(profile.Name, profileName, StringComparison.OrdinalIgnoreCase));

        if (matchingProfile == null && result?.Entries?.Count > 0)
        {
            matchingProfile = result.Entries[0];
        }

        if (string.IsNullOrWhiteSpace(matchingProfile?.Uuid))
        {
            throw new InvalidOperationException($"No matching recording profile found for '{profileName}' in TVHeadend.");
        }

        return matchingProfile.Uuid;
    }

    /// <summary>Gets the current plugin configuration or throws.</summary>
    /// <returns>The current plugin configuration.</returns>
    internal PluginConfiguration GetConfig()
    {
        return _apiClient.GetCurrentConfiguration()
            ?? throw new InvalidOperationException("Plugin configuration is not available.");
    }

    // ── Single timer delegation ──

    /// <inheritdoc />
    public Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
        => _singleTimer.CancelTimerAsync(timerId, cancellationToken);

    /// <inheritdoc />
    public Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
        => _singleTimer.CreateTimerAsync(info, cancellationToken);

    /// <inheritdoc />
    public Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken)
        => _singleTimer.UpdateTimerAsync(updatedTimer, cancellationToken);

    /// <inheritdoc />
    public Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
        => _singleTimer.GetTimersAsync(cancellationToken);

    // ── Series timer delegation ──

    /// <inheritdoc />
    public Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
        => _seriesTimer.CancelSeriesTimerAsync(timerId, cancellationToken);

    /// <inheritdoc />
    public Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        => _seriesTimer.CreateSeriesTimerAsync(info, cancellationToken);

    /// <inheritdoc />
    public Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
        => _seriesTimer.UpdateSeriesTimerAsync(info, cancellationToken);

    /// <inheritdoc />
    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(ProgramInfo program, CancellationToken cancellationToken)
        => _seriesTimer.GetNewTimerDefaultsAsync(program, cancellationToken);

    /// <inheritdoc />
    public Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
        => _seriesTimer.GetSeriesTimersAsync(cancellationToken);

    // ── Shared utilities for sub-services ──

    /// <summary>
    /// Tries to extract the created entity ID from a TVHeadend create-response JSON body.
    /// </summary>
    /// <param name="responseBody">The raw JSON response body.</param>
    /// <returns>The extracted entity ID, or <c>null</c> if extraction failed.</returns>
    internal static string? TryExtractCreatedEntityId(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return ExtractId(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractId(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (TryGetStringProperty(element, "uuid", out var uuid))
        {
            return uuid;
        }

        if (TryGetStringProperty(element, "id", out var id))
        {
            return id;
        }

        if (element.TryGetProperty("entry", out var entry))
        {
            var entryId = ExtractId(entry);
            if (!string.IsNullOrWhiteSpace(entryId))
            {
                return entryId;
            }
        }

        if (!element.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in entries.EnumerateArray())
        {
            var itemId = ExtractId(item);
            if (!string.IsNullOrWhiteSpace(itemId))
            {
                return itemId;
            }
        }

        return null;
    }

    private static bool TryGetStringProperty(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }
}
