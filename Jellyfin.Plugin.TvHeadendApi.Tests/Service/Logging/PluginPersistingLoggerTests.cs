// Tests for PluginPersistingLogger — the transparent ILogger<> decorator that also persists
// plugin-namespace entries. The safety-critical guarantees: it ALWAYS forwards to the real logger
// (so host logging is never altered) and NEVER throws (so a persistence problem can't break logging).

using System;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Logging;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Logging;

public class PluginPersistingLoggerTests
{
    private static (PluginPersistingLogger<T> Logger, Mock<ILogger> Inner) Create<T>()
    {
        var inner = new Mock<ILogger>();
        var factory = new Mock<ILoggerFactory>();
        factory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(inner.Object);
        var configProvider = new ConfigurationProvider(() => new PluginConfiguration());
        // Service provider returns null for PluginLogService — exercises the not-ready path.
        var sp = new Mock<IServiceProvider>();
        sp.Setup(s => s.GetService(typeof(PluginLogService))).Returns(null!);
        return (new PluginPersistingLogger<T>(factory.Object, configProvider, sp.Object), inner);
    }

    private static void VerifyForwarded(Mock<ILogger> inner, LogLevel level)
    {
        inner.Verify(
            l => l.Log(level, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void HostCategory_ForwardsToInner_AndDoesNotThrow()
    {
        // System.String is NOT a plugin namespace — must forward, never persist, never throw.
        var (logger, inner) = Create<string>();

        logger.Log(LogLevel.Information, new EventId(0), "host message", null, (s, _) => s);

        VerifyForwarded(inner, LogLevel.Information);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PluginCategory_ForwardsToInner_AndDoesNotThrowWhenLogServiceUnavailable()
    {
        // A plugin-namespace type — persistence is attempted but the service is null, so it must
        // still forward and must not throw.
        var (logger, inner) = Create<PluginLogLevelPolicyMarker>();

        logger.Log(LogLevel.Warning, new EventId(0), "plugin message", null, (s, _) => s);

        VerifyForwarded(inner, LogLevel.Warning);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsEnabled_And_BeginScope_DelegateToInner()
    {
        var (logger, inner) = Create<string>();
        inner.Setup(l => l.IsEnabled(LogLevel.Debug)).Returns(true);

        Assert.True(logger.IsEnabled(LogLevel.Debug));
        inner.Verify(l => l.IsEnabled(LogLevel.Debug), Times.Once);

        using (logger.BeginScope("scope"))
        {
        }

        inner.Verify(l => l.BeginScope(It.IsAny<string>()), Times.Once);
    }

    // A type that lives in the plugin namespace so its FullName triggers the plugin-category path.
    private sealed class PluginLogLevelPolicyMarker
    {
    }
}
