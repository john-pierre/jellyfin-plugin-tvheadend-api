// Tests for RelayAuthorizationHelper — token extraction and status code mapping.

using Jellyfin.Plugin.TvHeadendApi.Api;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Api;

public class RelayAuthorizationHelperTests
{
    [Theory]
    [InlineData(RelayTokenFailureReason.MissingToken, StatusCodes.Status401Unauthorized)]
    [InlineData(RelayTokenFailureReason.MalformedToken, StatusCodes.Status401Unauthorized)]
    [InlineData(RelayTokenFailureReason.TokenNotFound, StatusCodes.Status401Unauthorized)]
    [InlineData(RelayTokenFailureReason.Expired, StatusCodes.Status410Gone)]
    [InlineData(RelayTokenFailureReason.Revoked, StatusCodes.Status403Forbidden)]
    [InlineData(RelayTokenFailureReason.MaxUsesExceeded, StatusCodes.Status403Forbidden)]
    [InlineData(RelayTokenFailureReason.ScopeMismatch, StatusCodes.Status403Forbidden)]
    [InlineData(RelayTokenFailureReason.RelayTypeMismatch, StatusCodes.Status403Forbidden)]
    [InlineData(RelayTokenFailureReason.UserMismatch, StatusCodes.Status403Forbidden)]
    [InlineData(RelayTokenFailureReason.DeviceMismatch, StatusCodes.Status403Forbidden)]
    [InlineData(RelayTokenFailureReason.UnexpectedError, StatusCodes.Status500InternalServerError)]
    public void MapToStatusCode_ReturnsExpected(RelayTokenFailureReason reason, int expected)
    {
        Assert.Equal(expected, RelayAuthorizationHelper.MapToStatusCode(reason));
    }

    [Fact]
    public void MapToStatusCode_UnknownReason_Returns401()
    {
        Assert.Equal(StatusCodes.Status401Unauthorized, RelayAuthorizationHelper.MapToStatusCode((RelayTokenFailureReason)999));
    }

    [Fact]
    public void ExtractToken_WithQueryParam_ReturnsToken()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?token=my-secret-token");

        var result = RelayAuthorizationHelper.ExtractToken(context.Request);
        Assert.Equal("my-secret-token", result);
    }

    [Fact]
    public void ExtractToken_WithoutQueryParam_ReturnsNull()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?other=value");

        var result = RelayAuthorizationHelper.ExtractToken(context.Request);
        Assert.Null(result);
    }

    [Fact]
    public void ExtractToken_EmptyQueryString_ReturnsNull()
    {
        var context = new DefaultHttpContext();
        var result = RelayAuthorizationHelper.ExtractToken(context.Request);
        Assert.Null(result);
    }

    [Fact]
    public void TokenQueryParam_IsToken()
    {
        Assert.Equal("token", RelayAuthorizationHelper.TokenQueryParam);
    }
}

