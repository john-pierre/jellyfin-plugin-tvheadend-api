using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Integration;

/// <summary>
/// Live HTTP integration tests against a real TVHeadend instance.
/// These tests require a running TVHeadend server and are excluded from default test runs.
/// <para>
/// Configure connection via <c>TVHEADEND_URL</c> (default: <c>http://localhost:19981</c>).
/// </para>
/// </summary>
/// <remarks>
/// Run with: <c>dotnet test --filter "Category=LiveIntegration"</c>.
/// Default runs exclude this category: <c>dotnet test --filter "Category!=LiveIntegration"</c>.
/// See <c>docker-compose.test.yml</c> for a ready-made test environment.
/// </remarks>
[Trait("Category", "LiveIntegration")]
public class TvHeadendLiveTests : IDisposable
{
    private static readonly string BaseUrl =
        Environment.GetEnvironmentVariable("TVHEADEND_URL") ?? "http://localhost:19981";

    private readonly HttpClient _client;

    public TvHeadendLiveTests()
    {
        var serverUri = new Uri(BaseUrl);

        // TVHeadend uses Digest auth by default. .NET's SocketsHttpHandler does not
        // support Digest, so we use the same DigestAuthHandler as the plugin.
        var digestHandler = new DigestAuthHandler("testuser", "testpass")
        {
            InnerHandler = new HttpClientHandler(),
        };

        _client = new HttpClient(digestHandler)
        {
            BaseAddress = serverUri,
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    [Fact]
    public async Task ServerInfo_ReturnsValidResponse()
    {
        var response = await _client.GetAsync("/api/serverinfo", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(body));

        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("sw_version", out _), "Response should contain sw_version");
        Assert.True(doc.RootElement.TryGetProperty("api_version", out _), "Response should contain api_version");
    }

    [Fact]
    public async Task ChannelGrid_ReturnsGridStructure()
    {
        var response = await _client.GetAsync("/api/channel/grid?limit=5", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("entries", out var entries), "Response should contain entries array");
        Assert.Equal(JsonValueKind.Array, entries.ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("total", out _), "Response should contain total count");
    }

    [Fact]
    public async Task EpgEventsGrid_ReturnsGridStructure()
    {
        var response = await _client.GetAsync("/api/epg/events/grid?limit=5", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("entries", out var entries), "Response should contain entries array");
        Assert.Equal(JsonValueKind.Array, entries.ValueKind);
    }

    [Fact]
    public async Task ProfileList_ReturnsProfiles()
    {
        var response = await _client.GetAsync("/api/profile/list", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("entries", out var entries), "Response should contain entries array");
        Assert.Equal(JsonValueKind.Array, entries.ValueKind);
        Assert.True(entries.GetArrayLength() > 0, "TVHeadend should have at least one streaming profile");
    }

    [Fact]
    public async Task DvrEntryGrid_ReturnsGridStructure()
    {
        var response = await _client.GetAsync("/api/dvr/entry/grid?limit=1", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("entries", out _), "Response should contain entries array");
        Assert.True(doc.RootElement.TryGetProperty("total", out _), "Response should contain total count");
    }

    [Fact]
    public async Task InvalidEndpoint_Returns404OrError()
    {
        var response = await _client.GetAsync("/api/nonexistent/endpoint", CancellationToken.None);

        // TVHeadend returns 404 for unknown endpoints
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
