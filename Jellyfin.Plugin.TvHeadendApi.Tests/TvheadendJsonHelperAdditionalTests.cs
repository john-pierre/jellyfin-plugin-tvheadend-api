using System.Text.Json;
using Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

/// <summary>
/// Tests for TvheadendJsonHelper utility methods.
/// JSON Helper Coverage
/// </summary>
public class TvheadendJsonHelperTests
{
    [Fact]
    public void GetStringProp_WithValidProperty_ReturnsValue()
    {
        // Arrange
        var json = JsonDocument.Parse("{\"name\":\"Test\"}");
        var element = json.RootElement;

        // Act
        var result = TvheadendJsonHelper.GetStringProp(element, "name");

        // Assert
        Assert.Equal("Test", result);
    }

    [Fact]
    public void GetStringProp_WithMissingProperty_ReturnsNull()
    {
        // Arrange
        var json = JsonDocument.Parse("{\"other\":\"value\"}");
        var element = json.RootElement;

        // Act
        var result = TvheadendJsonHelper.GetStringProp(element, "name");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetStringProp_WithNullValue_ReturnsEmptyString()
    {
        // Arrange
        var json = JsonDocument.Parse("{\"name\":null}");
        var element = json.RootElement;

        // Act
        var result = TvheadendJsonHelper.GetStringProp(element, "name");

        // Assert
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetIntProp_WithValidInteger_ReturnsValue()
    {
        // Arrange
        var json = JsonDocument.Parse("{\"count\":42}");
        var element = json.RootElement;

        // Act
        var result = TvheadendJsonHelper.GetIntProp(element, "count");

        // Assert
        Assert.Equal(42, result);
    }

    [Fact]
    public void GetIntProp_WithMissingProperty_ReturnsNull()
    {
        // Arrange
        var json = JsonDocument.Parse("{\"other\":5}");
        var element = json.RootElement;

        // Act
        var result = TvheadendJsonHelper.GetIntProp(element, "count");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetIntProp_WithStringValue_ReturnsNull()
    {
        // Arrange
        var json = JsonDocument.Parse("{\"count\":\"not-a-number\"}");
        var element = json.RootElement;

        // Act
        var result = TvheadendJsonHelper.GetIntProp(element, "count");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetBoolProp_WithTrueValue_ReturnsTrue()
    {
        // Arrange
        var json = JsonDocument.Parse("{\"enabled\":true}");
        var element = json.RootElement;

        // Act
        var result = TvheadendJsonHelper.GetBoolProp(element, "enabled");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void GetBoolProp_WithFalseValue_ReturnsFalse()
    {
        // Arrange
        var json = JsonDocument.Parse("{\"enabled\":false}");
        var element = json.RootElement;

        // Act
        var result = TvheadendJsonHelper.GetBoolProp(element, "enabled");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void GetBoolProp_WithMissingProperty_ReturnsNull()
    {
        // Arrange
        var json = JsonDocument.Parse("{\"other\":true}");
        var element = json.RootElement;

        // Act
        var result = TvheadendJsonHelper.GetBoolProp(element, "enabled");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetBoolProp_WithNumericValue_ParsesCorrectly()
    {
        // Arrange - TVHeadend sometimes returns 0/1 instead of false/true
        var json = JsonDocument.Parse("{\"enabled\":1}");
        var element = json.RootElement;

        // Act
        var result = TvheadendJsonHelper.GetBoolProp(element, "enabled");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void GetStringPropOrParam_WithStringProp_ReturnsStringValue()
    {
        // Arrange
        var json = JsonDocument.Parse("{\"title\":\"My Title\"}");
        var element = json.RootElement;

        // Act
        var result = TvheadendJsonHelper.GetStringPropOrParam(element, "title");

        // Assert
        Assert.Equal("My Title", result);
    }

    [Fact]
    public void GetIntPropOrParam_WithIntValue_ReturnsIntValue()
    {
        // Arrange
        var json = JsonDocument.Parse("{\"duration\":3600}");
        var element = json.RootElement;

        // Act
        var result = TvheadendJsonHelper.GetIntPropOrParam(element, "duration");

        // Assert
        Assert.Equal(3600, result);
    }

    [Fact]
    public void GetBoolPropOrParam_WithBoolValue_ReturnsBoolValue()
    {
        // Arrange
        var json = JsonDocument.Parse("{\"enabled\":true}");
        var element = json.RootElement;

        // Act
        var result = TvheadendJsonHelper.GetBoolPropOrParam(element, "enabled");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void GetStringPropOrParam_WithParamString_ReturnsValue()
    {
        // Arrange
        var json = JsonDocument.Parse("{\"params\":[{\"id\":\"name\",\"value\":\"test\"}]}");
        var element = json.RootElement;

        // Act
        var result = TvheadendJsonHelper.GetStringPropOrParam(element, "name");

        // Assert
        Assert.Equal("test", result);
    }

    [Fact]
    public void GetIntPropOrParam_WithParamInt_ReturnsValue()
    {
        // Arrange
        var json = JsonDocument.Parse("{\"params\":[{\"id\":\"timeout\",\"value\":5000}]}");
        var element = json.RootElement;

        // Act
        var result = TvheadendJsonHelper.GetIntPropOrParam(element, "timeout");

        // Assert
        Assert.Equal(5000, result);
    }

    [Fact]
    public void GetBoolPropOrParam_WithParamBool_ReturnsValue()
    {
        // Arrange
        var json = JsonDocument.Parse("{\"params\":[{\"id\":\"debug\",\"value\":true}]}");
        var element = json.RootElement;

        // Act
        var result = TvheadendJsonHelper.GetBoolPropOrParam(element, "debug");

        // Assert
        Assert.True(result);
    }
}
