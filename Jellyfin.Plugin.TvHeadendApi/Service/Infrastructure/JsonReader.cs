using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Infrastructure;

/// <summary>
/// Adapter around static JSON helper methods to support DI and testing.
/// </summary>
internal sealed class JsonReader : IJsonReader
{
    public bool? GetBoolProp(JsonElement element, string name) => JsonHelper.GetBoolProp(element, name);

    public bool? GetBoolPropOrParam(JsonElement element, string name) => JsonHelper.GetBoolPropOrParam(element, name);

    public int? GetIntProp(JsonElement element, string name) => JsonHelper.GetIntProp(element, name);

    public int? GetIntPropOrParam(JsonElement element, string name) => JsonHelper.GetIntPropOrParam(element, name);

    public string? GetStringProp(JsonElement element, string name) => JsonHelper.GetStringProp(element, name);

    public string? GetStringPropOrParam(JsonElement element, string name) => JsonHelper.GetStringPropOrParam(element, name);

    public IReadOnlyList<string> GetStringArrayProp(JsonElement element, string name) => JsonHelper.GetStringArrayProp(element, name);

    public IReadOnlyList<string> GetStringArrayPropOrParam(JsonElement element, string name) => JsonHelper.GetStringArrayPropOrParam(element, name);
}
