using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Infrastructure;

/// <summary>
/// Abstraction for reading TVHeadend JSON and idnode param values.
/// </summary>
internal interface IJsonReader
{
    string? GetStringProp(JsonElement element, string name);

    string? GetStringPropOrParam(JsonElement element, string name);

    int? GetIntProp(JsonElement element, string name);

    int? GetIntPropOrParam(JsonElement element, string name);

    bool? GetBoolProp(JsonElement element, string name);

    bool? GetBoolPropOrParam(JsonElement element, string name);

    IReadOnlyList<string> GetStringArrayProp(JsonElement element, string name);

    IReadOnlyList<string> GetStringArrayPropOrParam(JsonElement element, string name);
}
