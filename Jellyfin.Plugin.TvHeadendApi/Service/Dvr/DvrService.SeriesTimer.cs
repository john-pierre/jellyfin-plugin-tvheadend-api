using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Dvr;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Dvr;

/// <summary>
/// Series timer operations for <see cref="DvrService"/>.
/// </summary>
internal sealed partial class DvrService
{
    public async Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        EnsureDvrEnabled();
        ArgumentException.ThrowIfNullOrWhiteSpace(timerId);

        var config = GetConfig();
        var url = _tvheadendUrlBuilder.BuildUrlWithHeaderAuth(config, "api/idnode/delete");
        var requestPayload = new[] { new KeyValuePair<string, string>("uuid", timerId) };
        using var httpClient = _tvheadendApiClient.BuildHttpClient(config);
        using var response = await _tvheadendApiClient.PostFormAsync(
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
        EnsureDvrEnabled();
        ArgumentNullException.ThrowIfNull(info);
        ArgumentException.ThrowIfNullOrWhiteSpace(info.Name);

        // ChannelId is required only when RecordAnyChannel is false
        if (!info.RecordAnyChannel)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(info.ChannelId);
        }

        var config = GetConfig();
        var configUuid = await GetRecordingProfileUuidAsync(config.RecordingProfile, cancellationToken).ConfigureAwait(false);
        string path;
        string requestBodyJson;
        FormUrlEncodedContent content;

        if (!string.IsNullOrWhiteSpace(info.ProgramId))
        {
            path = "api/dvr/autorec/create_by_series";
            var pairs = new[]
            {
                // TVH api_dvr_entry_create_from_single requires "config_uuid", not "config_name"
                new KeyValuePair<string, string>("config_uuid", configUuid),
                new KeyValuePair<string, string>("event_id", info.ProgramId),
            };
            requestBodyJson = JsonSerializer.Serialize(pairs);
            content = new FormUrlEncodedContent(pairs);
        }
        else
        {
            path = "api/dvr/autorec/create";
            var seriesTimerJson = new
            {
                // Empty channel = match any channel; UUID = specific channel
                channel = info.RecordAnyChannel ? null : info.ChannelId,
                title = info.Name,
                // TVH autorec has no "description" idnode; "comment" is the user-note field
                comment = info.Overview,
                // record: 0=all (DVR_AUTOREC_RECORD_ALL), 1=different episode number (new-only)
                record = info.RecordNewOnly ? 1 : 0,
                // start/start_window: "Any" = any time; absent/null = keep TVH default
                start = info.RecordAnyTime ? "Any" : null,
                start_window = info.RecordAnyTime ? "Any" : null,
                pri = config.Priority,
                start_extra = (int)Math.Round((double)info.PrePaddingSeconds / 60),
                stop_extra = (int)Math.Round((double)info.PostPaddingSeconds / 60),
                weekdays = BuildWeekdaysPayload(info.Days),
                // TVH autorec config field is named "config_name" (stores the DVR config UUID)
                config_name = configUuid,
            };

            requestBodyJson = JsonSerializer.Serialize(seriesTimerJson, JsonOptions);
            content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("conf", requestBodyJson),
            });
        }

        var url = _tvheadendUrlBuilder.BuildUrlWithHeaderAuth(config, path);
        _logger.LogDebug(
            "TVHeadend series timer create request. URL={Url}, RequestBody={RequestBody}",
            url,
            requestBodyJson);
        using var httpClient = _tvheadendApiClient.BuildHttpClient(config);
        using var response = await httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
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
        var createdId = TryExtractCreatedEntityId(createResponse);
        if (!string.IsNullOrWhiteSpace(createdId))
        {
            info.Id = createdId;
        }
        else if (string.IsNullOrWhiteSpace(info.Id))
        {
            // Keep downstream socket payloads valid even when TVHeadend omits an ID in create responses.
            info.Id = Guid.NewGuid().ToString("N");
        }
    }

    private static string? TryExtractCreatedEntityId(string responseBody)
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

    public async Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        EnsureDvrEnabled();
        ArgumentNullException.ThrowIfNull(info);
        ArgumentException.ThrowIfNullOrWhiteSpace(info.Id);

        var config = GetConfig();
        var url = _tvheadendUrlBuilder.BuildUrlWithHeaderAuth(config, "api/idnode/save");
        var updates = new Dictionary<string, object?>
        {
            { "uuid", info.Id },
            // record: 0=DVR_AUTOREC_RECORD_ALL (record everything), 1=different episode number (new-only)
            { "record", info.RecordNewOnly ? 1 : 0 },
            // start/start_window: "Any" matches any time; null keeps existing TVH value
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
            // TVH autorec has no "description" idnode field; "comment" is the correct field
            updates["comment"] = info.Overview;
        }

        var nodeJson = JsonSerializer.Serialize(new[] { updates }, JsonOptions);
        var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("node", nodeJson) });
        _logger.LogDebug(
            "TVHeadend series timer update request. URL={Url}, RequestBody={RequestBody}",
            url,
            nodeJson);
        using var httpClient = _tvheadendApiClient.BuildHttpClient(config);
        using var response = await httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
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
        if (!IsTvhDvrEnabled())
        {
            return Task.FromResult(new SeriesTimerInfo());
        }

        var config = GetConfig();
        return Task.FromResult(new SeriesTimerInfo
        {
            Id = Guid.NewGuid().ToString(),
            ChannelId = program?.ChannelId,
            Name = program?.Name,
            Overview = program?.Overview,
            RecordNewOnly = true,
            RecordAnyTime = true,
            RecordAnyChannel = false,
            Priority = config.Priority,
            PrePaddingSeconds = config.PrePaddingSeconds,
            PostPaddingSeconds = config.PostPaddingSeconds,
            KeepUntil = KeepUntil.UntilDeleted,
            Days = Enum.GetValues(typeof(DayOfWeek)).Cast<DayOfWeek>().ToList(),
        });
    }

    public async Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
    {
        if (!IsTvhDvrEnabled())
        {
            return Enumerable.Empty<SeriesTimerInfo>();
        }

        try
        {
            var config = GetConfig();
            var url = _tvheadendUrlBuilder.BuildUrlWithHeaderAuth(config, "api/dvr/autorec/grid");
            using var httpClient = _tvheadendApiClient.BuildHttpClient(config);
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync<DvrAutoRecGridResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
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
                // 0=DVR_AUTOREC_RECORD_ALL (record everything), 15=DVR_AUTOREC_RECORD_DVR_PROFILE (use config); both mean "no dedup"
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
