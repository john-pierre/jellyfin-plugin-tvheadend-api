using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Backend;

/// <summary>
/// Provides common helpers for reading TVHeadend idnode values from direct fields or parameter lists.
/// </summary>
internal static class IdNodeValueHelper
{
    public static string? ReadStringOrParam(JsonElement directValue, IReadOnlyList<IdNodeParam> parameters, string parameterName)
    {
        return ReadString(directValue) ?? ReadString(GetParamValue(parameters, parameterName));
    }

    public static int? ReadIntOrParam(JsonElement directValue, IReadOnlyList<IdNodeParam> parameters, string parameterName)
    {
        return ReadInt(directValue) ?? ReadInt(GetParamValue(parameters, parameterName));
    }

    public static bool? ReadBoolOrParam(JsonElement directValue, IReadOnlyList<IdNodeParam> parameters, string parameterName)
    {
        return ReadBool(directValue) ?? ReadBool(GetParamValue(parameters, parameterName));
    }

    public static IReadOnlyList<string> ReadStringArrayOrParam(JsonElement directValue, IReadOnlyList<IdNodeParam> parameters, string parameterName)
    {
        var values = ReadStringArray(directValue);
        return values.Count > 0 ? values : ReadStringArray(GetParamValue(parameters, parameterName));
    }

    public static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static JsonElement GetParamValue(IReadOnlyList<IdNodeParam> parameters, string parameterName)
    {
        foreach (var parameter in parameters)
        {
            if (string.Equals(parameter.Id, parameterName, StringComparison.OrdinalIgnoreCase))
            {
                return parameter.Value;
            }
        }

        return default;
    }

    private static string? ReadString(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int? ReadInt(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        return value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool? ReadBool(JsonElement value)
    {
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
            var rawValue = value.GetString();
            if (bool.TryParse(rawValue, out var boolValue))
            {
                return boolValue;
            }

            if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
            {
                return parsedInt != 0;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
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
}
