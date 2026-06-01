using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Logging;

/// <summary>
/// Tests for <see cref="PluginLogLevelPolicy"/> — the plugin-specific log-level override
/// applied by <see cref="PluginLogPersistenceProvider"/> when persisting plugin log entries.
/// </summary>
public class PluginLogLevelOverrideTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void JellyfinDefault_PersistsEverythingDelivered()
    {
        // JellyfinDefault means: persist whatever Jellyfin's framework already delivered (no extra filter).
        Assert.True(PluginLogLevelPolicy.ShouldPersist(LogLevel.Trace, PluginLogLevel.JellyfinDefault));
        Assert.True(PluginLogLevelPolicy.ShouldPersist(LogLevel.Debug, PluginLogLevel.JellyfinDefault));
        Assert.True(PluginLogLevelPolicy.ShouldPersist(LogLevel.Information, PluginLogLevel.JellyfinDefault));
        Assert.True(PluginLogLevelPolicy.ShouldPersist(LogLevel.Critical, PluginLogLevel.JellyfinDefault));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DebugOverride_AllowsDebugAndAbove_SuppressesTrace()
    {
        Assert.False(PluginLogLevelPolicy.ShouldPersist(LogLevel.Trace, PluginLogLevel.Debug));
        Assert.True(PluginLogLevelPolicy.ShouldPersist(LogLevel.Debug, PluginLogLevel.Debug));
        Assert.True(PluginLogLevelPolicy.ShouldPersist(LogLevel.Information, PluginLogLevel.Debug));
        Assert.True(PluginLogLevelPolicy.ShouldPersist(LogLevel.Warning, PluginLogLevel.Debug));
        Assert.True(PluginLogLevelPolicy.ShouldPersist(LogLevel.Error, PluginLogLevel.Debug));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void WarningOverride_SuppressesInformationAndDebug()
    {
        Assert.False(PluginLogLevelPolicy.ShouldPersist(LogLevel.Debug, PluginLogLevel.Warning));
        Assert.False(PluginLogLevelPolicy.ShouldPersist(LogLevel.Information, PluginLogLevel.Warning));
        Assert.True(PluginLogLevelPolicy.ShouldPersist(LogLevel.Warning, PluginLogLevel.Warning));
        Assert.True(PluginLogLevelPolicy.ShouldPersist(LogLevel.Error, PluginLogLevel.Warning));
        Assert.True(PluginLogLevelPolicy.ShouldPersist(LogLevel.Critical, PluginLogLevel.Warning));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ErrorOverride_AllowsErrorAndCritical()
    {
        Assert.False(PluginLogLevelPolicy.ShouldPersist(LogLevel.Warning, PluginLogLevel.Error));
        Assert.True(PluginLogLevelPolicy.ShouldPersist(LogLevel.Error, PluginLogLevel.Error));
        Assert.True(PluginLogLevelPolicy.ShouldPersist(LogLevel.Critical, PluginLogLevel.Error));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void NoneOverride_SuppressesAll()
    {
        Assert.False(PluginLogLevelPolicy.ShouldPersist(LogLevel.Trace, PluginLogLevel.None));
        Assert.False(PluginLogLevelPolicy.ShouldPersist(LogLevel.Debug, PluginLogLevel.None));
        Assert.False(PluginLogLevelPolicy.ShouldPersist(LogLevel.Information, PluginLogLevel.None));
        Assert.False(PluginLogLevelPolicy.ShouldPersist(LogLevel.Warning, PluginLogLevel.None));
        Assert.False(PluginLogLevelPolicy.ShouldPersist(LogLevel.Error, PluginLogLevel.None));
        Assert.False(PluginLogLevelPolicy.ShouldPersist(LogLevel.Critical, PluginLogLevel.None));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void LogLevelNone_IsNeverPersisted()
    {
        Assert.False(PluginLogLevelPolicy.ShouldPersist(LogLevel.None, PluginLogLevel.JellyfinDefault));
        Assert.False(PluginLogLevelPolicy.ShouldPersist(LogLevel.None, PluginLogLevel.Trace));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(PluginLogLevel.Trace, LogLevel.Trace)]
    [InlineData(PluginLogLevel.Debug, LogLevel.Debug)]
    [InlineData(PluginLogLevel.Information, LogLevel.Information)]
    [InlineData(PluginLogLevel.Warning, LogLevel.Warning)]
    [InlineData(PluginLogLevel.Error, LogLevel.Error)]
    [InlineData(PluginLogLevel.Critical, LogLevel.Critical)]
    [InlineData(PluginLogLevel.None, LogLevel.None)]
    public void MapToLogLevel_MapsEachLevel(PluginLogLevel input, LogLevel expected)
    {
        Assert.Equal(expected, PluginLogLevelPolicy.MapToLogLevel(input));
    }
}
