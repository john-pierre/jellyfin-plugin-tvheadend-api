using System.Text.Json;
using Jellyfin.Plugin.TvHeadendApi.Model;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

/// <summary>
/// Tests for model/DTO serialization and deserialization.
/// Data Model Coverage
/// </summary>
public class ModelTests
{
    [Fact]
    public void ProfileDetectionResult_CanBeSerialized_AndDeserialized()
    {
        // Arrange
        var model = new ProfileDetectionResult
        {
            Success = true,
            Message = "Profile created successfully",
            ProfileName = "jellyfin"
        };

        // Act
        var json = JsonSerializer.Serialize(model);
        var deserialized = JsonSerializer.Deserialize<ProfileDetectionResult>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.True(deserialized.Success);
        Assert.Equal("Profile created successfully", deserialized.Message);
        Assert.Equal("jellyfin", deserialized.ProfileName);
    }

    [Fact]
    public void ProfileDetectionResult_WithNullMessage_CanBeDeserialized()
    {
        // Arrange
        var json = "{\"Success\":false,\"Message\":null,\"ProfileName\":null}";

        // Act
        var model = JsonSerializer.Deserialize<ProfileDetectionResult>(json);

        // Assert
        Assert.NotNull(model);
        Assert.False(model.Success);
        Assert.True(string.IsNullOrEmpty(model.Message));
        Assert.True(string.IsNullOrEmpty(model.ProfileName));
    }

    [Fact]
    public void DiagnoseResult_CanBeSerialized_AndDeserialized()
    {
        // Arrange
        var model = new DiagnoseResult
        {
            OverallStatus = "OK",
            CompatibilityScore = 95
        };

        // Act
        var json = JsonSerializer.Serialize(model);
        var deserialized = JsonSerializer.Deserialize<DiagnoseResult>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal("OK", deserialized.OverallStatus);
        Assert.Equal(95, deserialized.CompatibilityScore);
    }

    [Fact]
    public void DiagnoseCheck_CanBeSerialized_AndDeserialized()
    {
        // Arrange
        var model = new DiagnoseCheck
        {
            Category = "Connection",
            Name = "TVHeadend Connection",
            Status = "OK",
            Message = "Connected successfully"
        };

        // Act
        var json = JsonSerializer.Serialize(model);
        var deserialized = JsonSerializer.Deserialize<DiagnoseCheck>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal("Connection", deserialized.Category);
        Assert.Equal("TVHeadend Connection", deserialized.Name);
        Assert.Equal("OK", deserialized.Status);
        Assert.Equal("Connected successfully", deserialized.Message);
    }

    [Fact]
    public void AuthTokenGenerationResult_CanBeSerialized_AndDeserialized()
    {
        // Arrange
        var model = new AuthTokenGenerationResult
        {
            Success = true,
            Message = "Token generated",
            AuthToken = "test-token-abc123"
        };

        // Act
        var json = JsonSerializer.Serialize(model);
        var deserialized = JsonSerializer.Deserialize<AuthTokenGenerationResult>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.True(deserialized.Success);
        Assert.Equal("Token generated", deserialized.Message);
        Assert.Equal("test-token-abc123", deserialized.AuthToken);
    }

    [Fact]
    public void AuthTokenGenerationResult_WithNullToken_CanBeDeserialized()
    {
        // Arrange
        var json = "{\"Success\":false,\"Message\":\"Failed\",\"AuthToken\":null}";

        // Act
        var model = JsonSerializer.Deserialize<AuthTokenGenerationResult>(json);

        // Assert
        Assert.NotNull(model);
        Assert.False(model.Success);
        Assert.Equal("Failed", model.Message);
        Assert.True(string.IsNullOrEmpty(model.AuthToken));
    }

    [Fact]
    public void TvhApiChannelGridResponse_CanBeDeserialized()
    {
        // Arrange
        var json = """{"entries":[],"total":0}""";

        // Act
        var model = JsonSerializer.Deserialize<TvhApiChannelGridResponse>(json);

        // Assert
        Assert.NotNull(model);
        Assert.NotNull(model.Entries);
        Assert.Empty(model.Entries);
        Assert.Equal(0, model.Total);
    }

    [Fact]
    public void TvhApiChannelTagResponse_CanBeDeserialized()
    {
        // Arrange
        var json = """{"entries":[],"total":0}""";

        // Act
        var model = JsonSerializer.Deserialize<TvhApiChannelTagResponse>(json);

        // Assert
        Assert.NotNull(model);
        Assert.Empty(model.Entries);
    }

    [Fact]
    public void Models_WithEmptyCollections_Serialize_Successfully()
    {
        // Arrange
        var channelJson = """{"entries":[],"total":0}""";
        var tagJson = """{"entries":[],"total":0}""";

        // Act
        var channelResponse = JsonSerializer.Deserialize<TvhApiChannelGridResponse>(channelJson);
        var tagResponse = JsonSerializer.Deserialize<TvhApiChannelTagResponse>(tagJson);

        // Assert
        Assert.NotNull(channelResponse);
        Assert.NotNull(tagResponse);
        Assert.Empty(channelResponse.Entries);
        Assert.Equal(0, channelResponse.Total);
    }
}
