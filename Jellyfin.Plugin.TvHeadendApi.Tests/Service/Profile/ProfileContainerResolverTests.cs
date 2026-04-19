using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Stream;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

public class ProfileContainerResolverTests
{
    [Fact]
    public async Task ResolveContainerAsync_UsesCacheForSameProfile()
    {
        var apiClient = new FakeApiClient();
        var profileResolver = new FakeProfileResolver();
        var sut = new ProfileContainerResolver(NullLogger<ProfileContainerResolver>.Instance, apiClient, new UrlBuilder(), profileResolver);

        var config = new PluginConfiguration { StreamingProfile = "jellyfin" };

        var first = await sut.ResolveContainerAsync(config, CancellationToken.None);
        var second = await sut.ResolveContainerAsync(config, CancellationToken.None);

        Assert.Equal("mp4", first);
        Assert.Equal("mp4", second);
        Assert.Equal(1, profileResolver.ResolveCalls);
    }

    [Fact]
    public async Task ResolveContainerAsync_InvalidatesCacheWhenProfileChanges()
    {
        var apiClient = new FakeApiClient();
        var profileResolver = new FakeProfileResolver();
        var sut = new ProfileContainerResolver(NullLogger<ProfileContainerResolver>.Instance, apiClient, new UrlBuilder(), profileResolver);

        var firstConfig = new PluginConfiguration { StreamingProfile = "jellyfin" };
        var secondConfig = new PluginConfiguration { StreamingProfile = "jellyfin-alt" };

        var first = await sut.ResolveContainerAsync(firstConfig, CancellationToken.None);
        var second = await sut.ResolveContainerAsync(secondConfig, CancellationToken.None);

        Assert.Equal("mp4", first);
        Assert.Equal("mp4", second);
        Assert.Equal(2, profileResolver.ResolveCalls);
    }

    [Fact]
    public async Task ResolveProfileSnapshotAsync_WithBlankProfileName_ReturnsFallbackContainer()
    {
        var apiClient = new FakeApiClient();
        var profileResolver = new FakeProfileResolver();
        var sut = new ProfileContainerResolver(NullLogger<ProfileContainerResolver>.Instance, apiClient, new UrlBuilder(), profileResolver);

        var config = new PluginConfiguration { StreamingProfile = "   " };
        var snapshot = await sut.ResolveProfileSnapshotAsync(config, CancellationToken.None);

        Assert.Equal("mpegts", snapshot.Container);
        Assert.Equal(string.Empty, snapshot.ProfileUuid);
    }

    [Fact]
    public async Task ResolveProfileSnapshotAsync_WhenResolvedProfileIsNull_ReturnsFallbackContainer()
    {
        var apiClient = new FakeApiClient();
        var profileResolver = new NullProfileResolver();
        var sut = new ProfileContainerResolver(NullLogger<ProfileContainerResolver>.Instance, apiClient, new UrlBuilder(), profileResolver);

        var config = new PluginConfiguration { StreamingProfile = "missing" };
        var snapshot = await sut.ResolveProfileSnapshotAsync(config, CancellationToken.None);

        Assert.Equal("mpegts", snapshot.Container);
    }

    [Fact]
    public async Task ResolveProfileSnapshotAsync_WhenTranscodeProfileHasEmptyContainer_UsesFallback()
    {
        var apiClient = new FakeApiClient();
        var profileResolver = new TranscodeNoContainerProfileResolver();
        var sut = new ProfileContainerResolver(NullLogger<ProfileContainerResolver>.Instance, apiClient, new UrlBuilder(), profileResolver);

        var config = new PluginConfiguration { StreamingProfile = "transcode-nocontainer" };
        var snapshot = await sut.ResolveProfileSnapshotAsync(config, CancellationToken.None);

        Assert.Equal("mpegts", snapshot.Container);
    }

    [Fact]
    public async Task ResolveProfileSnapshotAsync_WhenResolverThrows_ReturnsFallbackContainer()
    {
        var apiClient = new FakeApiClient();
        var profileResolver = new ThrowingProfileResolver();
        var sut = new ProfileContainerResolver(NullLogger<ProfileContainerResolver>.Instance, apiClient, new UrlBuilder(), profileResolver);

        var config = new PluginConfiguration { StreamingProfile = "throws" };
        var snapshot = await sut.ResolveProfileSnapshotAsync(config, CancellationToken.None);

        Assert.Equal("mpegts", snapshot.Container);
    }

    [Fact]
    public async Task ResolveContainerAsync_ReturnsCachedContainerFromSnapshot()
    {
        var apiClient = new FakeApiClient();
        var profileResolver = new FakeProfileResolver();
        using var sut = new ProfileContainerResolver(NullLogger<ProfileContainerResolver>.Instance, apiClient, new UrlBuilder(), profileResolver);

        var config = new PluginConfiguration { StreamingProfile = "jellyfin" };
        var container = await sut.ResolveContainerAsync(config, CancellationToken.None);

        Assert.Equal("mp4", container);
    }

