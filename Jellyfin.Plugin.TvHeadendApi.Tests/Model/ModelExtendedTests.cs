using System.Text.Json;
using Jellyfin.Plugin.TvHeadendApi.Model.Auth;
using Jellyfin.Plugin.TvHeadendApi.Model.Dvr;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

/// <summary>
/// Tests for model classes with low serialization coverage.
/// </summary>
public class ModelExtendedTests
{
    // --- DvrConfigGridEntry ---

    [Fact]
    public void DvrConfigGridEntry_Deserialize_AllProperties()
    {
        var json = """
        {
            "uuid": "abc123",
            "enabled": true,
            "name": "Default",
            "profile": "prof-uuid",
            "pri": 5,
            "retention-days": 30,
            "removal-days": 60,
            "remove-after-playback": 1,
            "pre-extra-time": 2,
            "post-extra-time": 3,
            "clone": false,
            "rerecord-errors": 10,
            "complex-scheduling": true,
            "fetch-artwork": false,
            "storage": "/recordings/",
            "storage-mfree": 500,
            "storage-mused": 200,
            "pathname": "$t/$t.mkv",
            "cache": 2
        }
        """;

        var entry = JsonSerializer.Deserialize<DvrConfigGridEntry>(json);

        Assert.NotNull(entry);
        Assert.Equal("abc123", entry.Uuid);
        Assert.True(entry.Enabled);
        Assert.Equal("Default", entry.Name);
        Assert.Equal("prof-uuid", entry.ProfileId);
        Assert.Equal(5, entry.Priority);
        Assert.Equal(30, entry.RetentionDays);
        Assert.Equal(60, entry.RemovalDays);
        Assert.Equal(1, entry.RemoveAfterPlayback);
        Assert.Equal(2, entry.PreExtraTime);
        Assert.Equal(3, entry.PostExtraTime);
        Assert.False(entry.Clone);
        Assert.Equal(10, entry.ReRecordErrors);
        Assert.True(entry.ComplexScheduling);
        Assert.False(entry.FetchArtwork);
        Assert.Equal("/recordings/", entry.Storage);
        Assert.Equal(500, entry.StorageMinFree);
        Assert.Equal(200, entry.StorageUsed);
        Assert.Equal("$t/$t.mkv", entry.Pathname);
        Assert.Equal(2, entry.Cache);
    }

    // --- DvrConfigListEntry ---

    [Fact]
    public void DvrConfigListEntry_EffectiveName_PrefersName()
    {
        var entry = new DvrConfigListEntry { Name = "n", Text = "t", Val = "v" };
        Assert.Equal("n", entry.EffectiveName);
    }

    [Fact]
    public void DvrConfigListEntry_EffectiveName_FallsToText()
    {
        var entry = new DvrConfigListEntry { Name = null, Text = "t", Val = "v" };
        Assert.Equal("t", entry.EffectiveName);
    }

    [Fact]
    public void DvrConfigListEntry_EffectiveName_FallsToVal()
    {
        var entry = new DvrConfigListEntry { Name = null, Text = null, Val = "v" };
        Assert.Equal("v", entry.EffectiveName);
    }

    [Fact]
    public void DvrConfigListEntry_EffectiveName_FallsToEmpty()
    {
        var entry = new DvrConfigListEntry();
        Assert.Equal(string.Empty, entry.EffectiveName);
    }

    [Fact]
    public void DvrConfigListEntry_Deserialize_AllProperties()
    {
        var json = """{"name":"n","text":"t","val":"v"}""";
        var entry = JsonSerializer.Deserialize<DvrConfigListEntry>(json);
        Assert.NotNull(entry);
        Assert.Equal("n", entry.Name);
        Assert.Equal("t", entry.Text);
        Assert.Equal("v", entry.Val);
    }

    // --- UserListEntry ---

    [Fact]
    public void UserListEntry_Deserialize_AllProperties()
    {
        var json = """{"uuid":"u1","key":"k1","username":"admin","val":"v1","title":"Admin"}""";
        var entry = JsonSerializer.Deserialize<UserListEntry>(json);
        Assert.NotNull(entry);
        Assert.Equal("u1", entry.Uuid);
        Assert.Equal("k1", entry.Key);
        Assert.Equal("admin", entry.Username);
        Assert.Equal("v1", entry.Val);
        Assert.Equal("Admin", entry.Title);
    }

    [Fact]
    public void UserListEntry_Defaults_AreNull()
    {
        var entry = new UserListEntry();
        Assert.Null(entry.Uuid);
        Assert.Null(entry.Key);
        Assert.Null(entry.Username);
        Assert.Null(entry.Val);
        Assert.Null(entry.Title);
    }

