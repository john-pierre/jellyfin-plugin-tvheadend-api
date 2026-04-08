using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;

/// <summary>
/// Adapter around static JSON helper methods to support DI and testing.
/// </summary>
internal sealed class TvheadendJsonReader : ITvheadendJsonReader
{
    public bool? GetBoolProp(JsonElement element, string name) => TvhJsonHelper.GetBoolProp(element, name);

    public bool? GetBoolPropOrParam(JsonElement element, string name) => TvhJsonHelper.GetBoolPropOrParam(element, name);

    public int? GetIntProp(JsonElement element, string name) => TvhJsonHelper.GetIntProp(element, name);

    public int? GetIntPropOrParam(JsonElement element, string name) => TvhJsonHelper.GetIntPropOrParam(element, name);

    public string? GetStringProp(JsonElement element, string name) => TvhJsonHelper.GetStringProp(element, name);

    public string? GetStringPropOrParam(JsonElement element, string name) => TvhJsonHelper.GetStringPropOrParam(element, name);

    public IReadOnlyList<string> GetStringArrayProp(JsonElement element, string name) => TvhJsonHelper.GetStringArrayProp(element, name);

    public IReadOnlyList<string> GetStringArrayPropOrParam(JsonElement element, string name) => TvhJsonHelper.GetStringArrayPropOrParam(element, name);
}
