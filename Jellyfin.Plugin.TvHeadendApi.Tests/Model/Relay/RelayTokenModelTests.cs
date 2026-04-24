// Tests for relay token domain models — scope, validation result, and failure reasons.

using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Model.Relay;

/// <summary>
/// Tests for <see cref="RelayTokenScope"/>, <see cref="RelayTokenValidationResult"/>, and <see cref="RelayTokenFailureReason"/>.
/// </summary>
public class RelayTokenModelTests
{
    [Fact]
    public void RelayTokenScope_InitProperties_AreSet()
    {
        var scope = new RelayTokenScope
        {
            RelayType = RelayType.Stream,
            ChannelId = "ch-123",
            UserId = "user-1",
            DeviceId = "dev-1",
            SelectedProfile = "pass",
            PlaybackMode = "Auto",
        };

        Assert.Equal(RelayType.Stream, scope.RelayType);
        Assert.Equal("ch-123", scope.ChannelId);
        Assert.Equal("user-1", scope.UserId);
        Assert.Equal("dev-1", scope.DeviceId);
        Assert.Equal("pass", scope.SelectedProfile);
        Assert.Equal("Auto", scope.PlaybackMode);
        Assert.Null(scope.ImageId);
        Assert.Null(scope.MediaKind);
    }

    [Fact]
    public void RelayTokenScope_ImageScope_SetsImageFields()
    {
        var scope = new RelayTokenScope
        {
            RelayType = RelayType.Image,
            ImageId = "img-456",
            MediaKind = MediaKind.Logo,
        };

        Assert.Equal(RelayType.Image, scope.RelayType);
        Assert.Equal("img-456", scope.ImageId);
        Assert.Equal(MediaKind.Logo, scope.MediaKind);
        Assert.Null(scope.ChannelId);
    }

    [Fact]
    public void RelayTokenValidationResult_Success_ReturnsValidResult()
    {
        var record = new RelayTokenRecord { Id = 1, TokenHash = "abc123" };
        var result = RelayTokenValidationResult.Success(record);

        Assert.True(result.IsValid);
        Assert.Equal(RelayTokenFailureReason.None, result.FailureReason);
        Assert.Same(record, result.TokenRecord);
    }

    [Fact]
    public void RelayTokenValidationResult_Failure_ReturnsInvalidResult()
    {
        var result = RelayTokenValidationResult.Failure(RelayTokenFailureReason.Expired);

        Assert.False(result.IsValid);
        Assert.Equal(RelayTokenFailureReason.Expired, result.FailureReason);
        Assert.Null(result.TokenRecord);
    }

    [Fact]
    public void RelayTokenValidationResult_SecurityDisabled_ReturnsValidSkipped()
    {
        var result = RelayTokenValidationResult.SecurityDisabledResult();

        Assert.True(result.IsValid);
        Assert.Equal(RelayTokenFailureReason.SecurityDisabled, result.FailureReason);
        Assert.Null(result.TokenRecord);
    }

    [Fact]
    public void RelayTokenRecord_DefaultValues()
    {
        var record = new RelayTokenRecord();

        Assert.Equal(0, record.Id);
        Assert.Equal(string.Empty, record.TokenHash);
        Assert.Equal(string.Empty, record.RelayType);
        Assert.Equal(0, record.UseCount);
        Assert.False(record.Revoked);
        Assert.Null(record.ChannelId);
        Assert.Null(record.MaxUses);
        Assert.Null(record.FirstUsedAtUtc);
    }

    [Fact]
    public void RelayTokenFailureReason_HasAllExpectedValues()
    {
        Assert.Equal(0, (int)RelayTokenFailureReason.None);
        Assert.True(System.Enum.IsDefined(RelayTokenFailureReason.MissingToken));
        Assert.True(System.Enum.IsDefined(RelayTokenFailureReason.MalformedToken));
        Assert.True(System.Enum.IsDefined(RelayTokenFailureReason.TokenNotFound));
        Assert.True(System.Enum.IsDefined(RelayTokenFailureReason.Expired));
        Assert.True(System.Enum.IsDefined(RelayTokenFailureReason.Revoked));
        Assert.True(System.Enum.IsDefined(RelayTokenFailureReason.MaxUsesExceeded));
        Assert.True(System.Enum.IsDefined(RelayTokenFailureReason.ScopeMismatch));
        Assert.True(System.Enum.IsDefined(RelayTokenFailureReason.RelayTypeMismatch));
        Assert.True(System.Enum.IsDefined(RelayTokenFailureReason.SecurityDisabled));
        Assert.True(System.Enum.IsDefined(RelayTokenFailureReason.UnexpectedError));
    }
}

