using System.Text.Json;
using Jellyfin.Plugin.TvHeadendApi.Service.Infrastructure;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class JsonReaderTests
{
    [Fact]
    public void GetStringPropOrParam_ReadsFromDirectProperty()
    {
        using var doc = JsonDocument.Parse("{\"name\":\"jellyfin\"}");
        var sut = new JsonReader();

        var value = sut.GetStringPropOrParam(doc.RootElement, "name");

        Assert.Equal("jellyfin", value);
    }

    [Fact]
    public void GetIntPropOrParam_ReadsFromParamsArray()
    {
        using var doc = JsonDocument.Parse("{\"params\":[{\"id\":\"container\",\"value\":9}]}");
        var sut = new JsonReader();

        var value = sut.GetIntPropOrParam(doc.RootElement, "container");

        Assert.Equal(9, value);
    }

    [Fact]
    public void GetBoolPropOrParam_ReadsStringBooleanFromParams()
    {
        using var doc = JsonDocument.Parse("{\"params\":[{\"id\":\"enabled\",\"value\":\"true\"}]}");
        var sut = new JsonReader();

        var value = sut.GetBoolPropOrParam(doc.RootElement, "enabled");

        Assert.True(value);
    }
}

