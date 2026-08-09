using System;
using System.Collections.Generic;
using System.Text.Json;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

/// <summary>
/// Tests for IdNodeValueHelper — all read methods, type coercions, and fallback-to-param paths.
/// </summary>
public class IdNodeValueHelperTests
{
    // --- ReadStringOrParam ---

    [Fact]
    public void ReadStringOrParam_DirectString_ReturnsValue()
    {
        var elem = JsonElement("\"hello\"");
        Assert.Equal("hello", IdNodeValueHelper.ReadStringOrParam(elem, NoParams(), "x"));
    }

    [Fact]
    public void ReadStringOrParam_DirectNumber_ReturnsTostringFallback()
    {
        var elem = JsonElement("42");
        Assert.Equal("42", IdNodeValueHelper.ReadStringOrParam(elem, NoParams(), "x"));
    }

    [Fact]
    public void ReadStringOrParam_DirectNull_FallsBackToParam()
    {
        var elem = default(JsonElement); // Undefined
        var param = new IdNodeParam { Id = "key", Value = JsonElement("\"fromParam\"") };
        Assert.Equal("fromParam", IdNodeValueHelper.ReadStringOrParam(elem, new[] { param }, "key"));
    }

    [Fact]
    public void ReadStringOrParam_BothUndefined_ReturnsNull()
    {
        Assert.Null(IdNodeValueHelper.ReadStringOrParam(default, NoParams(), "x"));
    }

    [Fact]
    public void ReadStringOrParam_DirectJsonNull_FallsBackToParam()
    {
        var elem = JsonElement("null");
        var param = new IdNodeParam { Id = "k", Value = JsonElement("\"fallback\"") };
        Assert.Equal("fallback", IdNodeValueHelper.ReadStringOrParam(elem, new[] { param }, "k"));
    }

    // --- ReadIntOrParam ---

    [Fact]
    public void ReadIntOrParam_DirectNumber_ReturnsValue()
    {
        Assert.Equal(7, IdNodeValueHelper.ReadIntOrParam(JsonElement("7"), NoParams(), "x"));
    }

    [Fact]
    public void ReadIntOrParam_DirectStringNumber_ParsesValue()
    {
        Assert.Equal(42, IdNodeValueHelper.ReadIntOrParam(JsonElement("\"42\""), NoParams(), "x"));
    }

    [Fact]
    public void ReadIntOrParam_DirectUndefined_FallsBackToParam()
    {
        var param = new IdNodeParam { Id = "n", Value = JsonElement("99") };
        Assert.Equal(99, IdNodeValueHelper.ReadIntOrParam(default, new[] { param }, "n"));
    }

    [Fact]
    public void ReadIntOrParam_BothUndefined_ReturnsNull()
    {
        Assert.Null(IdNodeValueHelper.ReadIntOrParam(default, NoParams(), "x"));
    }

    [Fact]
    public void ReadIntOrParam_NonNumericString_ReturnsNull()
    {
        Assert.Null(IdNodeValueHelper.ReadIntOrParam(JsonElement("\"abc\""), NoParams(), "x"));
    }

    [Fact]
    public void ReadIntOrParam_BoolDirect_ReturnsNull()
    {
        // Bool is not a number, should return null
        Assert.Null(IdNodeValueHelper.ReadIntOrParam(JsonElement("true"), NoParams(), "x"));
    }

    // --- ReadBoolOrParam ---

    [Fact]
    public void ReadBoolOrParam_DirectTrue_ReturnsTrue()
    {
        Assert.True(IdNodeValueHelper.ReadBoolOrParam(JsonElement("true"), NoParams(), "x"));
    }

    [Fact]
    public void ReadBoolOrParam_DirectFalse_ReturnsFalse()
    {
        Assert.False(IdNodeValueHelper.ReadBoolOrParam(JsonElement("false"), NoParams(), "x"));
    }

    [Fact]
    public void ReadBoolOrParam_DirectNumber1_ReturnsTrue()
    {
        Assert.True(IdNodeValueHelper.ReadBoolOrParam(JsonElement("1"), NoParams(), "x"));
    }

    [Fact]
    public void ReadBoolOrParam_DirectNumber0_ReturnsFalse()
    {
        Assert.False(IdNodeValueHelper.ReadBoolOrParam(JsonElement("0"), NoParams(), "x"));
    }

    [Fact]
    public void ReadBoolOrParam_StringTrue_ReturnsTrue()
    {
        Assert.True(IdNodeValueHelper.ReadBoolOrParam(JsonElement("\"true\""), NoParams(), "x"));
    }

    [Fact]
    public void ReadBoolOrParam_StringFalse_ReturnsFalse()
    {
        Assert.False(IdNodeValueHelper.ReadBoolOrParam(JsonElement("\"false\""), NoParams(), "x"));
    }

