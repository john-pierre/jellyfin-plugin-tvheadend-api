using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Streaming;
using Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class StreamProfileContainerResolverTests
{
    [Fact]
    public async Task ResolveContainerAsync_UsesCacheForSameProfile()
    {
        var apiClient = new FakeApiClient();
        var sut = new StreamProfileContainerResolver(
            NullLogger<StreamProfileContainerResolver>.Instance,
            apiClient,
            new TvheadendJsonReader());

        var config = new PluginConfiguration { StreamingProfile = "jellyfin" };

        var first = await sut.ResolveContainerAsync(config, CancellationToken.None);
        var second = await sut.ResolveContainerAsync(config, CancellationToken.None);

        Assert.Equal("mp4", first);
        Assert.Equal("mp4", second);
        Assert.Equal(2, apiClient.GetStringCallCount);
    }

    [Fact]
    public async Task ResolveContainerAsync_InvalidatesCacheWhenProfileChanges()
    {
        var apiClient = new FakeApiClient();
        var sut = new StreamProfileContainerResolver(
            NullLogger<StreamProfileContainerResolver>.Instance,
            apiClient,
            new TvheadendJsonReader());

        var firstConfig = new PluginConfiguration { StreamingProfile = "jellyfin" };
        var secondConfig = new PluginConfiguration { StreamingProfile = "jellyfin-alt" };

        var first = await sut.ResolveContainerAsync(firstConfig, CancellationToken.None);
        var second = await sut.ResolveContainerAsync(secondConfig, CancellationToken.None);

        Assert.Equal("mp4", first);
        Assert.Equal("mp4", second);
        Assert.Equal(4, apiClient.GetStringCallCount);
    }

    private sealed class FakeApiClient : ITvheadendApiClient
    {
        private static readonly HttpClient SharedClient = new();

        public int GetStringCallCount { get; private set; }

        public PluginConfiguration? GetCurrentConfiguration() => new PluginConfiguration();

        public HttpClient CreateHttpClient(PluginConfiguration config) => SharedClient;

        public string GetBaseUrl(PluginConfiguration config) => "http://tvh:9981";

        public string GetWebRoot(PluginConfiguration config) => "/";

        public string BuildUrl(PluginConfiguration config, string endpoint) => $"http://tvh:9981/{endpoint.TrimStart('/')}";

        public Task<string> GetStringAsync(HttpClient httpClient, string url, CancellationToken cancellationToken)
        {
            GetStringCallCount++;

            if (url.Contains("api/profile/list", StringComparison.Ordinal))
            {
                return Task.FromResult("{\"entries\":[{\"key\":\"uuid1\",\"val\":\"jellyfin\"},{\"key\":\"uuid2\",\"val\":\"jellyfin-alt\"}]}");
            }

            if (url.Contains("uuid=uuid1", StringComparison.Ordinal))
            {
                return Task.FromResult("{\"entries\":[{\"class\":\"profile-transcode\",\"container\":9}]}");
            }

            if (url.Contains("uuid=uuid2", StringComparison.Ordinal))
            {
                return Task.FromResult("{\"entries\":[{\"class\":\"profile-transcode\",\"container\":9}]}");
            }

            throw new InvalidOperationException($"Unexpected URL: {url}");
        }

        public Task<Stream> GetStreamAsync(HttpClient httpClient, string url, CancellationToken cancellationToken)
            => Task.FromResult<Stream>(new MemoryStream());

        public Task<HttpResponseMessage> PostFormAsync(HttpClient httpClient, string url, IEnumerable<KeyValuePair<string, string>> formValues, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
