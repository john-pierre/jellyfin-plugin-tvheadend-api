using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Dvr;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Common;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Dvr;

/// <summary>
/// Handles single-timer DVR operations: create, cancel, update, and list timers.
/// </summary>
internal sealed class SingleTimerService
{
    private readonly DvrService _dvr;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SingleTimerService"/> class.
    /// </summary>
    /// <param name="dvr">Parent DVR service providing shared infrastructure.</param>
    /// <param name="logger">Logger instance.</param>
    internal SingleTimerService(DvrService dvr, ILogger logger)
    {
        _dvr = dvr ?? throw new ArgumentNullException(nameof(dvr));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Confirms that a TVHeadend EPG event still describes the same broadcast the timer was
    /// created for. TVHeadend event ids are not stable across grabber runs; a stale id would
    /// record a completely different program. Any doubt (missing event, other channel, shifted
    /// times, transport error) returns <c>false</c> so the caller records by time window instead.
    /// </summary>
    /// <param name="config">Current plugin configuration.</param>
    /// <param name="info">The timer being created.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when the event matches channel and requested time window.</returns>
    private async Task<bool> EventStillMatchesTimerAsync(PluginConfiguration config, TimerInfo info, CancellationToken cancellationToken)
    {
        try
        {
            var url = _dvr.UrlBuilder.BuildApiUrl(config, "api/epg/events/load");
            using var httpClient = _dvr.ApiClient.CreateApiHttpClient(config);
            using var response = await _dvr.ApiClient.PostFormAsync(
                httpClient,
                url,
                new[] { new KeyValuePair<string, string>("eventId", info.ProgramId!) },
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("entries", out var entries)
                || entries.ValueKind != JsonValueKind.Array
                || entries.GetArrayLength() == 0)
            {
                return false;
            }

            var evt = entries[0];
            var channel = evt.TryGetProperty("channelUuid", out var channelElement) && channelElement.ValueKind == JsonValueKind.String
                ? channelElement.GetString()
                : null;
            if (!string.Equals(channel, info.ChannelId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!evt.TryGetProperty("start", out var startElement) || !evt.TryGetProperty("stop", out var stopElement))
            {
                return false;
            }

            // create_by_event ignores the requested start/stop entirely — the event's own times
            // win. A drifted event OR a user-customized window must go through the time-based path.
            var tolerance = TimeSpan.FromSeconds(120);
            var eventStart = DateTimeOffset.FromUnixTimeSeconds(startElement.GetInt64());
            var eventStop = DateTimeOffset.FromUnixTimeSeconds(stopElement.GetInt64());
            return (eventStart - new DateTimeOffset(info.StartDate)).Duration() <= tolerance
                && (eventStop - new DateTimeOffset(info.EndDate)).Duration() <= tolerance;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "EPG event verification failed for event {EventId}; falling back to a time-based recording entry.", info.ProgramId);
            return false;
        }
    }

    public async Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timerId);

        var config = _dvr.GetConfig();
        var url = _dvr.UrlBuilder.BuildApiUrl(config, "api/dvr/entry/cancel");
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

        var config = _dvr.GetConfig();
        var configUuid = await _dvr.GetRecordingProfileUuidAsync(config.RecordingProfile, cancellationToken).ConfigureAwait(false);
        string path;
        string requestBodyJson;
        IEnumerable<KeyValuePair<string, string>> formValues;

        // TVHeadend renumbers EPG event ids on every grabber run, so the event id cached in
        // Jellyfin's guide may silently point at a DIFFERENT broadcast by now. Trust it only
        // after re-validating channel and time window against TVHeadend's current EPG.
        var useEventCreation = false;
        if (!string.IsNullOrWhiteSpace(info.ProgramId))
        {
            useEventCreation = await EventStillMatchesTimerAsync(config, info, cancellationToken).ConfigureAwait(false);
            if (!useEventCreation)
            {
                _logger.LogWarning(
                    "EPG event {EventId} no longer matches the requested timer on channel {ChannelId} — creating a time-based recording entry instead.",
                    info.ProgramId,
                    info.ChannelId);
            }
        }

        if (useEventCreation)
        {
            path = "api/dvr/entry/create_by_event";
            var pairs = new List<KeyValuePair<string, string>>();

            // Omit the profile when the configured name matched nothing so TVHeadend applies its default.
            if (configUuid is not null)
            {
                pairs.Add(new KeyValuePair<string, string>("config_uuid", configUuid));
            }

            pairs.Add(new KeyValuePair<string, string>("event_id", info.ProgramId));
            requestBodyJson = JsonSerializer.Serialize(pairs);
            formValues = pairs;
        }
        else
        {
            path = "api/dvr/entry/create";
            var timerJson = new Dictionary<string, object?>
            {
                ["channel"] = info.ChannelId,
                ["start"] = new DateTimeOffset(info.StartDate).ToUnixTimeSeconds(),
                ["stop"] = new DateTimeOffset(info.EndDate).ToUnixTimeSeconds(),
                ["start_extra"] = (int)Math.Round((double)info.PrePaddingSeconds / 60),
                ["stop_extra"] = (int)Math.Round((double)info.PostPaddingSeconds / 60),
                ["disp_title"] = info.Name,
                ["disp_extratext"] = info.Overview,
                ["pri"] = config.Priority,
            };

            // Omit the profile when the configured name matched nothing so TVHeadend applies its default.
            if (configUuid is not null)
            {
                timerJson["config_name"] = configUuid;
            }

            requestBodyJson = JsonSerializer.Serialize(timerJson, JsonDefaults.Api);
            formValues = new[] { new KeyValuePair<string, string>("conf", requestBodyJson) };
        }

