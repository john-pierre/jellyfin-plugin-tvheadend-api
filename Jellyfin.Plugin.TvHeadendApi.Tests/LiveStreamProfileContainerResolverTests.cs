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

public class LiveStreamProfileContainerResolverTests
{
    [Fact]
    public async Task ResolveContainerAsync_UsesCacheForSameProfile()
    {
        var apiClient = new FakeApiClient();
        var profileResolver = new FakeProfileResolver();
        var sut = new LiveStreamProfileContainerResolver(
            NullLogger<LiveStreamProfileContainerResolver>.Instance,
            apiClient,
            profileResolver);

        var config = new PluginConfiguration { StreamingProfile = "jellyfin" };

        var first = await sut.ResolveContainerAsync(config, CancellationToken.None);
        var second = await sut.ResolveContainerAsync(config, CancellationToken.None);

        Assert.Equal("mp4", first);
        Assert.Equal("mp4", second);
        Assert.Equal(2, profileResolver.ResolveCalls);
    }

    [Fact]
    public async Task ResolveContainerAsync_InvalidatesCacheWhenProfileChanges()
    {
        var apiClient = new FakeApiClient();
        var profileResolver = new FakeProfileResolver();
        var sut = new LiveStreamProfileContainerResolver(
            NullLogger<LiveStreamProfileContainerResolver>.Instance,
            apiClient,
            profileResolver);

        var firstConfig = new PluginConfiguration { StreamingProfile = "jellyfin" };
        var secondConfig = new PluginConfiguration { StreamingProfile = "jellyfin-alt" };

        var first = await sut.ResolveContainerAsync(firstConfig, CancellationToken.None);
        var second = await sut.ResolveContainerAsync(secondConfig, CancellationToken.None);

        Assert.Equal("mp4", first);
        Assert.Equal("mp4", second);
        Assert.Equal(4, profileResolver.ResolveCalls);
    }

    private sealed class FakeProfileResolver : ITvheadendStreamProfileResolver
    {
        public int ResolveCalls { get; private set; }

        public Task<IReadOnlyList<TvheadendStreamProfileReference>> GetProfilesAsync(HttpClient httpClient, string baseUrl, string webRoot, CancellationToken cancellationToken)
        {
            ResolveCalls++;
            return Task.FromResult<IReadOnlyList<TvheadendStreamProfileReference>>(
                new[]
                {
                    new TvheadendStreamProfileReference("uuid1", "jellyfin"),
                    new TvheadendStreamProfileReference("uuid2", "jellyfin-alt"),
                });
        }

        public Task<TvheadendStreamProfileDetails?> GetProfileDetailsByUuidAsync(HttpClient httpClient, string baseUrl, string webRoot, string profileUuid, string profileName, CancellationToken cancellationToken)
        {
            ResolveCalls++;
            return Task.FromResult<TvheadendStreamProfileDetails?>(new TvheadendStreamProfileDetails(
                profileUuid,
                profileName,
                "profile-transcode",
                "mp4",
                "9",
                string.Empty,
                string.Empty,
                Array.Empty<string>(),
                Array.Empty<string>(),
                null));
        }
    }

    private sealed class FakeApiClient : ITvheadendApiClient
    {
        private static readonly HttpClient SharedClient = new();

        public PluginConfiguration? GetCurrentConfiguration() => new PluginConfiguration();

        public HttpClient CreateHttpClient(PluginConfiguration config) => SharedClient;

        public string GetBaseUrl(PluginConfiguration config) => "http://tvh:9981";

        public string GetWebRoot(PluginConfiguration config) => "/";

        public string BuildUrl(PluginConfiguration config, string endpoint) => $"http://tvh:9981/{endpoint.TrimStart('/')}";

        public Task<string> GetStringAsync(HttpClient httpClient, string url, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Not used by this resolver test.");

        public Task<Stream> GetStreamAsync(HttpClient httpClient, string url, CancellationToken cancellationToken)
            => Task.FromResult<Stream>(new MemoryStream());

        public Task<HttpResponseMessage> PostFormAsync(HttpClient httpClient, string url, IEnumerable<KeyValuePair<string, string>> formValues, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
