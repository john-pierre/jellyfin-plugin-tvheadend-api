using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Profile;

/// <summary>
/// Provides TVHeadend profile and container mapping helpers shared across services.
/// </summary>
internal static class ProfileMappingHelper
{
    /// <summary>
    /// Maps TVHeadend container values (numeric enum or string) to FFmpeg container names.
    /// </summary>
    /// <param name="raw">Raw container value from TVHeadend profile data.</param>
    /// <returns>Normalized container name used by FFmpeg/Jellyfin.</returns>
    public static string MapContainer(string raw)
    {
        return raw.ToLowerInvariant() switch
        {
            "0" or "" or "not set" => string.Empty,
            "1" or "matroska" or "mkv" => "matroska",
            "2" or "mpegts" or "ts" => "mpegts",
            "3" or "mpegps" or "ps" => "mpegps",
            // MC_PASS (4) is pass-through — no re-mux container; treat as mpegts
            "4" or "pass" => "mpegts",
            // MC_AVMP4 (9) is the libav MP4 muxer
            "9" or "mp4" => "mp4",
            _ => raw.ToLowerInvariant()
        };
    }

    /// <summary>
    /// Derives the container format from a TVHeadend profile class for non-transcode profiles.
    /// </summary>
    /// <param name="profileClass">TVHeadend profile class string.</param>
    /// <returns>Derived output container name.</returns>
    public static string MapProfileClassToContainer(string profileClass)
    {
        return profileClass.ToLowerInvariant() switch
        {
            "profile-matroska" => "matroska",
            "profile-mpegts" or "profile-mpegts-pass" or "profile-mpegts-spawn" => "mpegts",
            "profile-htsp" => "mpegts",
            _ => "mpegts"
        };
    }

    /// <summary>
    /// Normalizes a codec profile title by stripping the parenthesized codec class suffix.
    /// For example, "libx264 (H.264)" becomes "libx264".
    /// </summary>
    /// <param name="title">The raw codec profile title from TVHeadend.</param>
    /// <returns>The normalized title without the parenthesized suffix, or <see cref="string.Empty"/> if input is null/whitespace.</returns>
    public static string NormalizeCodecProfileTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var separatorIndex = title.IndexOf(" (", StringComparison.Ordinal);
        return separatorIndex > 0 ? title[..separatorIndex] : title;
    }

    /// <summary>
    /// Finds a codec profile entry from the TVHeadend codec profile list by UUID, title, or normalized title.
    /// </summary>
    /// <param name="apiClient">The API client for HTTP communication.</param>
    /// <param name="httpClient">The configured HTTP client.</param>
    /// <param name="baseUrl">TVHeadend base URL.</param>
    /// <param name="webRoot">TVHeadend web root path.</param>
    /// <param name="profileReference">UUID or title to search for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching entry or <c>null</c> if not found.</returns>
    public static async Task<CodecProfileMatch?> FindCodecProfileEntryByReferenceAsync(
        IApiClient apiClient,
        HttpClient httpClient,
        string baseUrl,
        string webRoot,
        string profileReference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileReference))
        {
            return null;
        }

        var listUrl = $"{baseUrl}{webRoot}api/codec_profile/list";
        var response = await apiClient.GetStringAsync(httpClient, listUrl, cancellationToken).ConfigureAwait(false);
        var list = JsonSerializer.Deserialize<CodecProfileListResponse>(response, JsonDefaults.Api);
        if (list?.Entries == null || list.Entries.Length == 0)
        {
            return null;
        }

        foreach (var entry in list.Entries)
        {
            var uuid = entry.EffectiveUuid;
            var entryTitle = entry.EffectiveTitle;
            var normalizedTitle = NormalizeCodecProfileTitle(entryTitle);

            if (string.Equals(profileReference, uuid, StringComparison.OrdinalIgnoreCase)
                || string.Equals(profileReference, entryTitle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(profileReference, normalizedTitle, StringComparison.OrdinalIgnoreCase))
            {
                return new CodecProfileMatch(uuid, string.IsNullOrWhiteSpace(normalizedTitle) ? entryTitle : normalizedTitle);
            }
        }

        return null;
    }

    /// <summary>
    /// Represents a matched codec profile with its UUID and display name.
    /// </summary>
    /// <param name="Key">The codec profile UUID.</param>
    /// <param name="Val">The normalized display name.</param>
    public sealed record CodecProfileMatch(string Key, string Val);
}
