// Tests for PluginLoggerFactory and PluginScopedLogger — log level override and persistence.

using System;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service;

public class PluginLoggerFactoryTests
{
    [Fact]
    public void CreateLogger_ReturnsNonNull()
    {
        var factory = new PluginLoggerFactory(
            NullLoggerFactory.Instance,
            new PluginConfigurationProvider(() => new PluginConfiguration()));

        var logger = factory.CreateLogger("TestCategory");
        Assert.NotNull(logger);
    }

    [Fact]
    public void CreateLoggerGeneric_ReturnsNonNull()
    {
        var factory = new PluginLoggerFactory(
            NullLoggerFactory.Instance,
            new PluginConfigurationProvider(() => new PluginConfiguration()));

        var logger = factory.CreateLogger<PluginLoggerFactoryTests>();
        Assert.NotNull(logger);
    }

    [Fact]
    public void Constructor_ThrowsOnNullFactory()
    {
        Assert.Throws<ArgumentNullException>(() => new PluginLoggerFactory(
            null!,
            new PluginConfigurationProvider(() => new PluginConfiguration())));
    }

    [Fact]
    public void Constructor_ThrowsOnNullConfigProvider()
    {
        Assert.Throws<ArgumentNullException>(() => new PluginLoggerFactory(
            NullLoggerFactory.Instance,
            null!));
    }

    [Fact]
    public void IsEnabled_WithJellyfinDefault_DelegatesToInner()
    {
        var config = new PluginConfiguration { PluginLogLevelOverride = PluginLogLevel.JellyfinDefault };
        var factory = new PluginLoggerFactory(
            NullLoggerFactory.Instance,
            new PluginConfigurationProvider(() => config));

        var logger = factory.CreateLogger("Test");
        // NullLogger is always disabled for Trace but enabled for None behavior
        // The point is it delegates to inner, which for NullLogger returns false
        Assert.False(logger.IsEnabled(LogLevel.Trace));
    }

    [Fact]
    public void IsEnabled_WithDebugOverride_FiltersCorrectly()
    {
        var config = new PluginConfiguration { PluginLogLevelOverride = PluginLogLevel.Debug };
        var factory = new PluginLoggerFactory(
            NullLoggerFactory.Instance,
            new PluginConfigurationProvider(() => config));

        var logger = factory.CreateLogger("Test");
        Assert.False(logger.IsEnabled(LogLevel.Trace));
        Assert.True(logger.IsEnabled(LogLevel.Debug));
        Assert.True(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
        Assert.True(logger.IsEnabled(LogLevel.Error));
    }

    [Fact]
    public void IsEnabled_WithErrorOverride_FiltersLowerLevels()
    {
        var config = new PluginConfiguration { PluginLogLevelOverride = PluginLogLevel.Error };
        var factory = new PluginLoggerFactory(
            NullLoggerFactory.Instance,
            new PluginConfigurationProvider(() => config));

        var logger = factory.CreateLogger("Test");
        Assert.False(logger.IsEnabled(LogLevel.Trace));
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.False(logger.IsEnabled(LogLevel.Warning));
        Assert.True(logger.IsEnabled(LogLevel.Error));
        Assert.True(logger.IsEnabled(LogLevel.Critical));
    }

    [Fact]
    public void Log_Disabled_DoesNotCallInner()
    {
        var config = new PluginConfiguration { PluginLogLevelOverride = PluginLogLevel.Error };
        var innerLogger = new Mock<ILogger>();
        innerLogger.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var innerFactory = new Mock<ILoggerFactory>();
        innerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(innerLogger.Object);

        var factory = new PluginLoggerFactory(
            innerFactory.Object,
            new PluginConfigurationProvider(() => config));

        var logger = factory.CreateLogger("Test");
        logger.LogDebug("Should not be logged");

        // With Error override, Debug is disabled — inner.Log should NOT be called
        innerLogger.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Fact]
    public void Log_Enabled_CallsInner()
    {
        var config = new PluginConfiguration { PluginLogLevelOverride = PluginLogLevel.Debug };
        var innerLogger = new Mock<ILogger>();
        innerLogger.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var innerFactory = new Mock<ILoggerFactory>();
        innerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(innerLogger.Object);

        var factory = new PluginLoggerFactory(
            innerFactory.Object,
            new PluginConfigurationProvider(() => config));

        var logger = factory.CreateLogger("Test");
        logger.LogError("Should be logged");

        innerLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void BeginScope_DelegatesToInner()
    {
        var factory = new PluginLoggerFactory(
            NullLoggerFactory.Instance,
            new PluginConfigurationProvider(() => new PluginConfiguration()));

        var logger = factory.CreateLogger("Test");
        var scope = logger.BeginScope("test-scope");
        // NullLogger returns NullScope — just verify no exception
        Assert.NotNull(scope);
    }

    [Fact]
    public void TypedLogger_IsEnabled_Works()
    {
        var config = new PluginConfiguration { PluginLogLevelOverride = PluginLogLevel.Warning };
        var factory = new PluginLoggerFactory(
            NullLoggerFactory.Instance,
            new PluginConfigurationProvider(() => config));

        var logger = factory.CreateLogger<PluginLoggerFactoryTests>();
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
    }
}

