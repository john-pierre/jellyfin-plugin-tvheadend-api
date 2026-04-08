using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;

/// <summary>
/// Adapter around static JSON helper methods to support DI and testing.
/// </summary>
internal sealed class TvheadendJsonReader : ITvheadendJsonReader
{
    public bool? GetBoolProp(JsonElement element, string name) => TvheadendJsonHelper.GetBoolProp(element, name);

    public bool? GetBoolPropOrParam(JsonElement element, string name) => TvheadendJsonHelper.GetBoolPropOrParam(element, name);

    public int? GetIntProp(JsonElement element, string name) => TvheadendJsonHelper.GetIntProp(element, name);

    public int? GetIntPropOrParam(JsonElement element, string name) => TvheadendJsonHelper.GetIntPropOrParam(element, name);

    public string? GetStringProp(JsonElement element, string name) => TvheadendJsonHelper.GetStringProp(element, name);

    public string? GetStringPropOrParam(JsonElement element, string name) => TvheadendJsonHelper.GetStringPropOrParam(element, name);

    public IReadOnlyList<string> GetStringArrayProp(JsonElement element, string name) => TvheadendJsonHelper.GetStringArrayProp(element, name);

    public IReadOnlyList<string> GetStringArrayPropOrParam(JsonElement element, string name) => TvheadendJsonHelper.GetStringArrayPropOrParam(element, name);
}
