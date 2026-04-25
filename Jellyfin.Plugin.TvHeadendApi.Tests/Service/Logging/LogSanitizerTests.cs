using Jellyfin.Plugin.TvHeadendApi.Service.Logging;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Jellyfin.Plugin.TvHeadendApi.Service.Resilience;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service;

/// <summary>
/// Tests for <see cref="LogSanitizer"/>.
/// </summary>
public class LogSanitizerTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Sanitize_RemovesAuthQueryParam()
    {
        var input = "GET http://tvh:9981/stream/channelid/123?auth=secrettoken123 HTTP/1.1";
        var result = LogSanitizer.Sanitize(input);

        Assert.DoesNotContain("secrettoken123", result);
        Assert.Contains("***REDACTED***", result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Sanitize_RemovesTokenQueryParam()
    {
        var input = "Relay URL: /relay/stream?token=abc123def456";
        var result = LogSanitizer.Sanitize(input);

        Assert.DoesNotContain("abc123def456", result);
        Assert.Contains("***REDACTED***", result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Sanitize_RemovesPasswordQueryParam()
    {
        var input = "Connection: ?password=hunter2&user=admin";
        var result = LogSanitizer.Sanitize(input);

        Assert.DoesNotContain("hunter2", result);
        Assert.Contains("***REDACTED***", result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Sanitize_RemovesAuthorizationHeader()
    {
        var input = "Request header: Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.token";
        var result = LogSanitizer.Sanitize(input);

        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", result);
        Assert.Contains("***REDACTED***", result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Sanitize_RemovesBasicAuth()
    {
        var input = "Authorization: Basic dXNlcjpwYXNz";
        var result = LogSanitizer.Sanitize(input);

        Assert.DoesNotContain("dXNlcjpwYXNz", result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Sanitize_RemovesPasswordKeyValue()
    {
        var input = "Config: password=supersecret host=tvh";
        var result = LogSanitizer.Sanitize(input);

        Assert.DoesNotContain("supersecret", result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Sanitize_PreservesNormalMessages()
    {
        var input = "Channel ARD switched to profile pass";
        var result = LogSanitizer.Sanitize(input);

        Assert.Equal(input, result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Sanitize_NullReturnsEmpty()
    {
        Assert.Equal(string.Empty, LogSanitizer.Sanitize(null));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Sanitize_EmptyReturnsEmpty()
    {
        Assert.Equal(string.Empty, LogSanitizer.Sanitize(string.Empty));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ContainsSensitiveData_DetectsAuthParam()
    {
        Assert.True(LogSanitizer.ContainsSensitiveData("url?auth=token123"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ContainsSensitiveData_DetectsPassword()
    {
        Assert.True(LogSanitizer.ContainsSensitiveData("password=secret"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ContainsSensitiveData_NormalMessageReturnsFalse()
    {
        Assert.False(LogSanitizer.ContainsSensitiveData("Channel tuned successfully"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ContainsSensitiveData_NullReturnsFalse()
    {
        Assert.False(LogSanitizer.ContainsSensitiveData(null));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Sanitize_RemovesApiKeyParam()
    {
        var input = "GET /api?api_key=mykey123&other=safe";
        var result = LogSanitizer.Sanitize(input);

        Assert.DoesNotContain("mykey123", result);
        Assert.Contains("other=safe", result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Sanitize_RemovesUsernameKeyValue()
    {
        var input = "Config: username=admin host=tvh";
        var result = LogSanitizer.Sanitize(input);

        Assert.DoesNotContain("admin", result);
        Assert.Contains("***REDACTED***", result);
    }
}
