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
        EnsureDvrEnabled();
        ArgumentException.ThrowIfNullOrWhiteSpace(timerId);

        var config = GetConfig();
        var url = _tvheadendUrlBuilder.BuildUrlWithHeaderAuth(config, "api/dvr/entry/cancel");
        using var httpClient = _tvheadendApiClient.BuildHttpClient(config);
        using var response = await _tvheadendApiClient.PostFormAsync(
            httpClient,
            url,
            new[] { new KeyValuePair<string, string>("uuid", timerId) },
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Failed to cancel timer with ID: {timerId}. HTTP Status: {response.StatusCode}. Response: {responseContent}.");
        }
    }

    public async Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
    {
        EnsureDvrEnabled();
        ArgumentNullException.ThrowIfNull(info);
        ArgumentException.ThrowIfNullOrWhiteSpace(info.ChannelId);

        if (info.StartDate == default || info.EndDate == default || info.StartDate >= info.EndDate)
        {
            throw new ArgumentException("Invalid start or end date for the timer.", nameof(info));
        }

        var config = GetConfig();
        var configUuid = await GetRecordingProfileUuidAsync(config.RecordingProfile, cancellationToken).ConfigureAwait(false);
        string path;
        FormUrlEncodedContent content;

        if (!string.IsNullOrWhiteSpace(info.ProgramId))
        {
            path = "api/dvr/entry/create_by_event";
            content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("config_uuid", configUuid),
                new KeyValuePair<string, string>("event_id", info.ProgramId),
            });
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

            content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("conf", JsonSerializer.Serialize(timerJson, JsonOptions)),
            });
        }

        var url = _tvheadendUrlBuilder.BuildUrlWithHeaderAuth(config, path);
        using var httpClient = _tvheadendApiClient.BuildHttpClient(config);
        using var response = await httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Failed to create timer for program '{info.Name}' on channel ID: '{info.ChannelId}'. HTTP Status: {response.StatusCode}. Response: {responseContent}");
        }
    }

    public async Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken)
    {
        EnsureDvrEnabled();
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

        var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("node", JsonSerializer.Serialize(new[] { updates })) });
        using var httpClient = _tvheadendApiClient.BuildHttpClient(config);
        using var response = await httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Failed to update timer with ID: {updatedTimer.Id}. HTTP Status: {response.StatusCode}. Response: {responseContent}");
        }
    }

    public async Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
    {
        if (!IsTvhDvrEnabled())
        {
            return Enumerable.Empty<TimerInfo>();
        }

        try
        {
            var config = GetConfig();
            const int limit = 10000;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var url = _tvheadendUrlBuilder.BuildUrlWithHeaderAuth(config, $"api/dvr/entry/grid?limit={limit}");
            using var httpClient = _tvheadendApiClient.BuildHttpClient(config);
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync<DvrEntryGridResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
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
                })
                .ToList() ?? Enumerable.Empty<TimerInfo>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while fetching timers from TVHeadEnd.");
            return Enumerable.Empty<TimerInfo>();
        }
    }
}
