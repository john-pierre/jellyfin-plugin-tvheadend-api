using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.TvHeadendApi.Model.Diagnostic;
using Jellyfin.Plugin.TvHeadendApi.Model.Dvr;
using Jellyfin.Plugin.TvHeadendApi.Model.Guide;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Integration;

/// <summary>
/// Contract tests that validate TVHeadend API JSON responses deserialize
/// correctly into plugin model types. Uses realistic JSON payloads
/// matching actual TVHeadend 4.3+ API output.
/// </summary>
public class TvHeadendApiContractTests
{
    // ── /api/channel/grid ────────────────────────────────────────────

    [Fact]
    public void ChannelGrid_DeserializesRealisticPayload()
    {
        const string json = """
        {
          "entries": [
            {
              "uuid": "b33aaa0113626f7e8e08d4c04083ea4f",
              "enabled": true,
              "name": "Das Erste HD",
              "number": 1000000,
              "icon": "file:///picons/daserstehd.png",
              "icon_public_url": "imagecache/14",
              "epgauto": true,
              "epglimit": 0,
              "services": ["a2251f9e8569315b8de5c8411a1d35b6"],
              "tags": ["cfa6fbe8db7cbef6edcfb405cee836de", "abc123"],
              "bouquet": ""
            },
            {
              "uuid": "c44bbb0224737f8f9f19e5d15194fb5f",
              "enabled": false,
              "name": "ZDF HD",
              "number": 2000000,
              "icon": "",
              "icon_public_url": "",
              "epgauto": true,
              "epglimit": 10,
              "services": [],
              "tags": [],
              "bouquet": "bouquet1"
            }
          ],
          "total": 2
        }
        """;

        var result = JsonSerializer.Deserialize<ChannelGridResponse>(json, JsonDefaults.Api);

        Assert.NotNull(result);
        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.Entries.Count);

        var ch1 = result.Entries[0];
        Assert.Equal("b33aaa0113626f7e8e08d4c04083ea4f", ch1.Uuid);
        Assert.True(ch1.Enabled);
        Assert.Equal("Das Erste HD", ch1.Name);
        Assert.Equal(1_000_000L, ch1.Number);
        Assert.Equal("imagecache/14", ch1.IconPublicUrl);
        Assert.Equal(2, ch1.Tags.Count);
        Assert.Single(ch1.Services);

