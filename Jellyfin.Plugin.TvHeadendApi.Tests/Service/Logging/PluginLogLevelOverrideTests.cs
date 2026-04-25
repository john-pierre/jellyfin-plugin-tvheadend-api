using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Jellyfin.Plugin.TvHeadendApi.Service.Logging;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Logging;

/// <summary>
/// Tests for <see cref="PluginScopedLogger"/> and log level override behavior.
/// </summary>
public class PluginLogLevelOverrideTests
{
    private readonly Mock<ILogger> _innerLogger;
    private readonly PluginConfigurationProvider _configProvider;
    private readonly PluginConfiguration _config;

    public PluginLogLevelOverrideTests()
    {
        _innerLogger = new Mock<ILogger>();
        _config = new PluginConfiguration();
        _configProvider = new PluginConfigurationProvider(() => _config);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void JellyfinDefault_DelegatesToInnerLogger()
    {
        _config.PluginLogLevelOverride = PluginLogLevel.JellyfinDefault;
        _innerLogger.Setup(l => l.IsEnabled(LogLevel.Debug)).Returns(false);
        _innerLogger.Setup(l => l.IsEnabled(LogLevel.Information)).Returns(true);

        var logger = new PluginScopedLogger(_innerLogger.Object, _configProvider, null, "Test");

        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.True(logger.IsEnabled(LogLevel.Information));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DebugOverride_AllowsDebugLogs()
    {
        _config.PluginLogLevelOverride = PluginLogLevel.Debug;
        _innerLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(false);

        var logger = new PluginScopedLogger(_innerLogger.Object, _configProvider, null, "Test");

        Assert.True(logger.IsEnabled(LogLevel.Debug));
        Assert.True(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
        Assert.True(logger.IsEnabled(LogLevel.Error));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DebugOverride_SuppressesTrace()
    {
        _config.PluginLogLevelOverride = PluginLogLevel.Debug;

        var logger = new PluginScopedLogger(_innerLogger.Object, _configProvider, null, "Test");

        Assert.False(logger.IsEnabled(LogLevel.Trace));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void WarningOverride_SuppressesInformationAndDebug()
    {
        _config.PluginLogLevelOverride = PluginLogLevel.Warning;

        var logger = new PluginScopedLogger(_innerLogger.Object, _configProvider, null, "Test");

        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
        Assert.True(logger.IsEnabled(LogLevel.Error));
        Assert.True(logger.IsEnabled(LogLevel.Critical));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void NoneOverride_SuppressesAll()
    {
        _config.PluginLogLevelOverride = PluginLogLevel.None;

        var logger = new PluginScopedLogger(_innerLogger.Object, _configProvider, null, "Test");

        Assert.False(logger.IsEnabled(LogLevel.Trace));
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.False(logger.IsEnabled(LogLevel.Warning));
        Assert.False(logger.IsEnabled(LogLevel.Error));
        Assert.False(logger.IsEnabled(LogLevel.Critical));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ErrorOverride_AllowsErrorAndCritical()
    {
        _config.PluginLogLevelOverride = PluginLogLevel.Error;

        var logger = new PluginScopedLogger(_innerLogger.Object, _configProvider, null, "Test");

        Assert.False(logger.IsEnabled(LogLevel.Warning));
        Assert.True(logger.IsEnabled(LogLevel.Error));
        Assert.True(logger.IsEnabled(LogLevel.Critical));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Log_WhenDisabled_DoesNotCallInner()
    {
        _config.PluginLogLevelOverride = PluginLogLevel.Error;

        var logger = new PluginScopedLogger(_innerLogger.Object, _configProvider, null, "Test");
        logger.Log(LogLevel.Debug, new EventId(0), "test", null, (s, _) => s);

        _innerLogger.Verify(
            l => l.Log(It.IsAny<LogLevel>(), It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<System.Exception>(), It.IsAny<System.Func<It.IsAnyType, System.Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Log_WhenEnabled_CallsInner()
    {
        _config.PluginLogLevelOverride = PluginLogLevel.Debug;
        _innerLogger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var logger = new PluginScopedLogger(_innerLogger.Object, _configProvider, null, "Test");
        logger.Log(LogLevel.Error, new EventId(1), "error message", null, (s, _) => s);

        _innerLogger.Verify(
            l => l.Log(LogLevel.Error, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), null, It.IsAny<System.Func<It.IsAnyType, System.Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void JellyfinDefault_DoesNotForceDebug()
    {
        // When JellyfinDefault is set, inner logger determines the level
        _config.PluginLogLevelOverride = PluginLogLevel.JellyfinDefault;
        _innerLogger.Setup(l => l.IsEnabled(LogLevel.Debug)).Returns(false);

        var logger = new PluginScopedLogger(_innerLogger.Object, _configProvider, null, "Test");

        // Should NOT force debug logs
        Assert.False(logger.IsEnabled(LogLevel.Debug));
    }
}