    private sealed class NullProfileResolver : IProfileResolver
    {
        public Task<IReadOnlyList<ProfileReference>> GetProfilesAsync(HttpClient httpClient, string baseUrl, string webRoot, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProfileReference>>(Array.Empty<ProfileReference>());

        public Task<ProfileDetails?> GetProfileDetailsByUuidAsync(HttpClient httpClient, string baseUrl, string webRoot, string profileUuid, string profileName, CancellationToken cancellationToken)
            => Task.FromResult<ProfileDetails?>(null);

        public Task<ResolvedProfile?> ResolveProfileByNameAsync(HttpClient httpClient, string baseUrl, string webRoot, string profileName, CancellationToken cancellationToken)
            => Task.FromResult<ResolvedProfile?>(null);
    }

    private sealed class TranscodeNoContainerProfileResolver : IProfileResolver
    {
        public Task<IReadOnlyList<ProfileReference>> GetProfilesAsync(HttpClient httpClient, string baseUrl, string webRoot, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProfileReference>>(Array.Empty<ProfileReference>());

        public Task<ProfileDetails?> GetProfileDetailsByUuidAsync(HttpClient httpClient, string baseUrl, string webRoot, string profileUuid, string profileName, CancellationToken cancellationToken)
            => Task.FromResult<ProfileDetails?>(null);

        public Task<ResolvedProfile?> ResolveProfileByNameAsync(HttpClient httpClient, string baseUrl, string webRoot, string profileName, CancellationToken cancellationToken)
            => Task.FromResult<ResolvedProfile?>(new ResolvedProfile(
                "key-1", profileName, "profile-transcode", string.Empty, string.Empty,
                string.Empty, string.Empty, string.Empty, string.Empty,
                Array.Empty<string>(), Array.Empty<string>(), null, null));
    }

    private sealed class ThrowingProfileResolver : IProfileResolver
    {
        public Task<IReadOnlyList<ProfileReference>> GetProfilesAsync(HttpClient httpClient, string baseUrl, string webRoot, CancellationToken cancellationToken)
            => throw new InvalidOperationException("forced failure");

        public Task<ProfileDetails?> GetProfileDetailsByUuidAsync(HttpClient httpClient, string baseUrl, string webRoot, string profileUuid, string profileName, CancellationToken cancellationToken)
            => Task.FromResult<ProfileDetails?>(null);

        public Task<ResolvedProfile?> ResolveProfileByNameAsync(HttpClient httpClient, string baseUrl, string webRoot, string profileName, CancellationToken cancellationToken)
            => throw new InvalidOperationException("forced failure");
    }

    private sealed class FakeProfileResolver : IProfileResolver
    {
        public int ResolveCalls { get; private set; }

        public Task<IReadOnlyList<ProfileReference>> GetProfilesAsync(HttpClient httpClient, string baseUrl, string webRoot, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<ProfileReference>>(
                new[]
                {
                    new ProfileReference("uuid1", "jellyfin"),
                    new ProfileReference("uuid2", "jellyfin-alt"),
                });
        }

        public Task<ProfileDetails?> GetProfileDetailsByUuidAsync(HttpClient httpClient, string baseUrl, string webRoot, string profileUuid, string profileName, CancellationToken cancellationToken)
        {
            return Task.FromResult<ProfileDetails?>(new ProfileDetails(
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

        public Task<ResolvedProfile?> ResolveProfileByNameAsync(HttpClient httpClient, string baseUrl, string webRoot, string profileName, CancellationToken cancellationToken)
        {
            ResolveCalls++;
            return Task.FromResult<ResolvedProfile?>(new ResolvedProfile(
                "uuid-" + profileName,
                profileName,
                "profile-transcode",
                "mp4",
                "9",
                "jellyfin-h264",
                "jellyfin-aac",
                "h264",
                "aac",
                Array.Empty<string>(),
                Array.Empty<string>(),
                true,
                true));
        }
    }

    private sealed class FakeApiClient : IApiClient
    {
        private static readonly HttpClient SharedClient = new();

        public PluginConfiguration? GetCurrentConfiguration() => new PluginConfiguration();

        public HttpClient CreateApiHttpClient(PluginConfiguration config) => SharedClient;

        public Task<string> GetStringAsync(HttpClient httpClient, string url, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Not used by this resolver test.");

        public Task<Stream> GetStreamAsync(HttpClient httpClient, string url, CancellationToken cancellationToken)
            => Task.FromResult<Stream>(new MemoryStream());

        public Task<HttpResponseMessage> PostFormAsync(HttpClient httpClient, string url, IEnumerable<KeyValuePair<string, string>> formValues, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}


