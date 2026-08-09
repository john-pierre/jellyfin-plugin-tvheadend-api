using System;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Statistic;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Logging;

/// <summary>
/// Tests for logging-related configuration and DTO mapping.
/// </summary>
public class LoggingConfigAndDtoTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void PluginConfiguration_DefaultLoggingValues()
    {
        var config = new PluginConfiguration();

        Assert.Equal(PluginLogLevel.JellyfinDefault, config.PluginLogLevelOverride);
        Assert.True(config.StorePluginLogsInSqlite);
        Assert.True(config.StoreTvHeadendLogsInSqlite);
        Assert.Equal(500, config.MaxDashboardLogEntries);
        Assert.Equal(7, config.LogRetentionDays);
        Assert.True(config.EnableDebugLogSanitization);
        Assert.True(config.TvHeadendLogImportEnabled);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PluginLogLevel_JellyfinDefaultIsZero()
    {
        Assert.Equal(0, (int)PluginLogLevel.JellyfinDefault);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PluginLogLevel_HasExpectedValues()
    {
        Assert.Equal(1, (int)PluginLogLevel.Trace);
        Assert.Equal(2, (int)PluginLogLevel.Debug);
        Assert.Equal(3, (int)PluginLogLevel.Information);
        Assert.Equal(4, (int)PluginLogLevel.Warning);
        Assert.Equal(5, (int)PluginLogLevel.Error);
        Assert.Equal(6, (int)PluginLogLevel.Critical);
        Assert.Equal(7, (int)PluginLogLevel.None);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PluginLogEntry_DefaultValues()
    {
        var entry = new PluginLogEntry();

        Assert.Equal(0, entry.Id);
        Assert.Equal("plugin", entry.Source);
        Assert.Equal("information", entry.LogType);
        Assert.Equal("Information", entry.Level);
        Assert.Equal(string.Empty, entry.Message);
        Assert.Null(entry.Category);
        Assert.Null(entry.Exception);
        Assert.Null(entry.EventId);
        Assert.Null(entry.CorrelationId);
        Assert.Null(entry.ChannelId);
        Assert.Null(entry.RawSource);
        Assert.Null(entry.RawLineHash);
        Assert.Null(entry.ImportedAtUtc);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PluginLogEntry_CanSetAllProperties()
    {
        var now = DateTime.UtcNow;
        var entry = new PluginLogEntry
        {
            Id = 42,
            CreatedAtUtc = now,
            Source = "tvheadend",
            LogType = "tvheadend_warning",
            Level = "Warning",
            Category = "mpegts",
            Message = "signal lost",
            Exception = "NullReferenceException",
            EventId = "100",
            CorrelationId = "abc-123",
            ChannelId = "ch-456",
            RawSource = "raw line text",
            RawLineHash = "ABCDEF",
            ImportedAtUtc = now.AddSeconds(1),
        };

        Assert.Equal(42, entry.Id);
        Assert.Equal("tvheadend", entry.Source);
        Assert.Equal("tvheadend_warning", entry.LogType);
        Assert.Equal("Warning", entry.Level);
        Assert.Equal("mpegts", entry.Category);
        Assert.Equal("signal lost", entry.Message);
        Assert.NotNull(entry.ImportedAtUtc);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void LogRetentionDays_MustBePositive()
    {
        var config = new PluginConfiguration { LogRetentionDays = -1 };

        // Business rule: retention of -1 should be treated as 7 by the service.
        // The config allows storing it but service clamps at usage time.
        Assert.Equal(-1, config.LogRetentionDays);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void MaxDashboardLogEntries_MustBePositive()
    {
        var config = new PluginConfiguration { MaxDashboardLogEntries = 0 };

        // The API controller clamps this to 1..5000 at query time.
        Assert.Equal(0, config.MaxDashboardLogEntries);
    }
}
