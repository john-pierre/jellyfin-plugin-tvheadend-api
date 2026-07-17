using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Integration;

/// <summary>
/// Comprehensive HTTP API end-to-end tests that exercise every plugin endpoint
/// through the running Jellyfin server. Requires the full Docker test stack
/// (TVHeadend + Jellyfin + IPTV simulator) to be running.
/// </summary>
[Trait("Category", "LiveIntegration")]
[Collection("JellyfinApi")]
public sealed class JellyfinApiEndToEndTests
{
    private readonly JellyfinApiFixture _fixture;

    public JellyfinApiEndToEndTests(JellyfinApiFixture fixture)
    {
        _fixture = fixture;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private void SkipIfUnavailable()
    {
        if (!_fixture.IsAvailable)
        {
            var reason = _fixture.SetupError ?? "unknown reason";
            Assert.Fail($"Jellyfin API fixture is not available ({reason}) — skipping test.");
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string path)
    {
        var response = await _fixture.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    private async Task<JsonDocument> PostJsonAsync(string path, HttpContent? content = null)
    {
        var response = await _fixture.Client.PostAsync(path, content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    /// <summary>
    /// Retrieves the first channel UUID from the streaming profiles channel list.
    /// Returns <c>null</c> if no channels are available (TVH may be unreachable).
    /// </summary>
    private async Task<string?> TryGetFirstChannelUuidAsync()
    {
        var response = await _fixture.Client.GetAsync("/TvHeadendApi/StreamingProfiles/Channels");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var first = doc.RootElement.EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return first.GetProperty("Id").GetString();
    }

    /// <summary>
    /// Requests PlaybackInfo for the first available Jellyfin Live TV channel item, driving the
    /// plugin's real <c>MediaSourceService</c>/<c>RelayUrlBuilder</c> path so a genuine HMAC relay
    /// token gets minted (the fixture's config keeps <c>EnableRelayTokenSecurity</c> enabled — this
    /// is NOT the same as the TVHeadend <c>AuthToken</c>). Returns the TVHeadend channel id, the
    /// streaming profile carried by the token (if any), and the raw token, so callers can build a
    /// request against either the open <c>/api/tvheadend/stream/{channelId}</c> route or the
    /// token-secured <c>/api/tvheadend/relay/stream/{channelId}</c> route.
    /// </summary>
    private async Task<(string ChannelId, string? Profile, string Token)> IssueRelayStreamTokenAsync()
    {
        // Jellyfin's Live TV channel cache may be empty right after the plugin recovered from a
        // misconfiguration — trigger a guide refresh when needed before requiring channel items.
        Assert.True(
            await _fixture.EnsureLiveTvChannelsAsync(),
            "Jellyfin must expose at least one Live TV channel item (guide refresh did not surface any).");

        using var channelsDoc = await GetJsonAsync("/LiveTv/Channels?limit=1");
        var liveTvChannels = channelsDoc.RootElement.GetProperty("Items");
        Assert.True(liveTvChannels.GetArrayLength() > 0, "Jellyfin must expose at least one Live TV channel item.");
        var jellyfinChannelId = liveTvChannels[0].GetProperty("Id").GetString();
        Assert.False(string.IsNullOrEmpty(jellyfinChannelId), "Live TV channel item must have an Id.");

        using var playbackDoc = await GetJsonAsync($"/Items/{jellyfinChannelId}/PlaybackInfo");
        var mediaSources = playbackDoc.RootElement.GetProperty("MediaSources");
        Assert.True(mediaSources.GetArrayLength() > 0, "PlaybackInfo must return at least one media source.");

        var relayUrl = mediaSources[0].GetProperty("Path").GetString();
        Assert.False(string.IsNullOrEmpty(relayUrl), "Media source Path must contain the relay stream URL.");

        var relayUri = new Uri(relayUrl, UriKind.Absolute);
        var channelId = Uri.UnescapeDataString(relayUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[^1]);
        Assert.False(string.IsNullOrEmpty(channelId), $"Could not extract channel id from relay URL: {relayUrl}");

        var queryParts = relayUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        var token = queryParts
            .Where(p => p.StartsWith("token=", StringComparison.OrdinalIgnoreCase))
            .Select(p => Uri.UnescapeDataString(p.Substring("token=".Length)))
            .FirstOrDefault();
        var profile = queryParts
            .Where(p => p.StartsWith("profile=", StringComparison.OrdinalIgnoreCase))
            .Select(p => Uri.UnescapeDataString(p.Substring("profile=".Length)))
            .FirstOrDefault();

        Assert.False(string.IsNullOrEmpty(token), $"Relay URL must contain a token query parameter: {relayUrl}");

        return (channelId, profile, token);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // A) Plugin Info & Config
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PluginInfo_ReturnsNameAndVersion()
    {
        SkipIfUnavailable();
        using var doc = await GetJsonAsync("/TvHeadendApi/PluginInfo");

        Assert.True(doc.RootElement.TryGetProperty("Name", out var name), "Response must have Name property.");
        Assert.False(string.IsNullOrEmpty(name.GetString()), "Name must not be empty.");

        Assert.True(doc.RootElement.TryGetProperty("Version", out var version), "Response must have Version property.");
        Assert.False(string.IsNullOrEmpty(version.GetString()), "Version must not be empty.");
    }

    [Fact]
    public async Task ResetToDefaults_ResetsAndReturnsSuccess()
    {
        SkipIfUnavailable();
        try
        {
            var resetResp = await _fixture.Client.PostAsync("/TvHeadendApi/ResetToDefaults", null);
            Assert.True(
                resetResp.StatusCode == HttpStatusCode.OK || resetResp.StatusCode == HttpStatusCode.NoContent,
                $"Expected 200 or 204 for ResetToDefaults, got {(int)resetResp.StatusCode}.");

            // If the response has a body, verify Success property.
            var resetBody = await resetResp.Content.ReadAsStringAsync();
            if (!string.IsNullOrWhiteSpace(resetBody))
            {
                using var doc = JsonDocument.Parse(resetBody);
                if (doc.RootElement.TryGetProperty("Success", out var success))
                {
                    Assert.True(success.GetBoolean(), "ResetToDefaults should succeed.");
                }
            }
        }
        finally
        {
            // ResetToDefaults points Host at the default (unreachable inside the container) and can trip
            // the circuit breaker; re-apply the known-good connection config and wait for it to recover
            // so later collection tests are not polluted. This performs the same field overrides
            // (Host/Port/Username/Password/AllowAnonymousAccess/RelayEnabled/EnableRelayTokenSecurity/
            // StreamingProfile) the fixture applies during setup, so no hand-rolled re-configure is needed.
            await _fixture.EnsureConfiguredAndHealthyAsync();
        }
    }

    [Fact]
    public async Task ProfileOptions_ReturnsStreamingAndRecordingProfiles()
    {
        SkipIfUnavailable();
        var response = await _fixture.Client.GetAsync("/TvHeadendApi/ProfileOptions");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected success from ProfileOptions, got {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("StreamingProfiles", out var streaming), "Response must have StreamingProfiles.");
        Assert.True(streaming.ValueKind == JsonValueKind.Array, "StreamingProfiles must be an array.");
    }

    [Fact]
    public async Task Diagnose_ReturnsCompatibilityReport()
    {
        SkipIfUnavailable();
        var response = await _fixture.Client.GetAsync("/TvHeadendApi/Diagnose");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected success from Diagnose, got {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("CompatibilityScore", out _), "Response must have CompatibilityScore.");

        Assert.True(doc.RootElement.TryGetProperty("Checks", out var checks), "Response must have Checks array.");
        // Connection check should be present.
        var hasConnection = false;
        foreach (var check in checks.EnumerateArray())
        {
            if (check.TryGetProperty("Category", out var cat) &&
                cat.GetString()?.Contains("Connection", StringComparison.OrdinalIgnoreCase) == true)
            {
                hasConnection = true;
                break;
            }
        }

        Assert.True(hasConnection, "Diagnose should include a Connection check.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // B) Auth Token
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GenerateAuthToken_ReturnsSuccess()
    {
        SkipIfUnavailable();
        var response = await _fixture.Client.PostAsync("/TvHeadendApi/GenerateAuthToken", null);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected success from GenerateAuthToken, got {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("Success", out var success), "Response must have Success property.");
        Assert.True(success.GetBoolean(), "GenerateAuthToken should succeed.");

        Assert.True(doc.RootElement.TryGetProperty("AuthToken", out var token), "Response must have AuthToken property.");
        Assert.False(string.IsNullOrEmpty(token.GetString()), "AuthToken must not be empty.");
    }

    [Fact]
    public async Task CreateProfile_CreatesJellyfinProfile()
    {
        SkipIfUnavailable();
        var response = await _fixture.Client.PostAsync("/TvHeadendApi/CreateProfile", null);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected success from CreateProfile, got {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("Success", out var success), "Response must have Success property.");
        Assert.True(success.GetBoolean(), "CreateProfile should succeed.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // C) Streaming Profile Discovery
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Discovered_ReturnsProfiles()
    {
        SkipIfUnavailable();
        var response = await _fixture.Client.GetAsync("/TvHeadendApi/StreamingProfiles/Discovered");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected success from Discovered, got {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Array, "Discovered must return an array.");
    }

    [Fact]
    public async Task Channels_ReturnsChannelList()
    {
        SkipIfUnavailable();
        var response = await _fixture.Client.GetAsync("/TvHeadendApi/StreamingProfiles/Channels");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected success from Channels, got {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Array, "Channels must return an array.");

        foreach (var ch in doc.RootElement.EnumerateArray())
        {
            Assert.True(ch.TryGetProperty("Id", out var id), "Channel must have Id.");
            Assert.False(string.IsNullOrEmpty(id.GetString()), "Channel Id must not be empty.");
            Assert.True(ch.TryGetProperty("Name", out var name), "Channel must have Name.");
            Assert.False(string.IsNullOrEmpty(name.GetString()), "Channel Name must not be empty.");
        }
    }

    [Fact]
    public async Task ChannelGroups_ReturnsGroupList()
    {
        SkipIfUnavailable();
        var response = await _fixture.Client.GetAsync("/TvHeadendApi/StreamingProfiles/ChannelGroups");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        // Channel groups depend on TVHeadend tag configuration — at minimum, verify it's a valid array.
        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Array, "Response must be an array.");
    }

    [Fact]
    public async Task Resolve_WithNoParams_ReturnsResult()
    {
        SkipIfUnavailable();
        using var doc = await GetJsonAsync("/TvHeadendApi/StreamingProfiles/Resolve");

        Assert.True(
            doc.RootElement.TryGetProperty("EffectiveTvHeadendProfile", out _),
            "Response must have EffectiveTvHeadendProfile.");
    }

    [Fact]
    public async Task Validate_ReturnsArrayOrEmpty()
    {
        SkipIfUnavailable();
        using var doc = await GetJsonAsync("/TvHeadendApi/StreamingProfiles/Validate");

        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Array, "Validate must return an array.");
    }

    [Fact]
    public async Task RefreshCache_ReturnsOk()
    {
        SkipIfUnavailable();
        using var doc = await PostJsonAsync("/TvHeadendApi/StreamingProfiles/RefreshCache");

        Assert.True(doc.RootElement.TryGetProperty("Message", out _), "RefreshCache should return a message.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // D) Monitoring
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Status_ReturnsActivityStatus()
    {
        SkipIfUnavailable();
        var response = await _fixture.Client.GetAsync("/TvHeadendApi/Status");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected success from Status, got {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Object, "Status must return a JSON object.");
    }

    [Fact]
    public async Task Connections_ReturnsArray()
    {
        SkipIfUnavailable();
        var response = await _fixture.Client.GetAsync("/TvHeadendApi/Connections");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected success from Connections, got {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Array, "Connections must return an array.");
    }

    [Fact]
    public async Task Inputs_ReturnsArray()
    {
        SkipIfUnavailable();
        var response = await _fixture.Client.GetAsync("/TvHeadendApi/Inputs");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected success from Inputs, got {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Array, "Inputs must return an array.");
    }

    [Fact]
    public async Task Subscriptions_ReturnsArray()
    {
        SkipIfUnavailable();
        using var doc = await GetJsonAsync("/TvHeadendApi/Subscriptions");

        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Array, "Subscriptions must return an array.");
    }

    [Fact]
    public async Task Health_ReturnsSnapshot()
    {
        SkipIfUnavailable();
        using var doc = await GetJsonAsync("/TvHeadendApi/Health");

        Assert.True(doc.RootElement.TryGetProperty("Status", out _), "Health must have a Status field.");
    }

    [Fact]
    public async Task HealthCheck_ReturnsUpdatedSnapshot()
    {
        SkipIfUnavailable();
        using var doc = await PostJsonAsync("/TvHeadendApi/Health/Check");

        Assert.True(doc.RootElement.TryGetProperty("Status", out _), "Health/Check must have a Status field.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // E) Statistics
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Statistics_ReturnsResult()
    {
        SkipIfUnavailable();
        var response = await _fixture.Client.GetAsync("/TvHeadendApi/Statistics?days=30");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Object, "Statistics must return a JSON object.");
    }

    [Fact]
    public async Task Statistics_ClearReturnsSuccess()
    {
        SkipIfUnavailable();
        var response = await _fixture.Client.DeleteAsync("/TvHeadendApi/Statistics");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent,
            $"Expected 200 or 204 for Statistics clear, got {(int)response.StatusCode}: {body}");

        if (!string.IsNullOrWhiteSpace(body))
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("Success", out var success))
            {
                Assert.True(success.GetBoolean(), "Clear should succeed.");
            }
        }
    }

    [Fact]
    public async Task Statistics_AfterClear_ReturnsEmpty()
    {
        SkipIfUnavailable();

        // Clear first.
        var deleteResp = await _fixture.Client.DeleteAsync("/TvHeadendApi/Statistics");
        var deleteBody = await deleteResp.Content.ReadAsStringAsync();

        Assert.True(
            deleteResp.StatusCode == HttpStatusCode.OK || deleteResp.StatusCode == HttpStatusCode.NoContent,
            $"Expected 200 or 204 for Statistics clear, got {(int)deleteResp.StatusCode}: {deleteBody}");

        // Fetch stats.
        using var doc = await GetJsonAsync("/TvHeadendApi/Statistics");
        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Object, "Statistics must return a JSON object.");

        // After clear, Sessions should be empty or ActiveCount should be 0.
        if (doc.RootElement.TryGetProperty("Sessions", out var sessions))
        {
            Assert.True(
                sessions.ValueKind == JsonValueKind.Array && sessions.GetArrayLength() == 0,
                "Sessions should be empty after clear.");
        }
        else if (doc.RootElement.TryGetProperty("ActiveCount", out var active))
        {
            Assert.Equal(0, active.GetInt32());
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // F) Dashboard
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Dashboard_ReturnsAggregatedData()
    {
        SkipIfUnavailable();
        using var doc = await GetJsonAsync("/TvHeadendApi/Dashboard");

        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Object, "Dashboard must return a JSON object.");
    }

    [Fact]
    public async Task RelayMetrics_ReturnsMetrics()
    {
        SkipIfUnavailable();
        var response = await _fixture.Client.GetAsync("/TvHeadendApi/RelayMetrics?hours=24");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Object, "RelayMetrics must return a JSON object.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // G) Logs
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Logs_ReturnsLogsAndDiskSpace()
    {
        SkipIfUnavailable();
        var response = await _fixture.Client.GetAsync("/TvHeadendApi/Logs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Object, "Logs must return a JSON object.");
    }

    [Fact]
    public async Task LogsDiskSpace_ReturnsDiskInfo()
    {
        SkipIfUnavailable();
        var response = await _fixture.Client.GetAsync("/TvHeadendApi/Logs/diskspace");

        // Disk space may be 404 if Comet has not received a disk space update yet — accept both 200 and 404.
        Assert.True(
            response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NotFound,
            $"Expected 200 or 404, got {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task LogEntries_ReturnsEntryList()
    {
        SkipIfUnavailable();
        var response = await _fixture.Client.GetAsync("/TvHeadendApi/Logs/entries");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Array, "Log entries must return an array.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // H) Dashboard Logs
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DashboardLogs_ReturnsFilteredLogs()
    {
        SkipIfUnavailable();
        var response = await _fixture.Client.GetAsync("/TvHeadendApi/Dashboard/Logs?limit=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Object, "Dashboard Logs must return a JSON object.");

        Assert.True(doc.RootElement.TryGetProperty("Entries", out var entries), "Response must have Entries.");
        Assert.True(entries.ValueKind == JsonValueKind.Array, "Entries must be an array.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // I) Relay Endpoints
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RelayStatus_ReturnsStatus()
    {
        SkipIfUnavailable();

        // Relay status is AllowAnonymous — use anonymous client.
        var response = await _fixture.AnonymousClient.GetAsync("/api/tvheadend/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("EffectiveHost", out _), "Relay status must have EffectiveHost.");
    }

    [Fact]
    public async Task RelayStream_WithValidChannel_Returns200()
    {
        SkipIfUnavailable();

        // The bootstrapped stack enables EnableRelayTokenSecurity by default, so even the open
        // /stream/{channelId} endpoint now requires a valid relay token — obtain a genuine one via
        // PlaybackInfo (the same token also authorizes /stream/{channelId}, not just
        // /relay/stream/{channelId}) instead of tolerating the resulting 401 as before.
        var (channelId, profile, token) = await IssueRelayStreamTokenAsync();
        var tokenQuery = string.IsNullOrWhiteSpace(profile)
            ? $"token={Uri.EscapeDataString(token)}"
            : $"profile={Uri.EscapeDataString(profile)}&token={Uri.EscapeDataString(token)}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await _fixture.Client.GetAsync(
            $"/api/tvheadend/stream/{channelId}?{tokenQuery}",
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        var body = response.IsSuccessStatusCode ? string.Empty : await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Expected success from the relay stream endpoint, got {(int)response.StatusCode}: {body}");

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        Assert.True(
            contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
            contentType == "application/octet-stream" ||
            contentType == "video/MP2T",
            $"Unexpected content type: {contentType}");
    }

    [Fact]
    public async Task RelayImage_WithChannelLogo_Returns200Or404()
    {
        SkipIfUnavailable();

        var response = await _fixture.Client.GetAsync("/api/tvheadend/images/imagecache/1");

        // Image may be 200 (logo cached) or 404 (no logo at this id) — TVHeadend reachability is
        // guaranteed by the bootstrapped stack, so a 502 upstream failure is no longer tolerated.
        Assert.True(
            response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NotFound,
            $"Expected 200 or 404, got {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task RelayTokenStream_WithValidToken_Succeeds()
    {
        SkipIfUnavailable();

        // Generate a fresh TVHeadend auth token via the API — TVH reachability is guaranteed by the
        // bootstrapped stack, so this must succeed.
        var tokenResp = await _fixture.Client.PostAsync("/TvHeadendApi/GenerateAuthToken", null);
        var tokenBody = await tokenResp.Content.ReadAsStringAsync();
        Assert.True(tokenResp.IsSuccessStatusCode, $"Expected success from GenerateAuthToken, got {(int)tokenResp.StatusCode}: {tokenBody}");

        using var tokenDoc = JsonDocument.Parse(tokenBody);
        var tokenSuccess = tokenDoc.RootElement.TryGetProperty("Success", out var s) && s.GetBoolean();
        Assert.True(tokenSuccess, $"GenerateAuthToken should succeed: {tokenBody}");

        // Re-read config to get the stored token.
        var pluginId = "ae9f5148-d656-43ab-ab83-94192f9e840a";
        using var configDoc = await GetJsonAsync($"/Plugins/{pluginId}/Configuration");
        var authToken = string.Empty;
        if (configDoc.RootElement.TryGetProperty("AuthToken", out var tokenProp))
        {
            authToken = tokenProp.GetString() ?? string.Empty;
        }

        Assert.False(string.IsNullOrEmpty(authToken), "AuthToken must be stored in plugin configuration after GenerateAuthToken.");

        var channelId = await TryGetFirstChannelUuidAsync();
        Assert.False(string.IsNullOrEmpty(channelId), "Bootstrapped stack must guarantee at least one channel.");

        // The TVHeadend AuthToken is NOT a relay token — relay tokens are plugin-internal HMAC
        // tokens minted per-request (see RelayStream_WithValidToken_StreamsBytes for the positive
        // path). The relay endpoint must reject this token as unrecognized (401).
        var response = await _fixture.AnonymousClient.GetAsync(
            $"/api/tvheadend/relay/stream/{channelId}?profile=pass&token={Uri.EscapeDataString(authToken)}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RelayTokenStream_WithInvalidToken_Returns401Or403()
    {
        SkipIfUnavailable();

        var channelId = await TryGetFirstChannelUuidAsync();
        Assert.False(string.IsNullOrEmpty(channelId), "Bootstrapped stack must guarantee at least one channel.");

        var response = await _fixture.AnonymousClient.GetAsync(
            $"/api/tvheadend/relay/stream/{channelId}?token=invalid-bogus-token");

        // A never-issued token is always classified as TokenNotFound/MalformedToken by
        // RelayAuthorizationHelper.MapToStatusCode, which both map to 401 — 403/410/502 are only
        // reachable for tokens that were actually issued and then revoked/expired/mismatched.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RelayTokenStream_WithMissingToken_Returns401()
    {
        SkipIfUnavailable();

        var channelId = await TryGetFirstChannelUuidAsync();
        Assert.False(string.IsNullOrEmpty(channelId), "Bootstrapped stack must guarantee at least one channel.");

        var response = await _fixture.AnonymousClient.GetAsync(
            $"/api/tvheadend/relay/stream/{channelId}");

        // A missing token is always classified as MissingToken by
        // RelayAuthorizationHelper.MapToStatusCode, which maps to 401.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RelayStream_WithValidToken_StreamsBytes()
    {
        SkipIfUnavailable();

        var channelId = await TryGetFirstChannelUuidAsync();
        Assert.False(string.IsNullOrEmpty(channelId), "Bootstrapped stack must guarantee at least one TVHeadend channel.");

        // Drive the plugin's real MediaSourceService/RelayUrlBuilder path via Jellyfin's own
        // PlaybackInfo endpoint so a genuine HMAC relay token gets minted — the TVHeadend AuthToken
        // (see RelayTokenStream_WithValidToken_Succeeds) is always rejected by the relay endpoint.
        var (tokenChannelId, profile, token) = await IssueRelayStreamTokenAsync();
        var tokenQuery = string.IsNullOrWhiteSpace(profile)
            ? $"token={Uri.EscapeDataString(token)}"
            : $"profile={Uri.EscapeDataString(profile)}&token={Uri.EscapeDataString(token)}";
        var relayPath = $"/api/tvheadend/relay/stream/{tokenChannelId}?{tokenQuery}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Positive path: the plugin-issued relay token must actually authorize a stream.
        using (var response = await _fixture.AnonymousClient.GetAsync(
            relayPath,
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token))
        {
            var errorBody = response.IsSuccessStatusCode ? string.Empty : await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"Expected success streaming with a valid relay token, got {(int)response.StatusCode}: {errorBody}");

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            Assert.True(
                contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
                contentType == "application/octet-stream",
                $"Expected video/* or application/octet-stream, got: {contentType}");

            using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new byte[65536];
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cts.Token);
                if (bytesRead == 0)
                {
                    break;
                }

                totalRead += bytesRead;
            }

            Assert.True(totalRead >= 65536, $"Expected >= 65536 bytes (64 KB) of continuous stream data within 30s, got {totalRead}.");
        }

        // Negative control: the identical request without the token query parameter must be rejected.
        var noTokenPath = string.IsNullOrWhiteSpace(profile)
            ? $"/api/tvheadend/relay/stream/{tokenChannelId}"
            : $"/api/tvheadend/relay/stream/{tokenChannelId}?profile={Uri.EscapeDataString(profile)}";

        var unauthorizedResponse = await _fixture.AnonymousClient.GetAsync(noTokenPath);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedResponse.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // J) Auth Enforcement
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AdminEndpoint_WithoutAuth_Returns401()
    {
        SkipIfUnavailable();

        var response = await _fixture.AnonymousClient.GetAsync("/TvHeadendApi/PluginInfo");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminEndpoint_WithAuth_Returns200()
    {
        SkipIfUnavailable();

        var response = await _fixture.Client.GetAsync("/TvHeadendApi/PluginInfo");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // K) Embedded Pages
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ConfigPage_IsServed()
    {
        SkipIfUnavailable();

        var response = await _fixture.Client.GetAsync("/web/configurationpage?name=TvHeadendApiConfig");

        // Jellyfin may route config pages under /web/ or root — try alternate path if not found.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response = await _fixture.Client.GetAsync("/configurationpage?name=TvHeadendApiConfig");
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        Assert.True(
            contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase),
            $"Expected HTML/JS content type, got: {contentType}");
    }

    [Fact]
    public async Task DashboardPage_IsServed()
    {
        SkipIfUnavailable();

        var response = await _fixture.Client.GetAsync("/web/configurationpage?name=TvHeadendDashboard");

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response = await _fixture.Client.GetAsync("/configurationpage?name=TvHeadendDashboard");
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        Assert.True(
            contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase),
            $"Expected HTML/JS content type, got: {contentType}");
    }
}
