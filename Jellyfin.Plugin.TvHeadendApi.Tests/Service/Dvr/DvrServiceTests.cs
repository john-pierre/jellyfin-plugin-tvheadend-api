using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Dvr;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class DvrServiceTests
{
    [Fact]
    public void Constructor_WithNullLogger_Throws()
    {
        var apiClient = new Mock<IApiClient>();
        var urlBuilder = new Mock<IUrlBuilder>();

        Assert.Throws<ArgumentNullException>(() => new DvrService(null!, apiClient.Object, urlBuilder.Object));
    }

    [Fact]
    public void Constructor_WithNullApiClient_Throws()
    {
        var urlBuilder = new Mock<IUrlBuilder>();

        Assert.Throws<ArgumentNullException>(() => new DvrService(NullLogger<DvrService>.Instance, null!, urlBuilder.Object));
    }

    [Fact]
    public void Constructor_WithNullUrlBuilder_Throws()
    {
        var apiClient = new Mock<IApiClient>();

        Assert.Throws<ArgumentNullException>(() => new DvrService(NullLogger<DvrService>.Instance, apiClient.Object, null!));
    }

    [Fact]
    public async Task GetTimersAsync_MapsAndFiltersEntries()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var responseJson = $$"""
                             {
                               "entries": [
                                 {
                                   "uuid": "timer-1",
                                   "broadcast": 0,
                                   "channel": "ch-1",
                                   "channelname": "Channel One",
                                   "disp_title": "",
                                   "disp_description": "",
                                   "disp_extratext": "Extra text",
                                   "disp_summary": "Summary text",
                                   "start": {{now - 60}},
                                   "stop": {{now + 3600}},
                                   "start_extra": -5,
                                   "stop_extra": 2,
                                   "enabled": true,
                                   "fileremoved": 0
                                 },
                                 {
                                   "uuid": "timer-2",
                                   "broadcast": 123,
                                   "channel": "ch-2",
                                   "channelname": "Channel Two",
                                   "disp_title": "Program Two",
                                   "disp_description": "Description Two",
                                   "start": {{now - 120}},
                                   "stop": {{now + 1800}},
                                   "start_extra": 1,
                                   "stop_extra": 3,
                                   "enabled": true,
                                   "fileremoved": 0
                                 },
                                 {
                                   "uuid": "disabled",
                                   "start": {{now}},
                                   "stop": {{now + 100}},
                                   "enabled": false,
                                   "fileremoved": 0
                                 },
                                 {
                                   "uuid": "removed",
                                   "start": {{now}},
                                   "stop": {{now + 100}},
                                   "enabled": true,
                                   "fileremoved": 1
                                 },
                                 {
                                   "uuid": "past",
                                   "start": {{now - 200}},
                                   "stop": {{now - 100}},
                                   "enabled": true,
                                   "fileremoved": 0
                                 }
                               ]
                             }
                             """;

        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, responseJson);

        var sut = CreateSut(handler, out _, out _, CreateConfig());

        var result = (await sut.GetTimersAsync(CancellationToken.None)).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal("timer-1", result[0].Id);
        Assert.Null(result[0].ProgramId);
        Assert.Equal("Channel One", result[0].Name);
        Assert.Equal("Extra text", result[0].Overview);
        Assert.Equal(0, result[0].PrePaddingSeconds);
        Assert.Equal(120, result[0].PostPaddingSeconds);

        Assert.Equal("timer-2", result[1].Id);
        Assert.Equal("123", result[1].ProgramId);
        Assert.Equal("Program Two", result[1].Name);
        Assert.Equal("Description Two", result[1].Overview);
    }

    [Fact]
    public async Task GetTimersAsync_WhenHttpNonSuccess_ReturnsEmpty()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.BadGateway, "backend down");
        var sut = CreateSut(handler, out _, out _, CreateConfig());

        var result = await sut.GetTimersAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTimersAsync_WhenInvalidJson_ReturnsEmpty()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "{ invalid json");
        var sut = CreateSut(handler, out _, out _, CreateConfig());

        var result = await sut.GetTimersAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTimersAsync_WhenConfigurationMissing_ReturnsEmpty()
    {
        var handler = new QueueHttpMessageHandler();
        var sut = CreateSut(handler, out _, out var apiClient, null);

        var result = await sut.GetTimersAsync(CancellationToken.None);

        Assert.Empty(result);
        apiClient.Verify(x => x.GetCurrentConfiguration(), Times.Once);
    }

    [Fact]
    public async Task CreateTimerAsync_WithProgramId_UsesCreateByEventEndpoint()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """
                                         { "entries": [ { "name": "default", "uuid": "profile-uuid" } ] }
                                         """);
        handler.Enqueue(HttpStatusCode.OK, "{}");

        var sut = CreateSut(handler, out _, out _, CreateConfig());
        var timer = new TimerInfo
        {
            ChannelId = "ch-1",
            ProgramId = "456",
            Name = "News",
            StartDate = DateTime.UtcNow.AddMinutes(10),
            EndDate = DateTime.UtcNow.AddMinutes(40)
        };

        await sut.CreateTimerAsync(timer, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("api/dvr/config/grid", handler.Requests[0].Url);
        Assert.Contains("api/dvr/entry/create_by_event", handler.Requests[1].Url);
        Assert.Contains("config_uuid=profile-uuid", handler.Requests[1].Body);
        Assert.Contains("event_id=456", handler.Requests[1].Body);
    }

    [Fact]
    public async Task CreateTimerAsync_WithoutProgramId_UsesCreateEndpointWithConfPayload()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """
                                         { "entries": [ { "name": "default", "uuid": "profile-uuid" } ] }
                                         """);
        handler.Enqueue(HttpStatusCode.OK, "{}");

        var sut = CreateSut(handler, out _, out _, CreateConfig(priority: 6));
        var start = DateTime.UtcNow.AddMinutes(30);
        var stop = start.AddMinutes(25);
        var timer = new TimerInfo
        {
            ChannelId = "ch-9",
            Name = "My Timer",
            Overview = "Overview",
            StartDate = start,
            EndDate = stop,
            PrePaddingSeconds = 120,
            PostPaddingSeconds = 300
        };

        await sut.CreateTimerAsync(timer, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("api/dvr/entry/create", handler.Requests[1].Url);
        Assert.Contains("conf=", handler.Requests[1].Body);
        Assert.Contains("%22channel%22%3A%22ch-9%22", handler.Requests[1].Body);
        Assert.Contains("%22start_extra%22%3A2", handler.Requests[1].Body);
        Assert.Contains("%22stop_extra%22%3A5", handler.Requests[1].Body);
        Assert.Contains("%22config_name%22%3A%22profile-uuid%22", handler.Requests[1].Body);
    }

    [Fact]
    public async Task CreateTimerAsync_WhenCreateRequestFails_ThrowsInvalidOperationException()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """
                                         { "entries": [ { "name": "default", "uuid": "profile-uuid" } ] }
                                         """);
        handler.Enqueue(HttpStatusCode.BadRequest, "bad create");
        var sut = CreateSut(handler, out _, out _, CreateConfig());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.CreateTimerAsync(
                new TimerInfo
                {
                    ChannelId = "ch-1",
                    Name = "Timer",
                    StartDate = DateTime.UtcNow.AddMinutes(10),
                    EndDate = DateTime.UtcNow.AddMinutes(15)
                },
                CancellationToken.None));

        Assert.Contains("Failed to create timer", ex.Message);
    }

    [Fact]
    public async Task CreateTimerAsync_WithInvalidDates_ThrowsArgumentException()
    {
        var sut = CreateSut(new QueueHttpMessageHandler(), out _, out _, CreateConfig());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.CreateTimerAsync(
                new TimerInfo
                {
                    ChannelId = "ch-1",
                    Name = "Invalid",
                    StartDate = default,
                    EndDate = default
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task UpdateTimerAsync_PostsIdNodeSave()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "{}");
        var sut = CreateSut(handler, out _, out _, CreateConfig());

        await sut.UpdateTimerAsync(new TimerInfo { Id = "timer-11", PrePaddingSeconds = 120, PostPaddingSeconds = 180 }, CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Contains("api/idnode/save", handler.Requests[0].Url);
        Assert.Contains("node=", handler.Requests[0].Body);
        Assert.Contains("%22uuid%22%3A%22timer-11%22", handler.Requests[0].Body);
        Assert.Contains("%22start_extra%22%3A2", handler.Requests[0].Body);
        Assert.Contains("%22stop_extra%22%3A3", handler.Requests[0].Body);
    }

    [Fact]
    public async Task CancelTimerAsync_PostsUuidFormViaApiClient()
    {
        var sut = CreateSut(new QueueHttpMessageHandler(), out _, out var apiClient, CreateConfig());
        IEnumerable<KeyValuePair<string, string>>? postedValues = null;

        apiClient
            .Setup(x => x.PostFormAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>(), It.IsAny<CancellationToken>()))
            .Callback<HttpClient, string, IEnumerable<KeyValuePair<string, string>>, CancellationToken>((_, _, values, _) => postedValues = values)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        await sut.CancelTimerAsync("timer-99", CancellationToken.None);

        Assert.NotNull(postedValues);
        Assert.Contains(postedValues!, pair => pair.Key == "uuid" && pair.Value == "timer-99");
    }

    [Fact]
    public async Task CancelTimerAsync_WhenHttpNonSuccess_ThrowsInvalidOperationException()
    {
        var sut = CreateSut(new QueueHttpMessageHandler(), out _, out var apiClient, CreateConfig());
        apiClient
            .Setup(x => x.PostFormAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("cancel failed") });

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.CancelTimerAsync("timer-77", CancellationToken.None));
    }

    [Fact]
    public async Task GetSeriesTimersAsync_MapsWeekdaysAndFields()
    {
        var responseJson = """
                           {
                             "entries": [
                               {
                                 "uuid": "series-1",
                                 "name": "Show A",
                                 "channel": "ch-1",
                                 "pri": 4,
                                 "comment": "Auto rule",
                                 "weekdays": [0, 1, 3, 7, 8]
                               }
                             ]
                           }
                           """;

        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, responseJson);
        var sut = CreateSut(handler, out _, out _, CreateConfig());

        var result = (await sut.GetSeriesTimersAsync(CancellationToken.None)).ToList();

        Assert.Single(result);
        var series = result[0];
        Assert.Equal("series-1", series.Id);
        Assert.Equal("Show A", series.Name);
        Assert.Equal("ch-1", series.ChannelId);
        Assert.Equal(4, series.Priority);
        Assert.Equal("Auto rule", series.Overview);
        Assert.Equal(3, series.Days.Count);
        Assert.Contains(DayOfWeek.Monday, series.Days);
        Assert.Contains(DayOfWeek.Wednesday, series.Days);
        Assert.Contains(DayOfWeek.Sunday, series.Days);
    }

    [Fact]
    public async Task GetSeriesTimersAsync_MapsPaddingAndFlags()
    {
        var responseJson = """
                           {
                             "entries": [
                               {
                                 "uuid": "series-p1",
                                 "name": "Padded Show",
                                 "channel": "",
                                 "pri": 3,
                                 "comment": "",
                                 "start": "Any",
                                 "start_extra": 2,
                                 "stop_extra": 5,
                                 "record": 1,
                                 "weekdays": [1, 2, 3, 4, 5]
                               }
                             ]
                           }
                           """;

        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, responseJson);
        var sut = CreateSut(handler, out _, out _, CreateConfig());

        var result = (await sut.GetSeriesTimersAsync(CancellationToken.None)).ToList();

        Assert.Single(result);
        var series = result[0];
        Assert.Equal(120, series.PrePaddingSeconds);
        Assert.Equal(300, series.PostPaddingSeconds);
        Assert.True(series.RecordNewOnly);
        Assert.True(series.RecordAnyTime);
        Assert.True(series.RecordAnyChannel);
        Assert.Null(series.ChannelId);
    }

    [Fact]
    public async Task GetSeriesTimersAsync_WithSpecificChannelAndNoRecord_MapsCorrectly()
    {
        var responseJson = """
                           {
                             "entries": [
                               {
                                 "uuid": "series-p2",
                                 "name": "Fixed Show",
                                 "channel": "ch-99",
                                 "pri": 2,
                                 "comment": "",
                                 "start": "20:00",
                                 "start_extra": 0,
                                 "stop_extra": 0,
                                 "record": 0,
                                 "weekdays": []
                               }
                             ]
                           }
                           """;

        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, responseJson);
        var sut = CreateSut(handler, out _, out _, CreateConfig());

        var result = (await sut.GetSeriesTimersAsync(CancellationToken.None)).ToList();

        Assert.Single(result);
        var series = result[0];
        Assert.Equal("ch-99", series.ChannelId);
        Assert.False(series.RecordNewOnly);
        Assert.False(series.RecordAnyTime);
        Assert.False(series.RecordAnyChannel);
        Assert.Equal(0, series.PrePaddingSeconds);
        Assert.Equal(0, series.PostPaddingSeconds);
    }

    [Fact]
    public async Task GetSeriesTimersAsync_WhenHttpNonSuccess_ReturnsEmpty()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.NotFound, "not found");
        var sut = CreateSut(handler, out _, out _, CreateConfig());

        var result = await sut.GetSeriesTimersAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetSeriesTimersAsync_WhenInvalidJson_ReturnsEmpty()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "{ invalid json");
        var sut = CreateSut(handler, out _, out _, CreateConfig());

        var result = await sut.GetSeriesTimersAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetSeriesTimersAsync_WhenConfigurationMissing_ReturnsEmpty()
    {
        var handler = new QueueHttpMessageHandler();
        var sut = CreateSut(handler, out _, out var apiClient, null);

        var result = await sut.GetSeriesTimersAsync(CancellationToken.None);

        Assert.Empty(result);
        apiClient.Verify(x => x.GetCurrentConfiguration(), Times.Once);
    }

    [Fact]
    public async Task CreateSeriesTimerAsync_WithProgramId_UsesCreateBySeriesEndpoint()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """
                                         { "entries": [ { "name": "default", "uuid": "profile-uuid" } ] }
                                         """);
        handler.Enqueue(HttpStatusCode.OK, """
                                         { "uuid": "series-created-1" }
                                         """);

        var sut = CreateSut(handler, out _, out _, CreateConfig());
        var info = new SeriesTimerInfo
        {
            Name = "Series Name",
            ChannelId = "ch-2",
            ProgramId = "789"
        };

        await sut.CreateSeriesTimerAsync(info, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("api/dvr/autorec/create_by_series", handler.Requests[1].Url);
        Assert.Contains("config_uuid=profile-uuid", handler.Requests[1].Body);
        Assert.Contains("event_id=789", handler.Requests[1].Body);
        Assert.DoesNotContain("config_name=", handler.Requests[1].Body);
        Assert.Equal("series-created-1", info.Id);
    }

    [Fact]
    public async Task CreateSeriesTimerAsync_WithoutProgramId_UsesCreateEndpointWithConfPayload()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """
                                         { "entries": [ { "name": "default", "uuid": "profile-uuid" } ] }
                                         """);
        handler.Enqueue(HttpStatusCode.OK, """
                                         { "entries": [ { "uuid": "series-created-2" } ] }
                                         """);

        var sut = CreateSut(handler, out _, out _, CreateConfig(priority: 7));
        var info = new SeriesTimerInfo
        {
            Name = "Series Name",
            ChannelId = "ch-5",
            Overview = "Series overview",
            RecordAnyTime = true,
            RecordAnyChannel = false,
            RecordNewOnly = true,
            PrePaddingSeconds = 180,
            PostPaddingSeconds = 240
        };

        await sut.CreateSeriesTimerAsync(info, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("api/dvr/autorec/create", handler.Requests[1].Url);
        Assert.Contains("conf=", handler.Requests[1].Body);
        Assert.Contains("%22channel%22%3A%22ch-5%22", handler.Requests[1].Body);
        Assert.Contains("%22title%22%3A%22Series+Name%22", handler.Requests[1].Body);
        Assert.Contains("%22start_extra%22%3A3", handler.Requests[1].Body);
        Assert.Contains("%22stop_extra%22%3A4", handler.Requests[1].Body);
        Assert.Contains("%22config_name%22%3A%22profile-uuid%22", handler.Requests[1].Body);
        Assert.Equal("series-created-2", info.Id);
    }

    [Fact]
    public async Task CreateSeriesTimerAsync_WithSpecificDays_UsesMappedWeekdaysPayload()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """
                                         { "entries": [ { "name": "default", "uuid": "profile-uuid" } ] }
                                         """);
        handler.Enqueue(HttpStatusCode.OK, """
                                         { "uuid": "series-created-3" }
                                         """);

        var sut = CreateSut(handler, out _, out _, CreateConfig(priority: 7));
        var info = new SeriesTimerInfo
        {
            Name = "Series Name",
            ChannelId = "ch-5",
            Days = new List<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Friday }
        };

        await sut.CreateSeriesTimerAsync(info, CancellationToken.None);

        Assert.Contains("%22weekdays%22%3A%5B1%2C5%5D", handler.Requests[1].Body);
        Assert.Contains("%22pri%22%3A7", handler.Requests[1].Body);
    }

    [Fact]
    public async Task CreateSeriesTimerAsync_WhenCreateResponseHasNoId_AssignsFallbackId()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """
                                         { "entries": [ { "name": "default", "uuid": "profile-uuid" } ] }
                                         """);
        handler.Enqueue(HttpStatusCode.OK, "{}");

        var sut = CreateSut(handler, out _, out _, CreateConfig());
        var info = new SeriesTimerInfo
        {
            Name = "Series Name",
            ChannelId = "ch-4"
        };

        await sut.CreateSeriesTimerAsync(info, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(info.Id));
        Assert.True(Guid.TryParse(info.Id, out _));
    }

    [Fact]
    public async Task CreateSeriesTimerAsync_WhenRecordingProfileMissing_ThrowsInvalidOperationException()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """
                                         { "entries": [ { "name": "other", "uuid": "x" } ] }
                                         """);
        var sut = CreateSut(handler, out _, out _, CreateConfig(recordingProfile: "default"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.CreateSeriesTimerAsync(new SeriesTimerInfo { Name = "Series", ChannelId = "ch-1" }, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateSeriesTimerAsync_PostsIdNodeSave()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "{}");
        var sut = CreateSut(handler, out _, out _, CreateConfig());

        await sut.UpdateSeriesTimerAsync(
            new SeriesTimerInfo
            {
                Id = "series-44",
                ChannelId = "ch-10",
                RecordAnyTime = false,
                RecordNewOnly = true,
                PrePaddingSeconds = 60,
                PostPaddingSeconds = 120
            },
            CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Contains("api/idnode/save", handler.Requests[0].Url);
        Assert.Contains("%22uuid%22%3A%22series-44%22", handler.Requests[0].Body);
        Assert.Contains("%22channel%22%3A%22ch-10%22", handler.Requests[0].Body);
        Assert.Contains("%22record%22%3A1", handler.Requests[0].Body);
    }

    [Fact]
    public async Task UpdateSeriesTimerAsync_IncludesExtendedFields()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "{}");
        var sut = CreateSut(handler, out _, out _, CreateConfig());

        await sut.UpdateSeriesTimerAsync(
            new SeriesTimerInfo
            {
                Id = "series-88",
                ChannelId = "ch-12",
                Name = "My Series",
                Overview = "My Overview",
                Priority = 9,
                RecordAnyTime = true,
                RecordAnyChannel = true,
                RecordNewOnly = false,
                Days = new List<DayOfWeek> { DayOfWeek.Sunday, DayOfWeek.Wednesday }
            },
            CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Contains("%22record%22%3A0", handler.Requests[0].Body);
        Assert.Contains("%22pri%22%3A9", handler.Requests[0].Body);
        Assert.Contains("%22name%22%3A%22My+Series%22", handler.Requests[0].Body);
        Assert.Contains("%22title%22%3A%22My+Series%22", handler.Requests[0].Body);
        Assert.Contains("%22comment%22%3A%22My+Overview%22", handler.Requests[0].Body);
        Assert.Contains("%22weekdays%22%3A%5B3%2C7%5D", handler.Requests[0].Body);
    }

    [Fact]
    public async Task UpdateSeriesTimerAsync_WithMissingId_ThrowsArgumentException()
    {
        var sut = CreateSut(new QueueHttpMessageHandler(), out _, out _, CreateConfig());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.UpdateSeriesTimerAsync(new SeriesTimerInfo { Id = string.Empty }, CancellationToken.None));
    }

    [Fact]
    public async Task CancelSeriesTimerAsync_PostsUuidFormViaApiClient()
    {
        var sut = CreateSut(new QueueHttpMessageHandler(), out _, out var apiClient, CreateConfig());
        IEnumerable<KeyValuePair<string, string>>? postedValues = null;

        apiClient
            .Setup(x => x.PostFormAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>(), It.IsAny<CancellationToken>()))
            .Callback<HttpClient, string, IEnumerable<KeyValuePair<string, string>>, CancellationToken>((_, _, values, _) => postedValues = values)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        await sut.CancelSeriesTimerAsync("series-7", CancellationToken.None);

        Assert.NotNull(postedValues);
        Assert.Contains(postedValues!, pair => pair.Key == "uuid" && pair.Value == "series-7");
    }

    [Fact]
    public async Task CancelSeriesTimerAsync_WhenHttpNonSuccess_ThrowsInvalidOperationException()
    {
        var sut = CreateSut(new QueueHttpMessageHandler(), out _, out var apiClient, CreateConfig());
        apiClient
            .Setup(x => x.PostFormAsync(It.IsAny<HttpClient>(), It.IsAny<string>(), It.IsAny<IEnumerable<KeyValuePair<string, string>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("cancel series failed") });

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.CancelSeriesTimerAsync("series-77", CancellationToken.None));
    }

    [Fact]
    public async Task CancelSeriesTimerAsync_WithEmptyId_ThrowsArgumentException()
    {
        var sut = CreateSut(new QueueHttpMessageHandler(), out _, out _, CreateConfig());

        await Assert.ThrowsAsync<ArgumentException>(() => sut.CancelSeriesTimerAsync(string.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task CreateSeriesTimerAsync_WithMissingName_ThrowsArgumentException()
    {
        var sut = CreateSut(new QueueHttpMessageHandler(), out _, out _, CreateConfig());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.CreateSeriesTimerAsync(new SeriesTimerInfo { ChannelId = "ch-1", Name = string.Empty }, CancellationToken.None));
    }

    [Fact]
    public async Task GetNewTimerDefaultsAsync_WithProgram_UsesConfigAndProgramFields()
    {
        var sut = CreateSut(new QueueHttpMessageHandler(), out _, out _, CreateConfig(priority: 9));

        var defaults = await sut.GetNewTimerDefaultsAsync(
            new ProgramInfo { ChannelId = "ch-42", Name = "Program Name", Overview = "Program Overview" },
            CancellationToken.None);

        Assert.Equal("ch-42", defaults.ChannelId);
        Assert.Equal("Program Name", defaults.Name);
        Assert.Equal("Program Overview", defaults.Overview);
        Assert.Equal(9, defaults.Priority);
        Assert.True(defaults.RecordAnyTime);
        Assert.True(defaults.RecordNewOnly);
        Assert.Equal(7, defaults.Days.Count);
    }

    [Fact]
    public async Task GetNewTimerDefaultsAsync_WithNullProgram_ReturnsSafeDefaults()
    {
        var sut = CreateSut(new QueueHttpMessageHandler(), out _, out _, CreateConfig());

        var defaults = await sut.GetNewTimerDefaultsAsync(null!, CancellationToken.None);

        Assert.Null(defaults.ChannelId);
        Assert.Null(defaults.Name);
        Assert.Null(defaults.Overview);
        Assert.Equal(7, defaults.Days.Count);
    }

    [Theory]
    [InlineData("recording", "InProgress")]
    [InlineData("completed", "Completed")]
    [InlineData("completedWarning", "Completed")]
    [InlineData("completedError", "Error")]
    [InlineData("missed", "Error")]
    [InlineData("invalid", "Error")]
    [InlineData("scheduled", "New")]
    [InlineData(null, "New")]
    [InlineData("", "New")]
    public async Task GetTimersAsync_MapsRecordingStatus(string? schedStatus, string expectedStatus)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var schedField = schedStatus == null ? "null" : $"\"{schedStatus}\"";
        var responseJson = $$"""
                             {
                               "entries": [
                                 {
                                   "uuid": "t1",
                                   "channel": "ch-1",
                                   "start": {{now}},
                                   "stop": {{now + 3600}},
                                   "enabled": true,
                                   "fileremoved": 0,
                                   "sched_status": {{schedField}}
                                 }
                               ]
                             }
                             """;

        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, responseJson);
        var sut = CreateSut(handler, out _, out _, CreateConfig());

        var result = (await sut.GetTimersAsync(CancellationToken.None)).ToList();

        Assert.Single(result);
        Assert.Equal(expectedStatus, result[0].Status.ToString());
    }

    [Fact]
    public async Task UpdateTimerAsync_WithAllOptionalFields_IncludesStartStopPriorityChannelAndOverview()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "{}");
        var sut = CreateSut(handler, out _, out _, CreateConfig());
        var start = new DateTime(2026, 5, 1, 20, 0, 0, DateTimeKind.Utc);
        var stop = start.AddHours(1);

        await sut.UpdateTimerAsync(new TimerInfo
        {
            Id = "timer-opts",
            StartDate = start,
            EndDate = stop,
            Priority = 8,
            ChannelId = "ch-opt",
            Name = "Optional Name",
            Overview = "Optional Overview",
            PrePaddingSeconds = 0,
            PostPaddingSeconds = 0
        }, CancellationToken.None);

        var body = handler.Requests[0].Body;
        Assert.Contains("%22start%22%3A", body);
        Assert.Contains("%22stop%22%3A", body);
        Assert.Contains("%22pri%22%3A8", body);
        Assert.Contains("%22channel%22%3A%22ch-opt%22", body);
        Assert.Contains("%22disp_title%22%3A%22Optional+Name%22", body);
        Assert.Contains("%22disp_extratext%22%3A%22Optional+Overview%22", body);
    }

    [Fact]
    public async Task UpdateTimerAsync_WhenHttpNonSuccess_ThrowsInvalidOperationException()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError, "server error");
        var sut = CreateSut(handler, out _, out _, CreateConfig());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateTimerAsync(new TimerInfo { Id = "timer-fail", PrePaddingSeconds = 0, PostPaddingSeconds = 0 }, CancellationToken.None));
    }

    [Fact]
    public async Task CreateTimerAsync_WhenResponseHasUuid_SetsInfoId()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{ "entries": [ { "name": "default", "uuid": "profile-uuid" } ] }""");
        handler.Enqueue(HttpStatusCode.OK, """{ "uuid": "created-timer-abc" }""");

        var sut = CreateSut(handler, out _, out _, CreateConfig());
        var info = new TimerInfo
        {
            ChannelId = "ch-1",
            Name = "My Show",
            StartDate = DateTime.UtcNow.AddMinutes(10),
            EndDate = DateTime.UtcNow.AddMinutes(40)
        };

        await sut.CreateTimerAsync(info, CancellationToken.None);

        Assert.Equal("created-timer-abc", info.Id);
    }

    [Fact]
    public async Task CreateSeriesTimerAsync_WithRecordAnyChannel_SendsNullChannelInPayload()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{ "entries": [ { "name": "default", "uuid": "profile-uuid" } ] }""");
        handler.Enqueue(HttpStatusCode.OK, """{ "uuid": "any-ch-series" }""");

        var sut = CreateSut(handler, out _, out _, CreateConfig());
        var info = new SeriesTimerInfo
        {
            Name = "Any Channel Show",
            RecordAnyChannel = true
        };

        await sut.CreateSeriesTimerAsync(info, CancellationToken.None);

        Assert.Equal("any-ch-series", info.Id);
        // channel=null is serialized as "channel":null in the JSON payload
        Assert.Contains("%22channel%22%3Anull", handler.Requests[1].Body);
    }

    [Fact]
    public async Task UpdateSeriesTimerAsync_WhenHttpNonSuccess_ThrowsInvalidOperationException()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.BadGateway, "gateway error");
        var sut = CreateSut(handler, out _, out _, CreateConfig());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateSeriesTimerAsync(new SeriesTimerInfo { Id = "series-fail" }, CancellationToken.None));
    }

    [Fact]
    public async Task CreateTimerAsync_WhenResponseHasEntryProperty_SetsInfoId()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{ "entries": [ { "name": "default", "uuid": "profile-uuid" } ] }""");
        handler.Enqueue(HttpStatusCode.OK, """{ "entry": { "uuid": "nested-entry-id" } }""");

        var sut = CreateSut(handler, out _, out _, CreateConfig());
        var info = new TimerInfo
        {
            ChannelId = "ch-1",
            Name = "Nested",
            StartDate = DateTime.UtcNow.AddMinutes(10),
            EndDate = DateTime.UtcNow.AddMinutes(40)
        };

        await sut.CreateTimerAsync(info, CancellationToken.None);

        Assert.Equal("nested-entry-id", info.Id);
    }

    [Fact]
    public async Task CreateSeriesTimerAsync_WhenResponseBodyIsInvalidJson_AssignsFallbackId()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, """{ "entries": [ { "name": "default", "uuid": "profile-uuid" } ] }""");
        handler.Enqueue(HttpStatusCode.OK, "not-json");

        var sut = CreateSut(handler, out _, out _, CreateConfig());
        var info = new SeriesTimerInfo
        {
            Name = "BadJson Series",
            ChannelId = "ch-1"
        };

        await sut.CreateSeriesTimerAsync(info, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(info.Id));
    }

    private static DvrService CreateSut(
        QueueHttpMessageHandler handler,
        out Mock<IUrlBuilder> urlBuilder,
        out Mock<IApiClient> apiClient,
        PluginConfiguration? configuration)
    {
        urlBuilder = new Mock<IUrlBuilder>();
        apiClient = new Mock<IApiClient>();

        urlBuilder
            .Setup(x => x.BuildUrlWithHeaderAuth(It.IsAny<PluginConfiguration>(), It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) =>
                "http://tvheadend.local/" + endpoint.TrimStart('/'));
        urlBuilder
            .Setup(x => x.BuildUrlWithParameterAuth(It.IsAny<PluginConfiguration>(), It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) =>
                "http://tvheadend.local/" + endpoint.TrimStart('/'));
        urlBuilder
            .Setup(x => x.BuildUrlWithUrlAuth(It.IsAny<PluginConfiguration>(), It.IsAny<string>()))
            .Returns<PluginConfiguration, string>((_, endpoint) =>
                "http://tvheadend.local/" + endpoint.TrimStart('/'));

        apiClient.Setup(x => x.GetCurrentConfiguration()).Returns(configuration);
        apiClient.Setup(x => x.BuildHttpClient(It.IsAny<PluginConfiguration>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));
        apiClient.Setup(x => x.PostFormAsync(
                It.IsAny<HttpClient>(),
                It.IsAny<string>(),
                It.IsAny<IEnumerable<KeyValuePair<string, string>>>(),
                It.IsAny<CancellationToken>()))
            .Returns<HttpClient, string, IEnumerable<KeyValuePair<string, string>>, CancellationToken>(
                (client, url, formValues, ct) =>
                {
                    var content = new FormUrlEncodedContent(formValues);
                    return client.PostAsync(url, content, ct);
                });

        return new DvrService(NullLogger<DvrService>.Instance, apiClient.Object, urlBuilder.Object);
    }

    private static PluginConfiguration CreateConfig(int priority = 5, string recordingProfile = "default")
    {
        return new PluginConfiguration
        {
            Host = "tvheadend.local",
            Port = 9981,
            UseSSL = false,
            Webroot = "/",
            AllowAnonymousAccess = true,
            Priority = priority,
            RecordingProfile = recordingProfile
        };
    }

    private sealed class QueueHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<CapturedRequest> Requests { get; } = new();

        public void Enqueue(HttpStatusCode statusCode, string body)
        {
            _responses.Enqueue(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri?.ToString() ?? string.Empty,
                body));

            if (_responses.Count == 0)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("No queued response")
                };
            }

            return _responses.Dequeue();
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string Url, string Body);
}
