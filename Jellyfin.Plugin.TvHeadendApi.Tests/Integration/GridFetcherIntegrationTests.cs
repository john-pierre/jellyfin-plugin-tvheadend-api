using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Integration;

/// <summary>
/// Integration tests that exercise service-level logic with fake HTTP responses,
/// validating the full pipeline from HTTP response to Jellyfin model output.
/// </summary>
public class GridFetcherIntegrationTests
{
    private static PluginConfiguration CreateConfig() => new()
    {
        Host = "tvh.local",
        Port = 9981,
        Username = "test",
        Password = "pass",
    };

    private static HttpClient CreateFakeHttpClient(string responseJson, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            });
        return new HttpClient(handler.Object);
    }

    // ── GridFetcher: probe + full fetch ──────────────────────────────

    [Fact]
    public async Task FetchAllAsync_WithSmallTotal_ReturnsProbeResult()
    {
        const string json = """
        {
          "entries": [
            { "uuid": "ch1", "name": "ARD", "number": 1000000, "enabled": true, "icon": "", "icon_public_url": "", "epgauto": true, "epglimit": 0, "services": [], "tags": [], "bouquet": "" }
          ],
          "total": 1
        }
        """;

        using var httpClient = CreateFakeHttpClient(json);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        var result = await GridFetcher.FetchAllAsync<Model.Guide.ChannelGridResponse>(
            httpClient,
            "http://tvh.local:9981/api/channel/grid",
            r => r.Total,
            logger,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result.Entries);
        Assert.Equal("ch1", result.Entries[0].Uuid);
        Assert.Equal("ARD", result.Entries[0].Name);
    }

    [Fact]
    public async Task FetchAllAsync_WithNullResponse_ReturnsNull()
    {
        // Empty JSON body
        using var httpClient = CreateFakeHttpClient("null");
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        var result = await GridFetcher.FetchAllAsync<Model.Guide.ChannelGridResponse>(
            httpClient,
            "http://tvh.local:9981/api/channel/grid",
            r => r.Total,
            logger,
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchAllAsync_WithHttpError_ThrowsHttpRequestException()
    {
        using var httpClient = CreateFakeHttpClient("{}", HttpStatusCode.InternalServerError);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await GridFetcher.FetchAllAsync<Model.Guide.ChannelGridResponse>(
                httpClient,
                "http://tvh.local:9981/api/channel/grid",
                r => r.Total,
                logger,
                CancellationToken.None));
    }

    // ── UrlBuilder integration ───────────────────────────────────────

    [Fact]
    public void UrlBuilder_BuildsCorrectStreamUrlWithAuth()
    {
        var config = CreateConfig();
        config.AuthToken = "abc123token";
        config.StreamingProfile = "jellyfin";
        config.Webroot = "/tvh/";

        var builder = new UrlBuilder();
        var url = builder.BuildUrlWithParameterAuth(config, "stream/channel/ch1?profile=jellyfin");

        Assert.Contains("http://tvh.local:9981/tvh/stream/channel/ch1", url);
        Assert.Contains("profile=jellyfin", url);
        Assert.Contains("auth=abc123token", url);
    }

    [Fact]
    public void UrlBuilder_AnonymousAccess_OmitsAuth()
    {
        var config = CreateConfig();
        config.AllowAnonymousAccess = true;
        config.AuthToken = "should-be-omitted";

        var builder = new UrlBuilder();
        var url = builder.BuildUrlWithParameterAuth(config, "stream/channel/ch1");

        Assert.DoesNotContain("auth=", url);
    }

    [Fact]
    public void UrlBuilder_MaskSensitiveData_ReplacesTokenAndCredentials()
    {
        var config = CreateConfig();
        config.AuthToken = "secrettoken";

        var builder = new UrlBuilder();
        var input = "http://tvh.local:9981/stream?auth=secrettoken";
        var masked = builder.MaskSensitiveData(input, config);

        Assert.DoesNotContain("secrettoken", masked);
        Assert.Contains("***", masked);
    }

    // ── EPG response with DvrEntry ───────────────────────────────────

    [Fact]
    public async Task FetchAllAsync_DvrEntryGrid_DeserializesCorrectly()
    {
        const string json = """
        {
          "entries": [
            {
              "uuid": "rec1",
              "enabled": true,
              "channel": "ch-uuid",
              "channelname": "ZDF",
              "disp_title": "Tagesschau",
              "start": 1735603200,
              "stop": 1735605900,
              "duration": 2700,
              "sched_status": "scheduled",
              "status": "Scheduled",
              "pri": 2,
              "start_extra": 3,
              "stop_extra": 5,
              "config_name": "cfg1",
              "creator": "jellyfin",
              "owner": "jellyfin",
              "title": { "deu": "Tagesschau" },
              "description": {},
              "genre": [32],
              "category": ["news"],
              "keyword": [],
              "credits": {},
              "channel_icon": "",
              "errors": 0,
              "data_errors": 0,
              "errorcode": 0,
              "age_rating": 0,
              "autorec": "",
              "autorec_caption": "",
              "broadcast": 0,
              "child": "",
              "content_type": 0,
              "copyright_year": 0,
              "create": 0,
              "disp_description": "",
              "disp_extratext": "",
              "disp_subtitle": "",
              "disp_summary": "",
              "duplicate": 0,
              "dvb_eid": 0,
              "episode_disp": "",
              "fanart_image": "",
              "filename": "",
              "fileremoved": 0,
              "filesize": 0,
              "first_aired": 0,
              "image": "",
              "norerecord": false,
              "noresched": false,
              "parent": "",
              "playcount": 0,
              "playposition": 0,
              "rating_authority": "",
              "rating_country": "",
              "rating_icon": "",
              "rating_label": "",
              "rating_label_uuid": "",
              "removal": 0,
              "retention": 0,
              "start_real": 0,
              "stop_real": 0,
              "timerec": "",
              "timerec_caption": "",
              "url": "",
              "watched": 0
            }
          ],
          "total": 1
        }
        """;

        using var httpClient = CreateFakeHttpClient(json);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        var result = await GridFetcher.FetchAllAsync<Model.Dvr.DvrEntryGridResponse>(
            httpClient,
            "http://tvh.local:9981/api/dvr/entry/grid",
            r => r.Total,
            logger,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result.Entries);
        var entry = result.Entries[0];
        Assert.Equal("rec1", entry.Uuid);
        Assert.Equal("ZDF", entry.ChannelName);
        Assert.Equal("Tagesschau", entry.DispTitle);
        Assert.Equal(3, entry.StartExtra);
        Assert.Equal(5, entry.StopExtra);
        Assert.Equal("scheduled", entry.SchedStatus);
        Assert.Single(entry.Genre);
        Assert.Equal(32, entry.Genre[0]);
    }

    // ── AutoRec grid ─────────────────────────────────────────────────

    [Fact]
    public async Task FetchAllAsync_DvrAutoRecGrid_DeserializesCorrectly()
    {
        const string json = """
        {
          "entries": [
            {
              "uuid": "ar1",
              "enabled": true,
              "name": "News Rule",
              "title": "Tagesschau",
              "channel": "ch-uuid",
              "config_name": "cfg1",
              "serieslink": "",
              "creator": "jellyfin",
              "owner": "jellyfin",
              "comment": "Auto-created",
              "pri": 6,
              "record": 15,
              "start": "Any",
              "start_window": "Any",
              "start_extra": 2,
              "stop_extra": 3,
              "weekdays": [1,2,3,4,5,6,7],
              "fulltext": false,
              "btype": 0,
              "content_type": 0,
              "maxcount": 5,
              "maxduration": 0,
              "maxsched": 0,
              "maxseason": 0,
              "maxyear": 0,
              "minduration": 0,
              "minseason": 0,
              "minyear": 0,
              "removal": 0,
              "retention": 0,
              "star_rating": 0,
              "tag": "",
              "season": "",
              "brand": ""
            }
          ],
          "total": 1
        }
        """;

        using var httpClient = CreateFakeHttpClient(json);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        var result = await GridFetcher.FetchAllAsync<Model.Dvr.DvrAutoRecGridResponse>(
            httpClient,
            "http://tvh.local:9981/api/dvr/autorec/grid",
            r => r.Total,
            logger,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result.Entries);
        var rule = result.Entries[0];
        Assert.Equal("ar1", rule.Uuid);
        Assert.Equal("Tagesschau", rule.Title);
        Assert.Equal(5, rule.MaxCount);
        Assert.Equal(7, rule.Weekdays.Count);
    }
}

