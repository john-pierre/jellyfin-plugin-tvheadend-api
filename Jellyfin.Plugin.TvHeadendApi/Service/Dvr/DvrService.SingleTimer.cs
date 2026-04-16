using System;
using System.Collections.Generic;
using System.Globalization;
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
/// Single timer operations for <see cref="DvrService"/>.
/// </summary>
internal sealed partial class DvrService
{
    public async Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timerId);

        var config = GetConfig();
        var url = _tvheadendUrlBuilder.BuildUrlWithHeaderAuth(config, "api/dvr/entry/cancel");
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
                "TVHeadend timer cancel failed. URL={Url}, RequestPayload={RequestPayload}, Status={Status}, Response={Response}",
                url,
                JsonSerializer.Serialize(requestPayload),
                response.StatusCode,
                responseContent);
            throw new InvalidOperationException($"Failed to cancel timer with ID: {timerId}. HTTP Status: {response.StatusCode}. Response: {responseContent}.");
        }
    }

    public async Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentException.ThrowIfNullOrWhiteSpace(info.ChannelId);

        if (info.StartDate == default || info.EndDate == default || info.StartDate >= info.EndDate)
        {
            throw new ArgumentException("Invalid start or end date for the timer.", nameof(info));
        }

        var config = GetConfig();
        var configUuid = await GetRecordingProfileUuidAsync(config.RecordingProfile, cancellationToken).ConfigureAwait(false);
        string path;
        string requestBodyJson;
        FormUrlEncodedContent content;

        if (!string.IsNullOrWhiteSpace(info.ProgramId))
        {
            path = "api/dvr/entry/create_by_event";
            var pairs = new[]
            {
                new KeyValuePair<string, string>("config_uuid", configUuid),
                new KeyValuePair<string, string>("event_id", info.ProgramId),
            };
            requestBodyJson = JsonSerializer.Serialize(pairs);
            content = new FormUrlEncodedContent(pairs);
        }
        else
        {
            path = "api/dvr/entry/create";
            var timerJson = new
            {
                channel = info.ChannelId,
                start = new DateTimeOffset(info.StartDate).ToUnixTimeSeconds(),
                stop = new DateTimeOffset(info.EndDate).ToUnixTimeSeconds(),
                start_extra = (int)Math.Round((double)info.PrePaddingSeconds / 60),
                stop_extra = (int)Math.Round((double)info.PostPaddingSeconds / 60),
                disp_title = info.Name,
                disp_extratext = info.Overview,
                pri = config.Priority,
                config_name = configUuid,
            };

            requestBodyJson = JsonSerializer.Serialize(timerJson, JsonOptions);
            content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("conf", requestBodyJson),
            });
        }

        var url = _tvheadendUrlBuilder.BuildUrlWithHeaderAuth(config, path);
        _logger.LogDebug(
            "TVHeadend timer create request. URL={Url}, RequestBody={RequestBody}",
            url,
            requestBodyJson);
        using var httpClient = _tvheadendApiClient.BuildHttpClient(config);
        using var response = await httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogError(
                "TVHeadend timer create failed. URL={Url}, RequestBody={RequestBody}, Status={Status}, Response={Response}",
                url,
                requestBodyJson,
                response.StatusCode,
                responseContent);
            throw new InvalidOperationException($"Failed to create timer for program '{info.Name}' on channel ID: '{info.ChannelId}'. HTTP Status: {response.StatusCode}. Response: {responseContent}");
        }
    }

    public async Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updatedTimer);
        ArgumentException.ThrowIfNullOrWhiteSpace(updatedTimer.Id);

        var config = GetConfig();
        var url = _tvheadendUrlBuilder.BuildUrlWithHeaderAuth(config, "api/idnode/save");
        var updates = new Dictionary<string, object>
        {
            { "uuid", updatedTimer.Id },
            { "start_extra", (int)Math.Round((double)updatedTimer.PrePaddingSeconds / 60) },
            { "stop_extra", (int)Math.Round((double)updatedTimer.PostPaddingSeconds / 60) },
        };

        var nodeJson = JsonSerializer.Serialize(new[] { updates });
        var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("node", nodeJson) });
        _logger.LogDebug(
            "TVHeadend timer update request. URL={Url}, RequestBody={RequestBody}",
            url,
            nodeJson);
        using var httpClient = _tvheadendApiClient.BuildHttpClient(config);
        using var response = await httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogError(
                "TVHeadend timer update failed. URL={Url}, RequestBody={RequestBody}, Status={Status}, Response={Response}",
                url,
                nodeJson,
                response.StatusCode,
                responseContent);
            throw new InvalidOperationException($"Failed to update timer with ID: {updatedTimer.Id}. HTTP Status: {response.StatusCode}. Response: {responseContent}");
        }
    }

    public async Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = GetConfig();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var url = _tvheadendUrlBuilder.BuildUrlWithHeaderAuth(config, "api/dvr/entry/grid");
            using var httpClient = _tvheadendApiClient.BuildHttpClient(config);
            var result = await Helper.GridFetcher.FetchAllAsync<DvrEntryGridResponse>(
                httpClient,
                url,
                r => r.Total,
                _logger,
                cancellationToken).ConfigureAwait(false);
            return result?.Entries?
                .Where(entry => entry.Enabled && entry.FileRemoved == 0 && entry.Stop >= now)
                .Select(entry => new TimerInfo
                {
                    Id = entry.Uuid,
                    ProgramId = entry.Broadcast > 0 ? entry.Broadcast.ToString(CultureInfo.InvariantCulture) : null,
                    ChannelId = entry.Channel,
                    Name = string.IsNullOrWhiteSpace(entry.DispTitle) ? entry.ChannelName : entry.DispTitle,
                    Overview = string.IsNullOrWhiteSpace(entry.DispDescription)
                        ? string.IsNullOrWhiteSpace(entry.DispExtraText) ? entry.DispSummary : entry.DispExtraText
                        : entry.DispDescription,
                    StartDate = DateTimeOffset.FromUnixTimeSeconds(entry.Start).UtcDateTime,
                    EndDate = DateTimeOffset.FromUnixTimeSeconds(entry.Stop).UtcDateTime,
                    PrePaddingSeconds = Math.Max(0, entry.StartExtra * 60),
                    PostPaddingSeconds = Math.Max(0, entry.StopExtra * 60),
                    // Link single timer to its parent series timer (autorec rule)
                    SeriesTimerId = string.IsNullOrWhiteSpace(entry.AutoRec) ? null : entry.AutoRec,
                    // TVH sched_status: "scheduled", "recording", "completed", "completedError", "missed", "invalid"
                    Status = MapRecordingStatus(entry.SchedStatus),
                })
                .ToList() ?? Enumerable.Empty<TimerInfo>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching timers from TVHeadEnd.");
            return Enumerable.Empty<TimerInfo>();
        }
    }

    /// <summary>
    /// Maps a TVHeadend <c>sched_status</c> string to a Jellyfin <see cref="RecordingStatus"/>.
    /// TVH values: "scheduled", "recording", "completed", "completedError", "completedWarning", "missed", "invalid".
    /// </summary>
    private static RecordingStatus MapRecordingStatus(string? schedStatus)
    {
        return schedStatus switch
        {
            "recording" => RecordingStatus.InProgress,
            "completed" => RecordingStatus.Completed,
            "completedError" => RecordingStatus.Error,
            "completedWarning" => RecordingStatus.Completed,
            "missed" => RecordingStatus.Error,
            "invalid" => RecordingStatus.Error,
            // "scheduled" or null/empty = new/pending timer
            _ => RecordingStatus.New,
        };
    }
}
