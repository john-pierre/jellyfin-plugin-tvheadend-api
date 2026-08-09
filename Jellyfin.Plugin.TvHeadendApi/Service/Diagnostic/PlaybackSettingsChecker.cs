using System.Collections.Generic;
using Jellyfin.Plugin.TvHeadendApi.Model.Diagnostic;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic;

/// <summary>
/// Validates playback-related configuration settings and transcode profile codec links.
/// </summary>
internal static class PlaybackSettingsChecker
{
    /// <summary>
    /// Checks playback settings (AnalyzeDuration, Buffer, DirectPlay flags) and adds results to the report.
    /// </summary>
    /// <param name="report">The diagnostic report to populate.</param>
    /// <param name="supportsDirectPlay">Whether direct play is enabled.</param>
    /// <param name="supportsDirectStream">Whether direct stream is enabled.</param>
    /// <param name="supportsTranscoding">Whether transcoding is enabled.</param>
    /// <param name="supportsProbing">Whether probing is enabled.</param>
    /// <param name="analyzeDurationMs">The configured analyze duration in milliseconds.</param>
    /// <param name="bufferMs">The configured buffer in milliseconds.</param>
    /// <returns>The total score deduction.</returns>
    internal static int Check(
        DiagnoseResult report,
        bool supportsDirectPlay,
        bool supportsDirectStream,
        bool supportsTranscoding,
        bool supportsProbing,
        int analyzeDurationMs,
        int bufferMs)
    {
        var scoreDeductions = 0;

        report.Checks.Add(new DiagnoseCheck { Category = "Playback", Name = "Playback Mode", Status = "OK", Message = $"DirectPlay={supportsDirectPlay}, DirectStream={supportsDirectStream}, Transcoding={supportsTranscoding}" });

        if (analyzeDurationMs > 1000)
        {
            report.Checks.Add(new DiagnoseCheck
            {
                Category = "Playback",
                Name = "AnalyzeDuration",
                Status = "WARNING",
                Message = $"AnalyzeDuration is {analyzeDurationMs}ms ({analyzeDurationMs * 1000}us) which is very high.",
                Recommendation = "Consider 200ms or less for faster channel switching."
            });
            scoreDeductions += 15;
        }
        else if (analyzeDurationMs > 0 && analyzeDurationMs < 50)
        {
            report.Checks.Add(new DiagnoseCheck
            {
                Category = "Playback",
                Name = "AnalyzeDuration",
                Status = "WARNING",
                Message = $"AnalyzeDuration is {analyzeDurationMs}ms which is very low.",
                Recommendation = "FFmpeg may not detect all streams. Recommended minimum: 100ms."
            });
            scoreDeductions += 5;
        }
        else
        {
            var effectiveMs = analyzeDurationMs > 0 ? analyzeDurationMs : 200;
            report.Checks.Add(new DiagnoseCheck { Category = "Playback", Name = "AnalyzeDuration", Status = "OK", Message = $"Effective AnalyzeDuration: {effectiveMs}ms (good for fast channel switching)." });
        }

        if (!supportsProbing && analyzeDurationMs == 0)
        {
            report.Recommendations.Add("Probing is off and AnalyzeDuration is 0. This legacy auto mode falls back to 200ms when TVHeadend stream details are available. Consider setting an explicit AnalyzeDuration of 200ms to match the current default.");
        }

        if (bufferMs > 2000)
        {
            report.Checks.Add(new DiagnoseCheck
            {
                Category = "Playback",
                Name = "Buffer Size",
                Status = "WARNING",
                Message = $"Buffer is {bufferMs}ms which is quite high.",
                Recommendation = "Lower the buffer to reduce channel-switch latency."
            });
            scoreDeductions += 5;
        }
        else
        {
            report.Checks.Add(new DiagnoseCheck { Category = "Playback", Name = "Buffer Size", Status = "OK", Message = $"Buffer: {(bufferMs > 0 ? $"{bufferMs}ms" : "Jellyfin default")}" });
        }

        return scoreDeductions;
    }

    /// <summary>
    /// Checks transcode profile codec links, deinterlacing, and source codec filters.
    /// </summary>
    /// <param name="report">The diagnostic report to populate.</param>
    /// <param name="proVideoCodec">The linked video codec profile reference.</param>
    /// <param name="proAudioCodec">The linked audio codec profile reference.</param>
    /// <param name="srcVideoCodecs">Source video codec filter list.</param>
    /// <param name="srcAudioCodecs">Source audio codec filter list.</param>
    /// <param name="deinterlace">Whether deinterlacing is enabled.</param>
    /// <returns>The total score deduction.</returns>
    internal static int CheckTranscodeProfile(DiagnoseResult report, string? proVideoCodec, string? proAudioCodec, IReadOnlyList<string> srcVideoCodecs, IReadOnlyList<string> srcAudioCodecs, bool? deinterlace)
    {
        var scoreDeductions = 0;
        report.Checks.Add(new DiagnoseCheck { Category = "Streaming", Name = "Profile Type", Status = "OK", Message = "Transcode profile detected. Output format is fixed per channel." });

        if (!string.IsNullOrWhiteSpace(proVideoCodec) && !string.IsNullOrWhiteSpace(proAudioCodec))
        {
            report.Checks.Add(new DiagnoseCheck { Category = "Streaming", Name = "Codec Profiles", Status = "OK", Message = $"Video: {proVideoCodec}, Audio: {proAudioCodec}" });
        }
        else
        {
            if (string.IsNullOrWhiteSpace(proVideoCodec))
            {
                report.Checks.Add(new DiagnoseCheck { Category = "Streaming", Name = "Video Codec Link", Status = "WARNING", Message = "No linked video codec profile (pro_vcodec / vcodec).", Recommendation = "Link a video codec profile in TVHeadend for consistent output." });
                scoreDeductions += 5;
            }

            if (string.IsNullOrWhiteSpace(proAudioCodec))
            {
                report.Checks.Add(new DiagnoseCheck { Category = "Streaming", Name = "Audio Codec Link", Status = "WARNING", Message = "No linked audio codec profile (pro_acodec / acodec).", Recommendation = "Link an audio codec profile in TVHeadend for consistent output." });
                scoreDeductions += 5;
            }
        }

        if (deinterlace == true)
        {
            report.Checks.Add(new DiagnoseCheck { Category = "Streaming", Name = "Deinterlacing", Status = "OK", Message = "Deinterlacing is enabled." });
        }
        else
        {
            report.Checks.Add(new DiagnoseCheck { Category = "Streaming", Name = "Deinterlacing", Status = "WARNING", Message = "Deinterlacing is not enabled.", Recommendation = "Enable deinterlacing in the TVHeadend video codec profile so clients can more often direct play." });
            scoreDeductions += 5;
        }

        if (srcVideoCodecs.Count == 0)
        {
            report.Checks.Add(new DiagnoseCheck { Category = "Streaming", Name = "Source Video Codecs", Status = "INFO", Message = "No source video codec filter set (all codecs accepted)." });
        }

        if (srcAudioCodecs.Count == 0)
        {
            report.Checks.Add(new DiagnoseCheck { Category = "Streaming", Name = "Source Audio Codecs", Status = "INFO", Message = "No source audio codec filter set (all codecs accepted)." });
        }

        return scoreDeductions;
    }
}
