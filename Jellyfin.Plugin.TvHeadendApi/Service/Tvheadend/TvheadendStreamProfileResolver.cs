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
}
