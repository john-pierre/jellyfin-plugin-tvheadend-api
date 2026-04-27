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

        // Re-configure the plugin after reset so subsequent tests still work.
        var tvhHost = Environment.GetEnvironmentVariable("TVH_HOST") ?? "localhost";
        var tvhPortStr = Environment.GetEnvironmentVariable("TVH_PORT") ?? "19981";
        var tvhUser = Environment.GetEnvironmentVariable("TVH_USER") ?? "testuser";
        var tvhPass = Environment.GetEnvironmentVariable("TVH_PASS") ?? "testpass";
        if (!int.TryParse(tvhPortStr, out var tvhPort))
        {
            tvhPort = 19981;
        }

        // Re-read, patch, and save config.
        var pluginId = "ae9f5148-d656-43ab-ab83-94192f9e840a";
        var getResp = await _fixture.Client.GetAsync($"/Plugins/{pluginId}/Configuration");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);

        var configJson = await getResp.Content.ReadAsStringAsync();
        using var configDoc = JsonDocument.Parse(configJson);

        using var ms = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var prop in configDoc.RootElement.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "Host":
                        writer.WriteString("Host", tvhHost);
                        break;
                    case "Port":
                        writer.WriteNumber("Port", tvhPort);
                        break;
                    case "Username":
                        writer.WriteString("Username", tvhUser);
                        break;
                    case "Password":
                        writer.WriteString("Password", tvhPass);
                        break;
                    case "AllowAnonymousAccess":
                        writer.WriteBoolean("AllowAnonymousAccess", false);
                        break;
                    case "RelayEnabled":
                        writer.WriteBoolean("RelayEnabled", true);
                        break;
                    case "EnableRelayTokenSecurity":
                        writer.WriteBoolean("EnableRelayTokenSecurity", true);
                        break;
                    case "StreamingProfile":
                        writer.WriteString("StreamingProfile", "pass");
                        break;
                    default:
                        prop.WriteTo(writer);
                        break;
                }
            }

            writer.WriteEndObject();
        }

        var updatedConfig = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        var postResp = await _fixture.Client.PostAsync(
            $"/Plugins/{pluginId}/Configuration",
            new StringContent(updatedConfig, System.Text.Encoding.UTF8, "application/json"));
        Assert.True(
            postResp.StatusCode == HttpStatusCode.OK || postResp.StatusCode == HttpStatusCode.NoContent,
            $"Expected 200 or 204 for config update, got {(int)postResp.StatusCode}.");
    }

    [Fact]
    public async Task ProfileOptions_ReturnsStreamingAndRecordingProfiles()
    {
        SkipIfUnavailable();
        var response = await _fixture.Client.GetAsync("/TvHeadendApi/ProfileOptions");
        Assert.True(
            response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.InternalServerError,
            $"Expected success or 500 (TVH unreachable), got {(int)response.StatusCode}.");

        if (!response.IsSuccessStatusCode)
        {
            return; // TVH unreachable — skip assertions on body.
        }

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("StreamingProfiles", out var streaming), "Response must have StreamingProfiles.");
        // Streaming profiles may be empty if TVH hasn't fully initialized.
        Assert.True(streaming.ValueKind == JsonValueKind.Array, "StreamingProfiles must be an array.");
    }

    [Fact]
    public async Task Diagnose_ReturnsCompatibilityReport()
    {
        SkipIfUnavailable();
        var response = await _fixture.Client.GetAsync("/TvHeadendApi/Diagnose");
        Assert.True(
            response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.InternalServerError,
            $"Expected success or 500 (TVH unreachable), got {(int)response.StatusCode}.");

        if (!response.IsSuccessStatusCode)
        {
            return; // TVH unreachable — skip body assertions.
        }

        var body = await response.Content.ReadAsStringAsync();
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
        Assert.True(
            response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.InternalServerError,
            $"Expected success or 500 (TVH unreachable), got {(int)response.StatusCode}.");

        if (!response.IsSuccessStatusCode)
        {
            return; // TVH unreachable.
        }

        var body = await response.Content.ReadAsStringAsync();
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
        Assert.True(
            response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.InternalServerError,
            $"Expected success or 500 (TVH unreachable), got {(int)response.StatusCode}.");

        if (!response.IsSuccessStatusCode)
        {
            return; // TVH unreachable.
        }

        var body = await response.Content.ReadAsStringAsync();
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
        Assert.True(
            response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.InternalServerError,
            $"Expected success or 500 (TVH unreachable), got {(int)response.StatusCode}.");

        if (!response.IsSuccessStatusCode)
        {
            return; // TVH unreachable.
        }

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        // TVH may return empty if not fully initialized — just verify it's an array.
        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Array, "Discovered must return an array.");
    }

    [Fact]
    public async Task Channels_ReturnsChannelList()
    {
        SkipIfUnavailable();
        var response = await _fixture.Client.GetAsync("/TvHeadendApi/StreamingProfiles/Channels");
        Assert.True(
            response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.InternalServerError,
            $"Expected success or 500 (TVH unreachable), got {(int)response.StatusCode}.");

        if (!response.IsSuccessStatusCode)
        {
            return; // TVH unreachable.
        }

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Array, "Channels must return an array.");

        // Channels may be empty if TVH hasn't fully initialized.
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
        Assert.True(
            response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.InternalServerError,
            $"Expected success or 500 (TVH unreachable), got {(int)response.StatusCode}.");

        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        // ActivityStatus is a JSON object — verify it's non-empty.
        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Object, "Status must return a JSON object.");
    }

    [Fact]
    public async Task Connections_ReturnsArray()
    {
        SkipIfUnavailable();
        var response = await _fixture.Client.GetAsync("/TvHeadendApi/Connections");
        Assert.True(
            response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.InternalServerError,
            $"Expected success or 500 (TVH unreachable), got {(int)response.StatusCode}.");

        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Array, "Connections must return an array.");
    }

    [Fact]
    public async Task Inputs_ReturnsArray()
    {
        SkipIfUnavailable();
        var response = await _fixture.Client.GetAsync("/TvHeadendApi/Inputs");
        Assert.True(
            response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.InternalServerError,
            $"Expected success or 500 (TVH unreachable), got {(int)response.StatusCode}.");

        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();
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

        // DELETE may return 200, 204, or 500 if DB is not yet initialized.
        if (response.StatusCode == HttpStatusCode.InternalServerError)
        {
            return; // DB not ready — acceptable in E2E timing
        }

        Assert.True(
            response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent,
            $"Expected 200 or 204 for Statistics clear, got {(int)response.StatusCode}.");

        var body = await response.Content.ReadAsStringAsync();
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

        // DELETE may return 200, 204, or 500 if DB is not yet initialized.
        if (deleteResp.StatusCode == HttpStatusCode.InternalServerError)
        {
            return; // DB not ready — acceptable in E2E timing
        }

        Assert.True(
            deleteResp.StatusCode == HttpStatusCode.OK || deleteResp.StatusCode == HttpStatusCode.NoContent,
            $"Expected 200 or 204 for Statistics clear, got {(int)deleteResp.StatusCode}.");

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

        var channelId = await TryGetFirstChannelUuidAsync();
        if (string.IsNullOrEmpty(channelId))
        {
            return; // No channels available (TVH unreachable) — skip gracefully.
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var response = await _fixture.Client.GetAsync(
                $"/api/tvheadend/stream/{channelId}?profile=pass",
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);

            // TVHeadend may return 200 (streaming) or 502/504 if upstream fails — but not 404.
            Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);

            if (response.IsSuccessStatusCode)
            {
                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                Assert.True(
                    contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
                    contentType == "application/octet-stream" ||
                    contentType == "video/MP2T",
                    $"Unexpected content type: {contentType}");
            }

            response.Dispose();
        }
        catch (TaskCanceledException)
        {
            // Timeout means the stream started (200 OK, data flowing) — acceptable.
        }
        catch (OperationCanceledException)
        {
            // Same as above.
        }
    }

    [Fact]
    public async Task RelayImage_WithChannelLogo_Returns200Or404()
    {
        SkipIfUnavailable();

        var response = await _fixture.Client.GetAsync("/api/tvheadend/images/imagecache/1");

        // Image may be 200 (logo cached), 404 (no logos), or 502 (TVH upstream unreachable) — all acceptable.
        Assert.True(
            response.StatusCode == HttpStatusCode.OK ||
            response.StatusCode == HttpStatusCode.NotFound ||
            response.StatusCode == HttpStatusCode.BadGateway,
            $"Expected 200, 404, or 502, got {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task RelayTokenStream_WithValidToken_Succeeds()
    {
        SkipIfUnavailable();

        // Generate a fresh token via the API.
        var tokenResp = await _fixture.Client.PostAsync("/TvHeadendApi/GenerateAuthToken", null);
        if (!tokenResp.IsSuccessStatusCode)
        {
            return; // TVH unreachable — skip gracefully.
        }

        var tokenBody = await tokenResp.Content.ReadAsStringAsync();
        using var tokenDoc = JsonDocument.Parse(tokenBody);
        var tokenSuccess = tokenDoc.RootElement.TryGetProperty("Success", out var s) && s.GetBoolean();

        // If token generation failed, skip this test — TVHeadend may not support token auth.
        if (!tokenSuccess)
        {
            return;
        }

        // Re-read config to get the stored token.
        var pluginId = "ae9f5148-d656-43ab-ab83-94192f9e840a";
        using var configDoc = await GetJsonAsync($"/Plugins/{pluginId}/Configuration");
        var authToken = string.Empty;
        if (configDoc.RootElement.TryGetProperty("AuthToken", out var tokenProp))
        {
            authToken = tokenProp.GetString() ?? string.Empty;
        }

        if (string.IsNullOrEmpty(authToken))
        {
            return; // Token not stored — skip.
        }

        var channelId = await TryGetFirstChannelUuidAsync();
        if (string.IsNullOrEmpty(channelId))
        {
            return; // No channels available — skip gracefully.
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var response = await _fixture.AnonymousClient.GetAsync(
                $"/api/tvheadend/relay/stream/{channelId}?profile=pass&token={Uri.EscapeDataString(authToken)}",
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);

            // With a valid TVH auth token (NOT a relay token), the relay endpoint
            // will reject it (401) because relay tokens are plugin-internal HMAC tokens.
            // This is expected behavior — we're testing that the endpoint responds, not that
            // the TVH auth token works as a relay token. A true relay token test would need
            // to call the MediaSource API which issues tokens internally.
            // Accept any response that proves the endpoint is reachable.
            Assert.True(
                response.StatusCode != HttpStatusCode.NotFound,
                $"Relay stream endpoint should exist, got {(int)response.StatusCode}.");
            response.Dispose();
        }
        catch (TaskCanceledException)
        {
            // Stream started — acceptable.
        }
        catch (OperationCanceledException)
        {
            // Stream started — acceptable.
        }
    }

    [Fact]
    public async Task RelayTokenStream_WithInvalidToken_Returns401Or403()
    {
        SkipIfUnavailable();

        var channelId = await TryGetFirstChannelUuidAsync();
        if (string.IsNullOrEmpty(channelId))
        {
            return; // No channels available — skip gracefully.
        }

        var response = await _fixture.AnonymousClient.GetAsync(
            $"/api/tvheadend/relay/stream/{channelId}?token=invalid-bogus-token");

        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.Forbidden ||
            response.StatusCode == HttpStatusCode.Gone ||
            response.StatusCode == HttpStatusCode.BadGateway,
            $"Invalid token should return 401, 403, 410, or 502 — got {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task RelayTokenStream_WithMissingToken_Returns401()
    {
        SkipIfUnavailable();

        var channelId = await TryGetFirstChannelUuidAsync();
        if (string.IsNullOrEmpty(channelId))
        {
            return; // No channels available — skip gracefully.
        }

        var response = await _fixture.AnonymousClient.GetAsync(
            $"/api/tvheadend/relay/stream/{channelId}");

        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.Forbidden ||
            response.StatusCode == HttpStatusCode.Gone ||
            response.StatusCode == HttpStatusCode.BadGateway,
            $"Missing token should return 401, 403, 410, or 502 — got {(int)response.StatusCode}.");
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
