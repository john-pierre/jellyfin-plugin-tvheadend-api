using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;

/// <summary>
/// Loads stream profile references and detailed profile metadata from TVHeadend APIs.
/// </summary>
internal sealed class TvheadendStreamProfileResolver : ITvheadendStreamProfileResolver
{
    private readonly ITvheadendApiClient _tvheadendApiClient;
    private readonly ITvheadendIdNodeService _idNodeService;
    private readonly ITvheadendJsonReader _jsonReader;

    public TvheadendStreamProfileResolver(
        ITvheadendApiClient tvheadendApiClient,
        ITvheadendIdNodeService idNodeService,
        ITvheadendJsonReader jsonReader)
    {
        _tvheadendApiClient = tvheadendApiClient ?? throw new ArgumentNullException(nameof(tvheadendApiClient));
        _idNodeService = idNodeService ?? throw new ArgumentNullException(nameof(idNodeService));
        _jsonReader = jsonReader ?? throw new ArgumentNullException(nameof(jsonReader));
    }

    public async Task<IReadOnlyList<TvheadendStreamProfileReference>> GetProfilesAsync(
        HttpClient httpClient,
        string baseUrl,
        string webRoot,
        CancellationToken cancellationToken)
    {
        var listUrl = $"{baseUrl}{webRoot}api/profile/list";
        var listBody = await _tvheadendApiClient.GetStringAsync(httpClient, listUrl, cancellationToken).ConfigureAwait(false);

        using var listDoc = JsonDocument.Parse(listBody);
        if (!listDoc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<TvheadendStreamProfileReference>();
        }

        var profiles = new List<TvheadendStreamProfileReference>();
        foreach (var entry in entries.EnumerateArray())
        {
            var key = _jsonReader.GetStringProp(entry, "key");
            var name = _jsonReader.GetStringProp(entry, "val");
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            profiles.Add(new TvheadendStreamProfileReference(key, name));
        }

        return profiles;
    }

    public async Task<TvheadendStreamProfileDetails?> GetProfileDetailsByUuidAsync(
        HttpClient httpClient,
        string baseUrl,
        string webRoot,
        string profileUuid,
        string profileName,
        CancellationToken cancellationToken)
    {
        using var profileDoc = await _idNodeService.LoadIdNodeByUuidAsync(httpClient, baseUrl, webRoot, profileUuid, cancellationToken).ConfigureAwait(false);
        if (!profileDoc.RootElement.TryGetProperty("entries", out var entries) || entries.GetArrayLength() == 0)
        {
            return null;
        }

        var entry = entries[0];
        var profileClass = _jsonReader.GetStringPropOrParam(entry, "class") ?? string.Empty;
        var rawContainer = _jsonReader.GetStringPropOrParam(entry, "container")
            ?? _jsonReader.GetIntPropOrParam(entry, "container")?.ToString(CultureInfo.InvariantCulture)
            ?? string.Empty;

        var mappedContainer = TvheadendProfileMappingHelper.MapContainer(rawContainer);
        if (string.IsNullOrWhiteSpace(mappedContainer))
        {
            mappedContainer = TvheadendProfileMappingHelper.MapProfileClassToContainer(profileClass);
        }

        var proVideoCodec = _jsonReader.GetStringPropOrParam(entry, "pro_vcodec")
            ?? _jsonReader.GetStringPropOrParam(entry, "vcodec")
            ?? string.Empty;
        var proAudioCodec = _jsonReader.GetStringPropOrParam(entry, "pro_acodec")
            ?? _jsonReader.GetStringPropOrParam(entry, "acodec")
            ?? string.Empty;

        return new TvheadendStreamProfileDetails(
            profileUuid,
            profileName,
            profileClass,
            mappedContainer,
            rawContainer,
            proVideoCodec,
            proAudioCodec,
            _jsonReader.GetStringArrayPropOrParam(entry, "src_vcodec"),
            _jsonReader.GetStringArrayPropOrParam(entry, "src_acodec"),
            _jsonReader.GetBoolPropOrParam(entry, "deinterlace"));
    }

    public async Task<TvheadendResolvedStreamProfile?> ResolveProfileByNameAsync(
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

        return new TvheadendResolvedStreamProfile(
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
        using var listDoc = JsonDocument.Parse(codecProfileListBody);
        if (!listDoc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? codecProfileUuid = null;
        string? codecProfileName = null;

        foreach (var entry in entries.EnumerateArray())
        {
            var uuid = _jsonReader.GetStringProp(entry, "uuid")
                ?? _jsonReader.GetStringProp(entry, "key")
                ?? string.Empty;
            var title = _jsonReader.GetStringProp(entry, "title")
                ?? _jsonReader.GetStringProp(entry, "val")
                ?? string.Empty;

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

        using var codecDoc = await _idNodeService.LoadIdNodeByUuidAsync(httpClient, baseUrl, webRoot, codecProfileUuid, cancellationToken).ConfigureAwait(false);
        if (!codecDoc.RootElement.TryGetProperty("entries", out var codecEntries) || codecEntries.GetArrayLength() == 0)
        {
            return new CodecProfileDetails(codecProfileUuid, codecProfileName ?? string.Empty, string.Empty, string.Empty, null);
        }

        var codecEntry = codecEntries[0];
        var codecName = _jsonReader.GetStringPropOrParam(codecEntry, "codec") ?? string.Empty;
        var codecProfileClass = _jsonReader.GetStringPropOrParam(codecEntry, "class") ?? string.Empty;
        var deinterlace = _jsonReader.GetBoolPropOrParam(codecEntry, "deinterlace");

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

    private sealed record CodecProfileDetails(string Uuid, string Name, string ProfileClass, string Codec, bool? Deinterlace);
}
