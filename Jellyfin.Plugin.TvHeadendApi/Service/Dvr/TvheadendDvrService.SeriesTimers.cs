using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Dvr;

/// <summary>
/// Series timer operations for <see cref="TvheadendDvrService"/>.
/// </summary>
internal sealed partial class TvheadendDvrService
{
    public async Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        EnsureDvrEnabled();
        ArgumentException.ThrowIfNullOrWhiteSpace(timerId);

        var config = GetConfig();
        var url = _tvheadendUrlBuilder.BuildUrl(config, "api/idnode/delete");
        using var httpClient = _tvheadendApiClient.CreateHttpClient(config);
        using var response = await _tvheadendApiClient.PostFormAsync(
            httpClient,
            url,
            new[] { new KeyValuePair<string, string>("uuid", timerId) },
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Failed to cancel series timer with ID: {timerId}. HTTP Status: {response.StatusCode}. Response: {responseContent}.");
        }
    }

    public async Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        EnsureDvrEnabled();
        ArgumentNullException.ThrowIfNull(info);
        ArgumentException.ThrowIfNullOrWhiteSpace(info.ChannelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(info.Name);

        var config = GetConfig();
        var configUuid = await GetRecordingProfileUuidAsync(config.RecordingProfile, cancellationToken).ConfigureAwait(false);
        string path;
        FormUrlEncodedContent content;

        if (!string.IsNullOrWhiteSpace(info.ProgramId))
        {
            path = "api/dvr/autorec/create_by_series";
            content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("config_uuid", configUuid),
                new KeyValuePair<string, string>("event_id", info.ProgramId),
            });
        }
        else
        {
            path = "api/dvr/autorec/create";
            var seriesTimerJson = new
            {
                channel = info.ChannelId,
                title = info.Name,
                description = info.Overview,
                record_any_time = info.RecordAnyTime,
                record_any_channel = info.RecordAnyChannel,
                record_new_only = info.RecordNewOnly,
                priority = config.Priority,
                start_extra = (int)Math.Round((double)info.PrePaddingSeconds / 60),
                stop_extra = (int)Math.Round((double)info.PostPaddingSeconds / 60),
                weekdays = new List<int> { 1, 2, 3, 4, 5, 6, 7 },
                config_uuid = configUuid,
            };

            content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("conf", JsonSerializer.Serialize(seriesTimerJson, JsonOptions)),
            });
        }

        var url = _tvheadendUrlBuilder.BuildUrl(config, path);
        using var httpClient = _tvheadendApiClient.CreateHttpClient(config);
        using var response = await httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
        var url = _tvheadendUrlBuilder.BuildUrl(config, "api/idnode/save");
        var updates = new Dictionary<string, object>
        {
            { "uuid", info.Id },
            { "channel", info.ChannelId },
            { "record_any_time", info.RecordAnyTime },
            { "record_new_only", info.RecordNewOnly },
            { "start_extra", (int)Math.Round((double)info.PrePaddingSeconds / 60) },
            { "stop_extra", (int)Math.Round((double)info.PostPaddingSeconds / 60) },
        };

        var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("node", JsonSerializer.Serialize(new[] { updates })) });
        using var httpClient = _tvheadendApiClient.CreateHttpClient(config);
        using var response = await httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
            var url = _tvheadendUrlBuilder.BuildUrl(config, "api/dvr/autorec/grid");
            using var httpClient = _tvheadendApiClient.CreateHttpClient(config);
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync<TvhApiDvrAutoRecGridResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return result?.Entries?.Select(entry => new SeriesTimerInfo
            {
                Id = entry.Uuid,
                Name = entry.Name,
                ChannelId = entry.Channel,
                Priority = entry.Priority,
                Overview = entry.Comment,
                Days = entry.Weekdays?.Where(day => day >= 1 && day <= 7).Select(day => (DayOfWeek)(day - 1)).ToList() ?? new List<DayOfWeek>(),
                RecordNewOnly = false,
                StartDate = DateTime.UtcNow,
                EndDate = DateTime.UtcNow.AddHours(1),
                RecordAnyTime = true,
                RecordAnyChannel = false,
            }).ToList() ?? Enumerable.Empty<SeriesTimerInfo>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching series timers from TVHeadEnd.");
            return Enumerable.Empty<SeriesTimerInfo>();
        }
    }
}