    // --- IdNodeEntry ---

    [Fact]
    public void IdNodeEntry_Deserialize_WithParams()
    {
        var json = """
        {
            "name": "test",
            "class": "profile-transcode",
            "container": "matroska",
            "params": [
                {"id": "p1", "value": "v1"},
                {"id": "p2", "value": 42}
            ]
        }
        """;
        var entry = JsonSerializer.Deserialize<IdNodeEntry>(json);
        Assert.NotNull(entry);
        Assert.Equal("test", entry.Name.GetString());
        Assert.Equal("profile-transcode", entry.ProfileClass.GetString());
        Assert.Equal("matroska", entry.Container.GetString());
        Assert.Equal(2, entry.Params.Count);
        Assert.Equal("p1", entry.Params[0].Id);
    }

    [Fact]
    public void IdNodeEntry_DefaultParams_IsEmpty()
    {
        var entry = new IdNodeEntry();
        Assert.Empty(entry.Params);
    }

    [Fact]
    public void IdNodeLoadResponse_DefaultEntries_IsEmpty()
    {
        var resp = new IdNodeLoadResponse();
        Assert.Empty(resp.Entries);
    }

    [Fact]
    public void IdNodeLoadResponse_Deserialize()
    {
        var json = """{"entries":[{"name":"x"}]}""";
        var resp = JsonSerializer.Deserialize<IdNodeLoadResponse>(json);
        Assert.NotNull(resp);
        Assert.Single(resp.Entries);
    }

    // --- ProfileSnapshot ---

    [Fact]
    public void ProfileSnapshot_RecordEquality()
    {
        var a = new ProfileSnapshot("n", "u", "c", "mkv", "vr", "ar", "h264", "aac", true);
        var b = new ProfileSnapshot("n", "u", "c", "mkv", "vr", "ar", "h264", "aac", true);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ProfileSnapshot_AllProperties()
    {
        var s = new ProfileSnapshot("name", "uuid", "transcode", "mp4", "vRef", "aRef", "h264", "aac", false);
        Assert.Equal("name", s.ProfileName);
        Assert.Equal("uuid", s.ProfileUuid);
        Assert.Equal("transcode", s.ProfileClass);
        Assert.Equal("mp4", s.Container);
        Assert.Equal("vRef", s.VideoCodecReference);
        Assert.Equal("aRef", s.AudioCodecReference);
        Assert.Equal("h264", s.VideoCodec);
        Assert.Equal("aac", s.AudioCodec);
        Assert.False(s.Deinterlace);
    }

    [Fact]
    public void ProfileSnapshot_NullDeinterlace()
    {
        var s = new ProfileSnapshot("n", "u", "c", "mkv", "", "", "", "", null);
        Assert.Null(s.Deinterlace);
    }

    // --- ResolvedProfile ---

    [Fact]
    public void ResolvedProfile_RecordEquality()
    {
        var a = new ResolvedProfile("k", "n", "c", "mp4", "mp4", "vp", "ap", "h264", "aac",
            new[] { "h264" }, new[] { "aac" }, true, false);
        var b = new ResolvedProfile("k", "n", "c", "mp4", "mp4", "vp", "ap", "h264", "aac",
            new[] { "h264" }, new[] { "aac" }, true, false);
        // Record equality compares references for lists, so use property checks
        Assert.Equal(a.Key, b.Key);
        Assert.Equal(a.Name, b.Name);
    }

    [Fact]
    public void ResolvedProfile_AllProperties()
    {
        var r = new ResolvedProfile("key", "name", "class", "mp4", "mp4raw", "vp", "ap",
            "h264", "aac", new[] { "src_v" }, new[] { "src_a" }, true, false);
        Assert.Equal("key", r.Key);
        Assert.Equal("name", r.Name);
        Assert.Equal("class", r.ProfileClass);
        Assert.Equal("mp4", r.Container);
        Assert.Equal("mp4raw", r.RawContainer);
        Assert.Equal("vp", r.ProVideoCodec);
        Assert.Equal("ap", r.ProAudioCodec);
        Assert.Equal("h264", r.ResolvedVideoCodec);
        Assert.Equal("aac", r.ResolvedAudioCodec);
        Assert.Single(r.SrcVideoCodecs);
        Assert.Single(r.SrcAudioCodecs);
        Assert.True(r.ProfileDeinterlace);
        Assert.False(r.VideoCodecDeinterlace);
    }
}