        var url = _dvr.UrlBuilder.BuildApiUrl(config, path);
        _logger.LogDebug(
            "TVHeadend timer create request. URL={Url}, RequestBody={RequestBody}",
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
                "TVHeadend timer create failed. URL={Url}, RequestBody={RequestBody}, Status={Status}, Response={Response}",
                url,
                requestBodyJson,
                response.StatusCode,
                responseContent);
            throw new InvalidOperationException($"Failed to create timer for program '{info.Name}' on channel ID: '{info.ChannelId}'. HTTP Status: {response.StatusCode}. Response: {responseContent}");
        }

        var createResponse = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var createdId = DvrService.TryExtractCreatedEntityId(createResponse);
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

    public async Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updatedTimer);
        ArgumentException.ThrowIfNullOrWhiteSpace(updatedTimer.Id);

        var config = _dvr.GetConfig();
        var url = _dvr.UrlBuilder.BuildApiUrl(config, "api/idnode/save");
        var updates = new Dictionary<string, object>
        {
            { "uuid", updatedTimer.Id },
            { "start_extra", (int)Math.Round((double)updatedTimer.PrePaddingSeconds / 60) },
            { "stop_extra", (int)Math.Round((double)updatedTimer.PostPaddingSeconds / 60) },
            // Always send the priority: 0 is a legitimate TVHeadend priority ("Important"),
            // not an "unset" sentinel, and GetTimersAsync round-trips the current value.
            { "pri", updatedTimer.Priority },
        };

        if (updatedTimer.StartDate != default)
        {
            updates["start"] = new DateTimeOffset(updatedTimer.StartDate).ToUnixTimeSeconds();
        }

        if (updatedTimer.EndDate != default)
        {
            updates["stop"] = new DateTimeOffset(updatedTimer.EndDate).ToUnixTimeSeconds();
        }

        if (!string.IsNullOrWhiteSpace(updatedTimer.ChannelId))
        {
            updates["channel"] = updatedTimer.ChannelId;
        }

        if (!string.IsNullOrWhiteSpace(updatedTimer.Name))
        {
            updates["disp_title"] = updatedTimer.Name;
        }

        if (!string.IsNullOrWhiteSpace(updatedTimer.Overview))
        {
            updates["disp_extratext"] = updatedTimer.Overview;
        }

        var nodeJson = JsonSerializer.Serialize(new[] { updates });
        var formValues = new[] { new KeyValuePair<string, string>("node", nodeJson) };
        _logger.LogDebug(
            "TVHeadend timer update request. URL={Url}, RequestBody={RequestBody}",
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
            var config = _dvr.GetConfig();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var url = _dvr.UrlBuilder.BuildApiUrl(config, "api/dvr/entry/grid");
            using var httpClient = _dvr.ApiClient.CreateApiHttpClient(config);
            var result = await GridFetcher.FetchAllAsync<DvrEntryGridResponse>(
                httpClient,
                url,
                r => r.Total,
                _logger,
                cancellationToken).ConfigureAwait(false);
            return result?.Entries?
                .Where(entry => ShouldIncludeEntry(entry, now))
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
                    Priority = entry.Priority,
                    // Link single timer to its parent recurring rule (EPG-based autorec or time-based timerec)
                    SeriesTimerId = ResolveSeriesTimerId(entry),
                    // Recorded file location on TVHeadend storage; empty until the recording has started
                    RecordingPath = string.IsNullOrWhiteSpace(entry.Filename) ? null : entry.Filename,
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
    /// Determines whether a DVR entry should be surfaced to Jellyfin. Upcoming and active
    /// entries are always included. Past entries are kept only while they represent a
    /// recording that actually ran ("recording", "completed", "completedWarning",
    /// "completedError"), so finished recordings keep reporting their final status instead
    /// of vanishing the moment the scheduled stop time passes; TVHeadend ages them out via
    /// <c>fileremoved</c>/retention. Past "missed"/"invalid" entries never produced a file
    /// and are dropped to avoid unbounded accumulation.
    /// </summary>
    /// <param name="entry">The DVR grid entry to evaluate.</param>
    /// <param name="now">The current time as a Unix timestamp in seconds.</param>
    /// <returns><c>true</c> when the entry should be mapped to a <see cref="TimerInfo"/>.</returns>
    private static bool ShouldIncludeEntry(DvrEntryGridEntry entry, long now)
    {
        if (!entry.Enabled || entry.FileRemoved != 0)
        {
            return false;
        }

        if (entry.Stop >= now)
        {
            return true;
        }

        return entry.SchedStatus is "recording" or "completed" or "completedWarning" or "completedError";
    }

    /// <summary>
    /// Resolves the parent recurring-rule ID for a DVR entry. TVHeadend spawns child entries
    /// from EPG-based <c>autorec</c> rules and from time-based <c>timerec</c> rules; either
    /// one links the timer to its series.
    /// </summary>
    /// <param name="entry">The DVR grid entry to inspect.</param>
    /// <returns>The parent rule UUID, or <c>null</c> for one-off timers.</returns>
    private static string? ResolveSeriesTimerId(DvrEntryGridEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.AutoRec))
        {
            return entry.AutoRec;
        }

        return string.IsNullOrWhiteSpace(entry.TimeRec) ? null : entry.TimeRec;
    }

    /// <summary>
    /// Maps a TVHeadend <c>sched_status</c> string to a Jellyfin <see cref="RecordingStatus"/>.
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
            _ => RecordingStatus.New,
        };
    }
}