    [Fact]
    public void ReadBoolOrParam_String1_ReturnsTrue()
    {
        Assert.True(IdNodeValueHelper.ReadBoolOrParam(JsonElement("\"1\""), NoParams(), "x"));
    }

    [Fact]
    public void ReadBoolOrParam_String0_ReturnsFalse()
    {
        Assert.False(IdNodeValueHelper.ReadBoolOrParam(JsonElement("\"0\""), NoParams(), "x"));
    }

    [Fact]
    public void ReadBoolOrParam_NonBoolString_ReturnsNull()
    {
        Assert.Null(IdNodeValueHelper.ReadBoolOrParam(JsonElement("\"maybe\""), NoParams(), "x"));
    }

    [Fact]
    public void ReadBoolOrParam_Undefined_FallsBackToParam()
    {
        var param = new IdNodeParam { Id = "b", Value = JsonElement("true") };
        Assert.True(IdNodeValueHelper.ReadBoolOrParam(default, new[] { param }, "b"));
    }

    [Fact]
    public void ReadBoolOrParam_BothUndefined_ReturnsNull()
    {
        Assert.Null(IdNodeValueHelper.ReadBoolOrParam(default, NoParams(), "x"));
    }

    // --- ReadStringArrayOrParam ---

    [Fact]
    public void ReadStringArrayOrParam_DirectArray_ReturnsValues()
    {
        var result = IdNodeValueHelper.ReadStringArrayOrParam(JsonElement("[\"a\",\"b\"]"), NoParams(), "x");
        Assert.Equal(new[] { "a", "b" }, result);
    }

    [Fact]
    public void ReadStringArrayOrParam_EmptyArray_FallsBackToParam()
    {
        var param = new IdNodeParam { Id = "arr", Value = JsonElement("[\"p1\"]") };
        var result = IdNodeValueHelper.ReadStringArrayOrParam(JsonElement("[]"), new[] { param }, "arr");
        Assert.Equal(new[] { "p1" }, result);
    }

    [Fact]
    public void ReadStringArrayOrParam_Undefined_FallsBackToParam()
    {
        var param = new IdNodeParam { Id = "arr", Value = JsonElement("[\"x\"]") };
        var result = IdNodeValueHelper.ReadStringArrayOrParam(default, new[] { param }, "arr");
        Assert.Equal(new[] { "x" }, result);
    }

    [Fact]
    public void ReadStringArrayOrParam_MixedTypes_CoercesToStrings()
    {
        var result = IdNodeValueHelper.ReadStringArrayOrParam(JsonElement("[\"a\",42,true]"), NoParams(), "x");
        Assert.Equal(3, result.Count);
        Assert.Equal("a", result[0]);
        Assert.Equal("42", result[1]);
        Assert.Equal("True", result[2]);
    }

    [Fact]
    public void ReadStringArrayOrParam_NullElements_Skipped()
    {
        var result = IdNodeValueHelper.ReadStringArrayOrParam(JsonElement("[\"a\",null,\"b\"]"), NoParams(), "x");
        Assert.Equal(new[] { "a", "b" }, result);
    }

    [Fact]
    public void ReadStringArrayOrParam_WhitespaceOnlyElements_Skipped()
    {
        var result = IdNodeValueHelper.ReadStringArrayOrParam(JsonElement("[\"a\",\"  \",\"b\"]"), NoParams(), "x");
        Assert.Equal(new[] { "a", "b" }, result);
    }

    // --- ToJsonArray ---

    [Fact]
    public void ToJsonArray_Strings_ReturnsJsonArray()
    {
        var array = IdNodeValueHelper.ToJsonArray(new[] { "one", "two" });
        Assert.Equal(2, array.Count);
        Assert.Equal("one", array[0]!.GetValue<string>());
        Assert.Equal("two", array[1]!.GetValue<string>());
    }

    [Fact]
    public void ToJsonArray_Empty_ReturnsEmptyArray()
    {
        var array = IdNodeValueHelper.ToJsonArray(Array.Empty<string>());
        Assert.Empty(array);
    }

    // --- GetParamValue (indirect via case-insensitive matching) ---

    [Fact]
    public void ReadStringOrParam_ParamMatchIsCaseInsensitive()
    {
        var param = new IdNodeParam { Id = "MyKey", Value = JsonElement("\"found\"") };
        Assert.Equal("found", IdNodeValueHelper.ReadStringOrParam(default, new[] { param }, "mykey"));
    }

    [Fact]
    public void ReadStringOrParam_ParamNotFound_ReturnsNull()
    {
        var param = new IdNodeParam { Id = "other", Value = JsonElement("\"val\"") };
        Assert.Null(IdNodeValueHelper.ReadStringOrParam(default, new[] { param }, "missing"));
    }

    // --- helpers ---

    private static IReadOnlyList<IdNodeParam> NoParams() => Array.Empty<IdNodeParam>();

    private static JsonElement JsonElement(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
