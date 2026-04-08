using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;

/// <summary>
/// Provides helper methods for reading TVHeadend JSON payloads and idnode params.
/// </summary>
internal static class TvhJsonHelper
{
    public static string? GetStringProp(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var prop))
        {
            return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
        }

        return null;
    }

    public static string? GetStringPropOrParam(JsonElement element, string name)
    {
        return GetStringProp(element, name) ?? GetParamStringProp(element, name);
    }

    public static int? GetIntProp(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var intVal))
            {
                return intVal;
            }

            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    public static int? GetIntPropOrParam(JsonElement element, string name)
    {
        return GetIntProp(element, name) ?? GetParamIntProp(element, name);
    }

    public static bool? GetBoolProp(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var prop))
        {
            return null;
        }

        if (prop.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (prop.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var intVal))
        {
            return intVal != 0;
        }

        if (prop.ValueKind == JsonValueKind.String)
        {
            var strVal = prop.GetString();
            if (bool.TryParse(strVal, out var boolVal))
            {
                return boolVal;
            }

            if (int.TryParse(strVal, out var parsedInt))
            {
                return parsedInt != 0;
            }
        }

        return null;
    }

    public static bool? GetBoolPropOrParam(JsonElement element, string name)
    {
        return GetBoolProp(element, name) ?? GetParamBoolProp(element, name);
    }

    public static IReadOnlyList<string> GetStringArrayProp(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var values = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            var value = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    public static IReadOnlyList<string> GetStringArrayPropOrParam(JsonElement element, string name)
    {
        var values = GetStringArrayProp(element, name);
        return values.Count > 0 ? values : GetParamStringArrayProp(element, name);
    }

    public static System.Text.Json.Nodes.JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new System.Text.Json.Nodes.JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static string? GetParamStringProp(JsonElement element, string name)
    {
        if (!TryGetParamValue(element, name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int? GetParamIntProp(JsonElement element, string name)
    {
        if (!TryGetParamValue(element, name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static bool? GetParamBoolProp(JsonElement element, string name)
    {
        if (!TryGetParamValue(element, name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
        {
            return intValue != 0;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var stringValue = value.GetString();
            if (bool.TryParse(stringValue, out var boolValue))
            {
                return boolValue;
            }

            if (int.TryParse(stringValue, out var parsed))
            {
                return parsed != 0;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> GetParamStringArrayProp(JsonElement element, string name)
    {
        if (!TryGetParamValue(element, name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var values = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            var stringValue = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
            if (!string.IsNullOrWhiteSpace(stringValue))
            {
                values.Add(stringValue);
            }
        }

        return values;
    }

    private static bool TryGetParamValue(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
        {
            foreach (var parameter in parameters.EnumerateArray())
            {
                var id = GetStringProp(parameter, "id");
                if (!string.Equals(id, name, StringComparison.OrdinalIgnoreCase)
                    || !parameter.TryGetProperty("value", out value))
                {
                    continue;
                }

                return true;
            }
        }

        value = default;
        return false;
    }
}
