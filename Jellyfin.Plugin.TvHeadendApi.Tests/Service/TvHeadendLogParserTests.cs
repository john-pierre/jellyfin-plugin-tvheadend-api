using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service;

/// <summary>
/// Tests for <see cref="TvHeadendLogParser"/>.
/// </summary>
public class TvHeadendLogParserTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_TimestampBracketLevel_ExtractsAll()
    {
        var result = TvHeadendLogParser.Parse("2026-04-24 13:20:15.123 [  WARNING] mpegts: signal lost on adapter 0");

        Assert.True(result.ParseSuccess);
        Assert.NotNull(result.ParsedTimestampUtc);
        Assert.Equal(2026, result.ParsedTimestampUtc!.Value.Year);
        Assert.Equal(4, result.ParsedTimestampUtc.Value.Month);
        Assert.Equal(24, result.ParsedTimestampUtc.Value.Day);
        Assert.Equal(13, result.ParsedTimestampUtc.Value.Hour);
        Assert.Equal(20, result.ParsedTimestampUtc.Value.Minute);
        Assert.Equal(15, result.ParsedTimestampUtc.Value.Second);
        Assert.Equal("Warning", result.Level);
        Assert.Equal("tvheadend_warning", result.LogType);
        Assert.Equal("mpegts", result.Category);
        Assert.Equal("signal lost on adapter 0", result.MessageWithoutTimestamp);
        Assert.False(string.IsNullOrEmpty(result.OriginalLineHash));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_TimestampWithoutMilliseconds_Works()
    {
        var result = TvHeadendLogParser.Parse("2026-04-24 13:20:15 [  INFO] linuxdvb: tuner ready");

        Assert.True(result.ParseSuccess);
        Assert.NotNull(result.ParsedTimestampUtc);
        Assert.Equal(15, result.ParsedTimestampUtc!.Value.Second);
        Assert.Equal("Information", result.Level);
        Assert.Equal("tvheadend_info", result.LogType);
        Assert.Equal("linuxdvb", result.Category);
        Assert.Equal("tuner ready", result.MessageWithoutTimestamp);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_ErrorLevel_MapsCorrectly()
    {
        var result = TvHeadendLogParser.Parse("2026-04-24 10:00:00 [  ERROR] dvr: recording failed");

        Assert.True(result.ParseSuccess);
        Assert.Equal("Error", result.Level);
        Assert.Equal("tvheadend_error", result.LogType);
        Assert.Equal("dvr", result.Category);
        Assert.Equal("recording failed", result.MessageWithoutTimestamp);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_DebugLevel_MapsCorrectly()
    {
        var result = TvHeadendLogParser.Parse("2026-04-24 10:00:00 [  DEBUG] htsp: client connected");

        Assert.True(result.ParseSuccess);
        Assert.Equal("Debug", result.Level);
        Assert.Equal("tvheadend_debug", result.LogType);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_NoticeLevel_MapsToInformation()
    {
        var result = TvHeadendLogParser.Parse("2026-04-24 10:00:00 [ NOTICE] main: starting up");

        Assert.True(result.ParseSuccess);
        Assert.Equal("Information", result.Level);
        Assert.Equal("tvheadend_notice", result.LogType);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_NoTimestamp_BracketLevelOnly()
    {
        var result = TvHeadendLogParser.Parse("[  WARNING] mpegts: signal degraded");

        Assert.False(result.ParseSuccess);
        Assert.Null(result.ParsedTimestampUtc);
        Assert.Equal("Warning", result.Level);
        Assert.Equal("mpegts", result.Category);
        Assert.Equal("signal degraded", result.MessageWithoutTimestamp);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_CompletelyUnparseable_ReturnsFullMessage()
    {
        var result = TvHeadendLogParser.Parse("some random text without structure");

        Assert.False(result.ParseSuccess);
        Assert.Null(result.ParsedTimestampUtc);
        Assert.Equal("Information", result.Level);
        Assert.Equal("some random text without structure", result.MessageWithoutTimestamp);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_EmptyString_ReturnsEmpty()
    {
        var result = TvHeadendLogParser.Parse(string.Empty);

        Assert.False(result.ParseSuccess);
        Assert.Equal(string.Empty, result.MessageWithoutTimestamp);
        Assert.False(string.IsNullOrEmpty(result.OriginalLineHash));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_NullString_ReturnsEmpty()
    {
        var result = TvHeadendLogParser.Parse(null!);

        Assert.False(result.ParseSuccess);
        Assert.Equal(string.Empty, result.MessageWithoutTimestamp);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_TimestampRemovedFromMessage()
    {
        var result = TvHeadendLogParser.Parse("2026-04-24 13:20:15.123 [  WARNING] mpegts: signal lost");

        Assert.DoesNotContain("2026-04-24", result.MessageWithoutTimestamp);
        Assert.DoesNotContain("13:20:15", result.MessageWithoutTimestamp);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_HashIsStable()
    {
        const string line = "2026-04-24 13:20:15.123 [  WARNING] mpegts: signal lost";
        var result1 = TvHeadendLogParser.Parse(line);
        var result2 = TvHeadendLogParser.Parse(line);

        Assert.Equal(result1.OriginalLineHash, result2.OriginalLineHash);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_DifferentLines_DifferentHashes()
    {
        var result1 = TvHeadendLogParser.Parse("line one");
        var result2 = TvHeadendLogParser.Parse("line two");

        Assert.NotEqual(result1.OriginalLineHash, result2.OriginalLineHash);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_MillisecondsParsed()
    {
        var result = TvHeadendLogParser.Parse("2026-04-24 13:20:15.456 [  INFO] test: ok");

        Assert.True(result.ParseSuccess);
        Assert.Equal(456, result.ParsedTimestampUtc!.Value.Millisecond);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_ColonFormat_Works()
    {
        var result = TvHeadendLogParser.Parse("2026-04-24 10:00:00 WARNING: mpegts: something happened");

        Assert.True(result.ParseSuccess);
        Assert.Equal("Warning", result.Level);
        Assert.Equal("mpegts", result.Category);
        Assert.Equal("something happened", result.MessageWithoutTimestamp);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_MalformedTimestamp_FallsBack()
    {
        var result = TvHeadendLogParser.Parse("not-a-date 99:99:99 [  INFO] test: data");

        // Should not parse as timestamp
        Assert.False(result.ParseSuccess);
        Assert.Null(result.ParsedTimestampUtc);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parse_NoCategoryInRemainder_FullMessagePreserved()
    {
        var result = TvHeadendLogParser.Parse("2026-04-24 10:00:00 [  INFO] simple message without colon");

        Assert.Equal("simple message without colon", result.MessageWithoutTimestamp);
        Assert.Null(result.Category);
    }
}

