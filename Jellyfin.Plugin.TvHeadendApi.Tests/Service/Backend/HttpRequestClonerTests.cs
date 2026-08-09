using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Backend;

/// <summary>
/// Tests for <see cref="HttpRequestCloner"/>.
/// </summary>
public class HttpRequestClonerTests
{
    [Fact]
    public async Task CloneAsync_CopiesMethodAndUri()
    {
        var original = new HttpRequestMessage(HttpMethod.Post, "https://example.com/api/test");
        var clone = await HttpRequestCloner.CloneAsync(original, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, clone.Method);
        Assert.Equal(new Uri("https://example.com/api/test"), clone.RequestUri);
        clone.Dispose();
        original.Dispose();
    }

    [Fact]
    public async Task CloneAsync_CopiesVersion()
    {
        var original = new HttpRequestMessage(HttpMethod.Get, "https://example.com")
        {
            Version = new Version(2, 0),
        };
        var clone = await HttpRequestCloner.CloneAsync(original, CancellationToken.None);

        Assert.Equal(new Version(2, 0), clone.Version);
        clone.Dispose();
        original.Dispose();
    }

    [Fact]
    public async Task CloneAsync_CopiesHeaders()
    {
        var original = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        original.Headers.Add("X-Custom", "test-value");
        var clone = await HttpRequestCloner.CloneAsync(original, CancellationToken.None);

        Assert.True(clone.Headers.Contains("X-Custom"));
        Assert.Equal("test-value", clone.Headers.GetValues("X-Custom").First());
        clone.Dispose();
        original.Dispose();
    }

    [Fact]
    public async Task CloneAsync_CopiesContent()
    {
        var original = new HttpRequestMessage(HttpMethod.Post, "https://example.com")
        {
            Content = new StringContent("hello world", Encoding.UTF8, "text/plain"),
        };
        var clone = await HttpRequestCloner.CloneAsync(original, CancellationToken.None);

        Assert.NotNull(clone.Content);
        var content = await clone.Content.ReadAsStringAsync();
        Assert.Equal("hello world", content);
        clone.Dispose();
        original.Dispose();
    }

    [Fact]
    public async Task CloneAsync_CopiesContentHeaders()
    {
        var original = new HttpRequestMessage(HttpMethod.Post, "https://example.com")
        {
            Content = new StringContent("data", Encoding.UTF8, "application/json"),
        };
        var clone = await HttpRequestCloner.CloneAsync(original, CancellationToken.None);

        Assert.NotNull(clone.Content);
        Assert.Equal("application/json", clone.Content.Headers.ContentType?.MediaType);
        clone.Dispose();
        original.Dispose();
    }

    [Fact]
    public async Task CloneAsync_WithNullContent_ClonesSuccessfully()
    {
        var original = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        var clone = await HttpRequestCloner.CloneAsync(original, CancellationToken.None);

        Assert.Null(clone.Content);
        clone.Dispose();
        original.Dispose();
    }

    [Fact]
    public async Task CloneAsync_CopiesOptions()
    {
        var original = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        original.Options.Set(new HttpRequestOptionsKey<string>("testKey"), "testValue");
        var clone = await HttpRequestCloner.CloneAsync(original, CancellationToken.None);

        Assert.True(clone.Options.TryGetValue(new HttpRequestOptionsKey<string>("testKey"), out var value));
        Assert.Equal("testValue", value);
        clone.Dispose();
        original.Dispose();
    }

    [Fact]
    public async Task CloneAsync_ProducesIndependentInstance()
    {
        var original = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        original.Headers.Add("X-Test", "original");
        var clone = await HttpRequestCloner.CloneAsync(original, CancellationToken.None);

        clone.Headers.Remove("X-Test");
        clone.Headers.Add("X-Test", "modified");

        Assert.Equal("original", original.Headers.GetValues("X-Test").First());
        Assert.Equal("modified", clone.Headers.GetValues("X-Test").First());
        clone.Dispose();
        original.Dispose();
    }
}
