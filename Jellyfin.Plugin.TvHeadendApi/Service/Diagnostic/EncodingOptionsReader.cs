using System;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;

/// <summary>
/// DI wrapper for reading Jellyfin encoding settings.
/// </summary>
internal sealed class EncodingOptionsReader : IEncodingOptionsReader
{
    public (string? ProbeSize, string? AnalyzeDuration) ReadFfmpegSettings(
        IServerConfigurationManager serverConfigManager,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(serverConfigManager);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            var encoding = serverConfigManager.GetConfiguration("encoding");
            var type = encoding.GetType();

            var probeSize = type.GetProperty("FFmpegProbeSize")?.GetValue(encoding) as string;
            var analyzeDuration = type.GetProperty("FFmpegAnalyzeDuration")?.GetValue(encoding) as string;

            // Fallback to environment variables if not set in config.
            probeSize ??= Environment.GetEnvironmentVariable("JELLYFIN_FFmpeg__ProbeSize");
            analyzeDuration ??= Environment.GetEnvironmentVariable("JELLYFIN_FFmpeg__AnalyzeDuration");

            return (probeSize, analyzeDuration);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read Jellyfin's encoding options via reflection.");

            // Still try environment variables as fallback.
            var probeSize = Environment.GetEnvironmentVariable("JELLYFIN_FFmpeg__ProbeSize");
            var analyzeDuration = Environment.GetEnvironmentVariable("JELLYFIN_FFmpeg__AnalyzeDuration");
            return (probeSize, analyzeDuration);
        }
    }
}