        var ch2 = result.Entries[1];
        Assert.False(ch2.Enabled);
        Assert.Empty(ch2.Tags);
        Assert.Equal("bouquet1", ch2.Bouquet);
    }

    [Fact]
    public void ChannelGrid_DeserializesEmptyGrid()
    {
        const string json = """{ "entries": [], "total": 0 }""";

        var result = JsonSerializer.Deserialize<ChannelGridResponse>(json, JsonDefaults.Api);

        Assert.NotNull(result);
        Assert.Equal(0, result.Total);
        Assert.Empty(result.Entries);
    }

    // ── /api/epg/events/grid ─────────────────────────────────────────

    [Fact]
    public void EpgEventsGrid_DeserializesRealisticPayload()
    {
        const string json = """
        {
          "entries": [
            {
              "eventId": 510178,
              "channelUuid": "e1b1de65605dc4e13bac6d4478e66a44",
              "channelName": "More 4",
              "channelNumber": "14",
              "channelIcon": "imagecache/37",
              "title": "Time Team",
              "subtitle": "Tony Robinson and the Team...",
              "description": "Full description of the programme.",
              "summary": "Short summary.",
              "start": 1508760600,
              "stop": 1508764500,
              "genre": [160],
              "nextEventId": 510181,
              "widescreen": 1,
              "hd": 1,
              "subtitled": 1,
              "new": 0,
              "ageRating": 9,
              "ratingLabel": "PG",
              "serieslinkUri": "crid://www.channel4.com/M4EI0021031162146302",
              "serieslinkId": 510179,
              "episodeId": 510180,
              "episodeUri": "crid://www.channel4.com/41408/013",
              "copyright_year": 2022,
              "first_aired": 1508760600,
              "image": "https://images.example.com/image.webp",
              "category": ["documentary", "history"]
            }
          ],
          "totalCount": 1
        }
        """;

        var result = JsonSerializer.Deserialize<EpgEventsGridResponse>(json, JsonDefaults.Api);

        Assert.NotNull(result);
        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Entries);

        var ev = result.Entries[0];
        Assert.Equal(510178, ev.EventId);
        Assert.Equal("e1b1de65605dc4e13bac6d4478e66a44", ev.ChannelUuid);
        Assert.Equal("Time Team", ev.Title);
        Assert.Equal(1508760600L, ev.Start);
        Assert.Equal(1508764500L, ev.Stop);
        Assert.Single(ev.Genre);
        Assert.Equal(160, ev.Genre[0]);
        Assert.Equal(1, ev.IsWidescreen);
        Assert.Equal(1, ev.IsHD);
        Assert.Equal(9, ev.AgeRating);
        Assert.Equal("PG", ev.RatingLabel);
        Assert.Equal("crid://www.channel4.com/M4EI0021031162146302", ev.SerieslinkUri);
        Assert.Equal(2022, ev.CopyrightYear);
        Assert.Equal(1508760600L, ev.FirstAired);
        Assert.Equal("https://images.example.com/image.webp", ev.Image);
        Assert.Equal(2, ev.Category.Count);
    }

    [Fact]
    public void EpgEventsGrid_DeserializesMinimalEntry()
    {
        const string json = """
        {
          "entries": [
            {
              "eventId": 1,
              "channelUuid": "abc",
              "start": 100,
              "stop": 200,
              "title": "Test"
            }
          ],
          "totalCount": 1
        }
        """;

        var result = JsonSerializer.Deserialize<EpgEventsGridResponse>(json, JsonDefaults.Api);

        Assert.NotNull(result);
        var ev = result.Entries[0];
        Assert.Equal(1, ev.EventId);
        Assert.Equal("Test", ev.Title);
        Assert.Null(ev.IsHD);
        Assert.Null(ev.AgeRating);
        Assert.Empty(ev.Genre);
        Assert.Null(ev.FirstAired);
        Assert.Null(ev.Image);
    }

    // ── /api/dvr/entry/grid ──────────────────────────────────────────

    [Fact]
    public void DvrEntryGrid_DeserializesRealisticPayload()
    {
        const string json = """
        {
          "entries": [
            {
              "uuid": "5577325d4d8bbb8e6f6b03bc98794a77",
              "enabled": true,
              "channel": "6c038f7ea7caa46af45e943926e08bb2",
              "channelname": "VOX",
              "channel_icon": "imagecache/25",
              "disp_title": "Medical Detectives",
              "disp_subtitle": "Episode 5",
              "disp_description": "Full episode description.",
              "start": 1735603200,
              "stop": 1735605900,
              "start_extra": 5,
              "stop_extra": 5,
              "start_real": 1735603170,
              "stop_real": 1735605900,
              "duration": 2700,
              "sched_status": "completed",
              "status": "Completed OK",
              "pri": 2,
              "config_name": "7a5edfbe189851e5b1d1df19c93962f0",
              "creator": "jellyfin",
              "owner": "jellyfin",
              "filesize": 60315100,
              "filename": "/recordings/vox.ts",
              "url": "dvrfile/5577325d4d8bbb8e6f6b03bc98794a77",
              "create": 1735598123,
              "title": { "eng": "Medical Detectives" },
              "description": { "eng": "Full episode description." },
              "genre": [],
              "category": [],
              "keyword": [],
              "credits": {},
              "errors": 0,
              "data_errors": 0,
              "errorcode": 0,
              "age_rating": 0,
              "autorec": "",
              "autorec_caption": "",
              "broadcast": 0,
              "content_type": 0,
              "copyright_year": 0,
              "duplicate": 0,
              "dvb_eid": 0,
              "episode_disp": "",
              "fanart_image": "",
              "fileremoved": 0,
              "first_aired": 0,
              "image": "",
              "norerecord": false,
              "noresched": true,
              "parent": "",
              "child": "",
              "playcount": 0,
              "playposition": 0,
              "rating_authority": "",
              "rating_country": "",
              "rating_icon": "",
              "rating_label": "",
              "rating_label_uuid": "",
              "removal": 0,
              "retention": 0,
              "timerec": "",
              "timerec_caption": "",
              "watched": 0
            }
          ],
          "total": 1
        }
        """;

        var result = JsonSerializer.Deserialize<DvrEntryGridResponse>(json, JsonDefaults.Api);

        Assert.NotNull(result);
        Assert.Equal(1, result.Total);
        Assert.Single(result.Entries);

        var entry = result.Entries[0];
        Assert.Equal("5577325d4d8bbb8e6f6b03bc98794a77", entry.Uuid);
        Assert.True(entry.Enabled);
        Assert.Equal("VOX", entry.ChannelName);
        Assert.Equal("Medical Detectives", entry.DispTitle);
        Assert.Equal(1735603200L, entry.Start);
        Assert.Equal(1735605900L, entry.Stop);
        Assert.Equal(5, entry.StartExtra);
        Assert.Equal(5, entry.StopExtra);
        Assert.Equal(2700, entry.Duration);
        Assert.Equal("completed", entry.SchedStatus);
        Assert.Equal("jellyfin", entry.Creator);
        Assert.Equal(60315100L, entry.FileSize);
        Assert.True(entry.NoResched);
        Assert.Equal("Medical Detectives", entry.Title["eng"]);
    }

    // ── /api/dvr/autorec/grid ────────────────────────────────────────

    [Fact]
    public void DvrAutoRecGrid_DeserializesRealisticPayload()
    {
        const string json = """
        {
          "entries": [
            {
              "uuid": "6824d98d296cc926a312deed00134d65",
              "enabled": true,
              "name": "test",
              "title": "baum",
              "channel": "cb027d8e79e8dca308f6636f79b0d47a",
              "config_name": "7a5edfbe189851e5b1d1df19c93962f0",
              "serieslink": "crid://www.five.tv/R5HP0",
              "creator": "root",
              "owner": "root",
              "comment": "Created from EPG query",
              "pri": 6,
              "record": 15,
              "start": "Any",
              "start_window": "Any",
              "start_extra": 5,
              "stop_extra": 5,
              "weekdays": [1, 2, 3, 4, 5],
              "fulltext": false,
              "btype": 0,
              "content_type": 0,
              "maxcount": 0,
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

        var result = JsonSerializer.Deserialize<DvrAutoRecGridResponse>(json, JsonDefaults.Api);

        Assert.NotNull(result);
        Assert.Single(result.Entries);

        var rule = result.Entries[0];
        Assert.Equal("6824d98d296cc926a312deed00134d65", rule.Uuid);
        Assert.True(rule.Enabled);
        Assert.Equal("baum", rule.Title);
        Assert.Equal("crid://www.five.tv/R5HP0", rule.SeriesLink);
        Assert.Equal(5, rule.Weekdays.Count);
        Assert.Equal(5, rule.StartExtra);
        Assert.Equal(5, rule.StopExtra);
        Assert.Equal("Created from EPG query", rule.Comment);
    }

    // ── /api/channeltag/list ─────────────────────────────────────────

    [Fact]
    public void ChannelTagList_DeserializesRealisticPayload()
    {
        const string json = """
        {
          "entries": [
            { "key": "721356e8bf5129df2756012b73b11cc3", "val": "HDTV" },
            { "key": "abc123", "val": "Radio" }
          ]
        }
        """;

        var result = JsonSerializer.Deserialize<ChannelTagResponse>(json, JsonDefaults.Api);

        Assert.NotNull(result);
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("HDTV", result.Entries[0].Val);
        Assert.Equal("abc123", result.Entries[1].Key);
    }

    // ── /api/serverinfo ──────────────────────────────────────────────

    [Fact]
    public void ServerInfo_DeserializesRealisticPayload()
    {
        const string json = """
        {
          "sw_version": "4.3-2078~g1b1de65",
          "api_version": 19,
          "name": "Tvheadend"
        }
        """;

        var result = JsonSerializer.Deserialize<ServerInfoResponse>(json, JsonDefaults.Api);

        Assert.NotNull(result);
        Assert.Equal("4.3-2078~g1b1de65", result.SwVersion);
        Assert.Equal(19, result.ApiVersion);
        Assert.Equal("Tvheadend", result.Name);
    }

    [Fact]
    public void ServerInfo_DeserializesWithMissingOptionalFields()
    {
        const string json = """{ "sw_version": "4.2", "name": "Test" }""";

        var result = JsonSerializer.Deserialize<ServerInfoResponse>(json, JsonDefaults.Api);

        Assert.NotNull(result);
        Assert.Equal("4.2", result.SwVersion);
        Assert.Null(result.ApiVersion);
    }

    // ── /api/profile/list ────────────────────────────────────────────

    [Fact]
    public void ProfileList_DeserializesRealisticPayload()
    {
        const string json = """
        {
          "entries": [
            { "key": "abc-uuid-1", "val": "pass" },
            { "key": "def-uuid-2", "val": "jellyfin" },
            { "key": "ghi-uuid-3", "val": "webtv-h264-aac-mpegts" }
          ]
        }
        """;

        var result = JsonSerializer.Deserialize<ProfileListResponse>(json, JsonDefaults.Api);

        Assert.NotNull(result);
        Assert.Equal(3, result.Entries.Length);
        Assert.Equal("pass", result.Entries[0].Val);
        Assert.Equal("jellyfin", result.Entries[1].Val);
        Assert.Equal("def-uuid-2", result.Entries[1].Key);
    }

    // ── /api/epg/content_type/list ───────────────────────────────────

    [Fact]
    public void EpgContentTypeList_DeserializesRealisticPayload()
    {
        const string json = """
        {
          "entries": [
            { "key": 16, "val": "Movie / Drama" },
            { "key": 32, "val": "News / Current affairs" },
            { "key": 64, "val": "Sports" }
          ]
        }
        """;

        var result = JsonSerializer.Deserialize<EpgContentTypeListResponse>(json, JsonDefaults.Api);

        Assert.NotNull(result);
        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(16, result.Entries[0].Key);
        Assert.Equal("Movie / Drama", result.Entries[0].Val);
    }

    // ── Resilience: unknown fields are ignored ───────────────────────

    [Fact]
    public void Deserialization_IgnoresUnknownFields()
    {
        const string json = """
        {
          "entries": [
            {
              "uuid": "abc",
              "name": "Test",
              "number": 1000000,
              "enabled": true,
              "unknown_future_field": "should be ignored",
              "another_new_array": [1, 2, 3]
            }
          ],
          "total": 1,
          "some_new_metadata": true
        }
        """;

        var result = JsonSerializer.Deserialize<ChannelGridResponse>(json, JsonDefaults.Api);

        Assert.NotNull(result);
        Assert.Single(result.Entries);
        Assert.Equal("abc", result.Entries[0].Uuid);
    }

    // ── DVR config grid ──────────────────────────────────────────────

    [Fact]
    public void DvrConfigGrid_DeserializesRealisticPayload()
    {
        const string json = """
        {
          "entries": [
            {
              "uuid": "7a5edfbe189851e5b1d1df19c93962f0",
              "name": "",
              "enabled": true
            }
          ],
          "total": 1
        }
        """;

        var result = JsonSerializer.Deserialize<DvrConfigGridResponse>(json, JsonDefaults.Api);

        Assert.NotNull(result);
        Assert.Single(result.Entries);
        Assert.Equal("7a5edfbe189851e5b1d1df19c93962f0", result.Entries[0].Uuid);
    }
}

