using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Profile;

/// <summary>
/// Loads stream profile references and detailed profile metadata from TVHeadend APIs.
/// </summary>
internal sealed class ProfileResolver : IProfileResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IApiClient _tvheadendApiClient;

    public ProfileResolver(
        IApiClient tvheadendApiClient)
    {
        _tvheadendApiClient = tvheadendApiClient ?? throw new ArgumentNullException(nameof(tvheadendApiClient));
    }

    public async Task<IReadOnlyList<ProfileReference>> GetProfilesAsync(
        HttpClient httpClient,
        string baseUrl,
        string webRoot,
        CancellationToken cancellationToken)
    {
        var listUrl = $"{baseUrl}{webRoot}api/profile/list";
        var listBody = await _tvheadendApiClient.GetStringAsync(httpClient, listUrl, cancellationToken).ConfigureAwait(false);

        var listResponse = JsonSerializer.Deserialize<ProfileListResponse>(listBody, JsonOptions);
        if (listResponse?.Entries == null || listResponse.Entries.Length == 0)
        {
            return Array.Empty<ProfileReference>();
        }

        var profiles = new List<ProfileReference>();
        foreach (var entry in listResponse.Entries)
        {
            var key = entry.Key;
            var name = entry.Val;
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            profiles.Add(new ProfileReference(key, name));
        }

        return profiles;
    }

    public async Task<ProfileDetails?> GetProfileDetailsByUuidAsync(
        HttpClient httpClient,
        string baseUrl,
        string webRoot,
        string profileUuid,
        string profileName,
        CancellationToken cancellationToken)
    {
        using var profileDoc = await LoadIdNodeByUuidAsync(httpClient, baseUrl, webRoot, profileUuid, cancellationToken).ConfigureAwait(false);
        var profileResponse = JsonSerializer.Deserialize<IdNodeLoadResponse>(profileDoc.RootElement.GetRawText(), JsonOptions);
        if (profileResponse?.Entries == null || profileResponse.Entries.Length == 0)
        {
            return null;
        }

        var entry = profileResponse.Entries[0];
        var profileClass = IdNodeValueHelper.ReadStringOrParam(entry.ProfileClass, entry.Params, "class") ?? string.Empty;
        var rawContainer = IdNodeValueHelper.ReadStringOrParam(entry.Container, entry.Params, "container")
            ?? IdNodeValueHelper.ReadIntOrParam(entry.Container, entry.Params, "container")?.ToString(CultureInfo.InvariantCulture)
            ?? string.Empty;

        var mappedContainer = ProfileMappingHelper.MapContainer(rawContainer);
        if (string.IsNullOrWhiteSpace(mappedContainer))
        {
            mappedContainer = ProfileMappingHelper.MapProfileClassToContainer(profileClass);
        }

        var proVideoCodec = IdNodeValueHelper.ReadStringOrParam(entry.ProVideoCodec, entry.Params, "pro_vcodec")
            ?? IdNodeValueHelper.ReadStringOrParam(entry.VideoCodec, entry.Params, "vcodec")
            ?? string.Empty;
        var proAudioCodec = IdNodeValueHelper.ReadStringOrParam(entry.ProAudioCodec, entry.Params, "pro_acodec")
            ?? IdNodeValueHelper.ReadStringOrParam(entry.AudioCodec, entry.Params, "acodec")
            ?? string.Empty;

        return new ProfileDetails(
            profileUuid,
            profileName,
            profileClass,
            mappedContainer,
            rawContainer,
            proVideoCodec,
            proAudioCodec,
            IdNodeValueHelper.ReadStringArrayOrParam(entry.SourceVideoCodecs, entry.Params, "src_vcodec"),
            IdNodeValueHelper.ReadStringArrayOrParam(entry.SourceAudioCodecs, entry.Params, "src_acodec"),
            IdNodeValueHelper.ReadBoolOrParam(entry.Deinterlace, entry.Params, "deinterlace"));
    }

    public async Task<ResolvedProfile?> ResolveProfileByNameAsync(
        HttpClient httpClient,
        string baseUrl,
        string webRoot,
        string profileName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return null;
        }

        var profiles = await GetProfilesAsync(httpClient, baseUrl, webRoot, cancellationToken).ConfigureAwait(false);
        var profileReference = profiles.FirstOrDefault(p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (profileReference == null)
        {
            return null;
        }

        var details = await GetProfileDetailsByUuidAsync(
            httpClient,
            baseUrl,
            webRoot,
            profileReference.Key,
            profileReference.Name,
            cancellationToken).ConfigureAwait(false);
        if (details == null)
        {
            return null;
        }

        var videoCodecProfile = await ResolveCodecProfileByReferenceAsync(httpClient, baseUrl, webRoot, details.ProVideoCodec, cancellationToken).ConfigureAwait(false);
        var audioCodecProfile = await ResolveCodecProfileByReferenceAsync(httpClient, baseUrl, webRoot, details.ProAudioCodec, cancellationToken).ConfigureAwait(false);

        var resolvedVideoCodec = DeriveCodecName(videoCodecProfile?.Codec, videoCodecProfile?.ProfileClass, videoCodecProfile?.Name, details.ProVideoCodec);
        var resolvedAudioCodec = DeriveCodecName(audioCodecProfile?.Codec, audioCodecProfile?.ProfileClass, audioCodecProfile?.Name, details.ProAudioCodec);

        return new ResolvedProfile(
            details.Key,
            details.Name,
            details.ProfileClass,
            details.Container,
            details.RawContainer,
            details.ProVideoCodec,
            details.ProAudioCodec,
            resolvedVideoCodec,
            resolvedAudioCodec,
            details.SrcVideoCodecs,
            details.SrcAudioCodecs,
            details.Deinterlace,
            videoCodecProfile?.Deinterlace);
    }

    private async Task<CodecProfileDetails?> ResolveCodecProfileByReferenceAsync(
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

        var codecProfileListUrl = $"{baseUrl}{webRoot}api/codec_profile/list";
        var codecProfileListBody = await _tvheadendApiClient.GetStringAsync(httpClient, codecProfileListUrl, cancellationToken).ConfigureAwait(false);
        var codecProfileList = JsonSerializer.Deserialize<CodecProfileListResponse>(codecProfileListBody, JsonOptions);
        if (codecProfileList?.Entries == null || codecProfileList.Entries.Length == 0)
        {
            return null;
        }

        string? codecProfileUuid = null;
        string? codecProfileName = null;

        foreach (var entry in codecProfileList.Entries)
        {
            var uuid = entry.EffectiveUuid;
            var title = entry.EffectiveTitle;

            if (!string.Equals(profileReference, uuid, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(profileReference, title, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(profileReference, NormalizeCodecProfileTitle(title), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            codecProfileUuid = uuid;
            codecProfileName = string.IsNullOrWhiteSpace(NormalizeCodecProfileTitle(title)) ? title : NormalizeCodecProfileTitle(title);
            break;
        }

        if (string.IsNullOrWhiteSpace(codecProfileUuid))
        {
            return null;
        }

        using var codecDoc = await LoadIdNodeByUuidAsync(httpClient, baseUrl, webRoot, codecProfileUuid, cancellationToken).ConfigureAwait(false);
        var codecResponse = JsonSerializer.Deserialize<IdNodeLoadResponse>(codecDoc.RootElement.GetRawText(), JsonOptions);
        if (codecResponse?.Entries == null || codecResponse.Entries.Length == 0)
        {
            return new CodecProfileDetails(codecProfileUuid, codecProfileName ?? string.Empty, string.Empty, string.Empty, null);
        }

        var codecEntry = codecResponse.Entries[0];
        var codecName = IdNodeValueHelper.ReadStringOrParam(codecEntry.Codec, codecEntry.Params, "codec") ?? string.Empty;
        var codecProfileClass = IdNodeValueHelper.ReadStringOrParam(codecEntry.ProfileClass, codecEntry.Params, "class") ?? string.Empty;
        var deinterlace = IdNodeValueHelper.ReadBoolOrParam(codecEntry.Deinterlace, codecEntry.Params, "deinterlace");

        return new CodecProfileDetails(codecProfileUuid, codecProfileName ?? string.Empty, codecProfileClass, codecName, deinterlace);
    }

    private static string NormalizeCodecProfileTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var separatorIndex = title.IndexOf(" (", StringComparison.Ordinal);
        return separatorIndex > 0 ? title[..separatorIndex] : title;
    }

    private static string DeriveCodecName(string? codec, string? codecProfileClass, string? codecProfileName, string? fallbackReference)
    {
        var source = string.Join(
            " ",
            new[] { codec, codecProfileClass, codecProfileName, fallbackReference }
                .Where(v => !string.IsNullOrWhiteSpace(v)));
        if (string.IsNullOrWhiteSpace(source))
        {
            return string.Empty;
        }

        var lower = source.ToLowerInvariant();
        if (lower.Contains("h264", StringComparison.Ordinal)
            || lower.Contains("avc", StringComparison.Ordinal)
            || lower.Contains("x264", StringComparison.Ordinal))
        {
            return "h264";
        }

        if (lower.Contains("hevc", StringComparison.Ordinal)
            || lower.Contains("h265", StringComparison.Ordinal)
            || lower.Contains("x265", StringComparison.Ordinal))
        {
            return "hevc";
        }

        if (lower.Contains("aac", StringComparison.Ordinal))
        {
            return "aac";
        }

        if (lower.Contains("ac3", StringComparison.Ordinal))
        {
            return "ac3";
        }

        if (lower.Contains("eac3", StringComparison.Ordinal))
        {
            return "eac3";
        }

        if (lower.Contains("mp2", StringComparison.Ordinal)
            || lower.Contains("mpeg2audio", StringComparison.Ordinal))
        {
            return "mp2";
        }

        if (lower.Contains("opus", StringComparison.Ordinal))
        {
            return "opus";
        }

        if (lower.Contains("vorbis", StringComparison.Ordinal))
        {
            return "vorbis";
        }

        return string.Empty;
    }

    private async Task<JsonDocument> LoadIdNodeByUuidAsync(HttpClient httpClient, string baseUrl, string webRoot, string uuid, CancellationToken cancellationToken)
    {
        var url = $"{baseUrl}{webRoot}api/idnode/load?uuid={Uri.EscapeDataString(uuid)}";
        var body = await _tvheadendApiClient.GetStringAsync(httpClient, url, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(body);
    }


    private sealed record CodecProfileDetails(string Uuid, string Name, string ProfileClass, string Codec, bool? Deinterlace);
}
