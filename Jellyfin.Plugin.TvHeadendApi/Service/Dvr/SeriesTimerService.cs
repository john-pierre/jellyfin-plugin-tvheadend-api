using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Dvr;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Common;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Dvr;

/// <summary>
/// Handles series-timer (autorec) DVR operations: create, cancel, update, list, and defaults.
/// </summary>
internal sealed class SeriesTimerService
{
    private readonly DvrService _dvr;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeriesTimerService"/> class.
    /// </summary>
    /// <param name="dvr">Parent DVR service providing shared infrastructure.</param>
    /// <param name="logger">Logger instance.</param>
    internal SeriesTimerService(DvrService dvr, ILogger logger)
    {
        _dvr = dvr ?? throw new ArgumentNullException(nameof(dvr));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timerId);

        var config = _dvr.GetConfig();
        var url = _dvr.UrlBuilder.BuildApiUrl(config, "api/idnode/delete");
        var requestPayload = new[] { new KeyValuePair<string, string>("uuid", timerId) };
        using var httpClient = _dvr.ApiClient.CreateApiHttpClient(config);
        using var response = await _dvr.ApiClient.PostFormAsync(
            httpClient,
            url,
            requestPayload,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogError(
                "TVHeadend series timer cancel failed. URL={Url}, RequestPayload={RequestPayload}, Status={Status}, Response={Response}",
                url,
                JsonSerializer.Serialize(requestPayload),
                response.StatusCode,
                responseContent);
            throw new InvalidOperationException($"Failed to cancel series timer with ID: {timerId}. HTTP Status: {response.StatusCode}. Response: {responseContent}.");
        }
    }

    public async Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentException.ThrowIfNullOrWhiteSpace(info.Name);

        // ChannelId is required only when RecordAnyChannel is false
        if (!info.RecordAnyChannel)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(info.ChannelId);
        }

        var config = _dvr.GetConfig();
        var configUuid = await _dvr.GetRecordingProfileUuidAsync(config.RecordingProfile, cancellationToken).ConfigureAwait(false);
        string path;
        string requestBodyJson;
        IEnumerable<KeyValuePair<string, string>> formValues;

        if (!string.IsNullOrWhiteSpace(info.ProgramId))
        {
            path = "api/dvr/autorec/create_by_series";
            var pairs = new[]
            {
                new KeyValuePair<string, string>("config_uuid", configUuid),
                new KeyValuePair<string, string>("event_id", info.ProgramId),
            };
            requestBodyJson = JsonSerializer.Serialize(pairs);
            formValues = pairs;
        }
        else
        {
            path = "api/dvr/autorec/create";
            var seriesTimerJson = new
            {
                channel = info.RecordAnyChannel ? null : info.ChannelId,
                name = info.Name,
                title = info.Name,
                comment = info.Overview,
                record = info.RecordNewOnly ? 1 : 0,
                start = info.RecordAnyTime ? "Any" : null,
                start_window = info.RecordAnyTime ? "Any" : null,
                pri = config.Priority,
                start_extra = (int)Math.Round((double)info.PrePaddingSeconds / 60),
                stop_extra = (int)Math.Round((double)info.PostPaddingSeconds / 60),
                weekdays = BuildWeekdaysPayload(info.Days),
                config_name = configUuid,
            };

            requestBodyJson = JsonSerializer.Serialize(seriesTimerJson, JsonDefaults.Api);
            formValues = new[] { new KeyValuePair<string, string>("conf", requestBodyJson) };
        }

        var url = _dvr.UrlBuilder.BuildApiUrl(config, path);
        _logger.LogDebug(
            "TVHeadend series timer create request. URL={Url}, RequestBody={RequestBody}",
            url,
            requestBodyJson);
        using var httpClient = _dvr.ApiClient.CreateApiHttpClient(config);
        using var response = await _dvr.ApiClient.PostFormAsync(
            httpClient,
            url,
            formValues,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogError(
                "TVHeadend series timer create failed. URL={Url}, RequestBody={RequestBody}, Status={Status}, Response={Response}",
                url,
                requestBodyJson,
                response.StatusCode,
                responseContent);
            throw new InvalidOperationException($"Failed to create series timer for series '{info.Name}' on channel ID: {info.ChannelId}. HTTP Status: {response.StatusCode}. Response: {responseContent}.");
        }

        var createResponse = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var createdId = DvrService.TryExtractCreatedEntityId(createResponse);
        if (!string.IsNullOrWhiteSpace(createdId))
        {
            info.Id = createdId;
        }
        else if (string.IsNullOrWhiteSpace(info.Id))
        {
            info.Id = Guid.NewGuid().ToString("N");
        }
    }

    public async Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentException.ThrowIfNullOrWhiteSpace(info.Id);

        var config = _dvr.GetConfig();
        var url = _dvr.UrlBuilder.BuildApiUrl(config, "api/idnode/save");
        var updates = new Dictionary<string, object?>
        {
            { "uuid", info.Id },
            { "record", info.RecordNewOnly ? 1 : 0 },
            { "start", info.RecordAnyTime ? "Any" : null },
            { "start_window", info.RecordAnyTime ? "Any" : null },
            { "pri", info.Priority },
            { "start_extra", (int)Math.Round((double)info.PrePaddingSeconds / 60) },
            { "stop_extra", (int)Math.Round((double)info.PostPaddingSeconds / 60) },
            { "weekdays", BuildWeekdaysPayload(info.Days) },
        };

        if (!string.IsNullOrWhiteSpace(info.ChannelId))
        {
            updates["channel"] = info.ChannelId;
        }

        if (!string.IsNullOrWhiteSpace(info.Name))
        {
            updates["name"] = info.Name;
            updates["title"] = info.Name;
        }

        if (!string.IsNullOrWhiteSpace(info.Overview))
        {
            updates["comment"] = info.Overview;
        }

        var nodeJson = JsonSerializer.Serialize(new[] { updates }, JsonDefaults.Api);
        var formValues = new[] { new KeyValuePair<string, string>("node", nodeJson) };
        _logger.LogDebug(
            "TVHeadend series timer update request. URL={Url}, RequestBody={RequestBody}",
            url,
            nodeJson);
        using var httpClient = _dvr.ApiClient.CreateApiHttpClient(config);
        using var response = await _dvr.ApiClient.PostFormAsync(
            httpClient,
            url,
            formValues,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogError(
                "TVHeadend series timer update failed. URL={Url}, RequestBody={RequestBody}, Status={Status}, Response={Response}",
                url,
                nodeJson,
                response.StatusCode,
                responseContent);
            throw new InvalidOperationException($"Failed to update series timer with ID: {info.Id}. HTTP Status: {response.StatusCode}. Response: {responseContent}");
        }
    }

    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(ProgramInfo program, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = _dvr.GetConfig();
        return Task.FromResult(new SeriesTimerInfo
        {
            Id = Guid.NewGuid().ToString(),
            ChannelId = program?.ChannelId,
            Name = program?.Name,
            Overview = program?.Overview,
            RecordNewOnly = config.SeriesRecordNewOnly,
            RecordAnyTime = config.SeriesRecordAnyTime,
            RecordAnyChannel = config.SeriesRecordAnyChannel,
            Priority = config.Priority,
            PrePaddingSeconds = config.PrePaddingSeconds,
            PostPaddingSeconds = config.PostPaddingSeconds,
            KeepUntil = KeepUntil.UntilDeleted,
            Days = Enum.GetValues(typeof(DayOfWeek)).Cast<DayOfWeek>().ToList(),
        });
    }

    public async Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = _dvr.GetConfig();
            var url = _dvr.UrlBuilder.BuildApiUrl(config, "api/dvr/autorec/grid");
            using var httpClient = _dvr.ApiClient.CreateApiHttpClient(config);
            var result = await GridFetcher.FetchAllAsync<DvrAutoRecGridResponse>(
                httpClient,
                url,
                r => r.Total,
                _logger,
                cancellationToken).ConfigureAwait(false);
            return result?.Entries?.Select(entry => new SeriesTimerInfo
            {
                Id = entry.Uuid,
                Name = entry.Name,
                ChannelId = string.IsNullOrEmpty(entry.Channel) ? null : entry.Channel,
                Priority = entry.Priority,
                Overview = entry.Comment,
                Days = entry.Weekdays?.Where(day => day >= 1 && day <= 7)
                    // TVH: 1=Mon..6=Sat, 7=Sun; DayOfWeek: 0=Sun, 1=Mon..6=Sat
                    .Select(day => day == 7 ? DayOfWeek.Sunday : (DayOfWeek)day)
                    .ToList() ?? new List<DayOfWeek>(),
                RecordNewOnly = entry.RecordMode is not 0 and not 15,
                RecordAnyTime = string.IsNullOrEmpty(entry.Start) || string.Equals(entry.Start, "Any", StringComparison.OrdinalIgnoreCase),
                RecordAnyChannel = string.IsNullOrEmpty(entry.Channel),
                PrePaddingSeconds = entry.StartExtra * 60,
                PostPaddingSeconds = entry.StopExtra * 60,
                StartDate = DateTime.UtcNow,
                EndDate = DateTime.UtcNow.AddHours(1),
            }).ToList() ?? Enumerable.Empty<SeriesTimerInfo>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching series timers from TVHeadEnd.");
            return Enumerable.Empty<SeriesTimerInfo>();
        }
    }

    private static List<int> BuildWeekdaysPayload(List<DayOfWeek>? days)
    {
        if (days == null || days.Count == 0)
        {
            return new List<int> { 1, 2, 3, 4, 5, 6, 7 };
        }

        return days
            .Distinct()
            // TVH: 1=Mon..6=Sat, 7=Sun; DayOfWeek: 0=Sun, 1=Mon..6=Sat
            .Select(day => day == DayOfWeek.Sunday ? 7 : (int)day)
            .Where(day => day >= 1 && day <= 7)
            .OrderBy(day => day)
            .ToList();
    }
}
