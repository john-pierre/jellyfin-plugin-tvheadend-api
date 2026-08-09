using System;
using System.IO;
using Jellyfin.Plugin.TvHeadendApi;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

/// <summary>
/// Tests for <see cref="Plugin"/> covering constructor null guards.
/// Note: Creating a Plugin instance sets a static singleton (Plugin.Instance),
/// which can interfere with parallel tests. Only null-guard tests are safe here.
/// </summary>
public class PluginTests
{
    private static IApplicationPaths CreatePaths()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "jellyfin_plugin_tests");
        Directory.CreateDirectory(tempDir);
        var mock = new Mock<IApplicationPaths>();
        mock.Setup(p => p.PluginConfigurationsPath).Returns(tempDir);
        mock.Setup(p => p.CachePath).Returns(Path.Combine(tempDir, "cache"));
        mock.Setup(p => p.DataPath).Returns(tempDir);
        mock.Setup(p => p.PluginsPath).Returns(tempDir);
        return mock.Object;
    }

    [Fact]
    public void Constructor_NullApplicationHost_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new Plugin(
            CreatePaths(),
            Mock.Of<IXmlSerializer>(),
            null!,
            NullLogger<Plugin>.Instance));
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new Plugin(
            CreatePaths(),
            Mock.Of<IXmlSerializer>(),
            Mock.Of<IServerApplicationHost>(),
            null!));
    }
}
