using System;
using Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Diagnostic;

public class EncodingOptionsReaderTests
{
    [Fact]
    public void ReadFfmpegSettings_WithNullServerConfigManager_Throws()
    {
        var sut = new EncodingOptionsReader();

        Assert.Throws<ArgumentNullException>(() =>
            sut.ReadFfmpegSettings(null!, NullLogger.Instance));
    }

    [Fact]
    public void ReadFfmpegSettings_WithNullLogger_Throws()
    {
        var sut = new EncodingOptionsReader();
        var serverConfigManager = new Mock<IServerConfigurationManager>();

        Assert.Throws<ArgumentNullException>(() =>
            sut.ReadFfmpegSettings(serverConfigManager.Object, null!));
    }

    [Fact]
    public void ReadFfmpegSettings_ReadsValuesFromEncodingConfiguration()
    {
        var sut = new EncodingOptionsReader();
        var serverConfigManager = new Mock<IServerConfigurationManager>();
        serverConfigManager
            .Setup(x => x.GetConfiguration("encoding"))
            .Returns(new EncodingConfigStub
            {
                FFmpegProbeSize = "1500000",
                FFmpegAnalyzeDuration = "2500000"
            });

        var (probeSize, analyzeDuration) = sut.ReadFfmpegSettings(serverConfigManager.Object, NullLogger.Instance);

        Assert.Equal("1500000", probeSize);
        Assert.Equal("2500000", analyzeDuration);
    }

    [Fact]
    public void ReadFfmpegSettings_WhenConfigThrows_UsesEnvironmentFallback()
    {
        var originalProbe = Environment.GetEnvironmentVariable("JELLYFIN_FFmpeg__ProbeSize");
        var originalAnalyze = Environment.GetEnvironmentVariable("JELLYFIN_FFmpeg__AnalyzeDuration");

        try
        {
            Environment.SetEnvironmentVariable("JELLYFIN_FFmpeg__ProbeSize", "777777");
            Environment.SetEnvironmentVariable("JELLYFIN_FFmpeg__AnalyzeDuration", "888888");

            var sut = new EncodingOptionsReader();
            var serverConfigManager = new Mock<IServerConfigurationManager>();
            serverConfigManager.Setup(x => x.GetConfiguration("encoding")).Throws(new InvalidOperationException("boom"));

            var (probeSize, analyzeDuration) = sut.ReadFfmpegSettings(serverConfigManager.Object, NullLogger.Instance);

            Assert.Equal("777777", probeSize);
            Assert.Equal("888888", analyzeDuration);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JELLYFIN_FFmpeg__ProbeSize", originalProbe);
            Environment.SetEnvironmentVariable("JELLYFIN_FFmpeg__AnalyzeDuration", originalAnalyze);
        }
    }

    private sealed class EncodingConfigStub
    {
        public string FFmpegProbeSize { get; init; } = string.Empty;

        public string FFmpegAnalyzeDuration { get; init; } = string.Empty;
    }
}
