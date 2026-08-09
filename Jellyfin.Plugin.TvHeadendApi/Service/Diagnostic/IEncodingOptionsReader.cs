using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;

/// <summary>
/// Reads Jellyfin encoding settings that affect FFmpeg startup behavior.
/// </summary>
internal interface IEncodingOptionsReader
{
    (string? ProbeSize, string? AnalyzeDuration) ReadFfmpegSettings(
        IServerConfigurationManager serverConfigManager,
        ILogger logger);
}
