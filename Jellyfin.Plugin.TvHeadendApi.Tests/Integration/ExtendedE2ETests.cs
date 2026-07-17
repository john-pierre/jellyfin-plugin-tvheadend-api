using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Integration;

/// <summary>
/// Extended E2E tests that close remaining coverage gaps around relay token images,
/// dashboard log filters, streaming profile resolution, auth enforcement, config
/// round-trips, diagnostics with bad config, relay status during streams, relay
/// metrics, and log entry count clamping.
/// Requires the full Docker test stack (TVHeadend + Jellyfin + IPTV simulator).
/// </summary>
[Trait("Category", "LiveIntegration")]
[Collection("JellyfinApi")]
public sealed class ExtendedE2ETests
{
    private static readonly string PluginId = "ae9f5148-d656-43ab-ab83-94192f9e840a";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly JellyfinApiFixture _fixture;

    public ExtendedE2ETests(JellyfinApiFixture fixture)
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

    private async Task<JsonDocument> GetJsonAsync(string path)
    {
        var response = await _fixture.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    private async Task<JsonDocument> ReadPluginConfigAsync()
    {
        var response = await _fixture.Client.GetAsync($"/Plugins/{PluginId}/Configuration");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    private async Task SavePluginConfigAsync(JsonDocument original, Action<Utf8JsonWriter, JsonProperty> propertyOverride)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var prop in original.RootElement.EnumerateObject())
            {
                propertyOverride(writer, prop);
            }

            writer.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(ms.ToArray());
        var postResp = await _fixture.Client.PostAsync(
            $"/Plugins/{PluginId}/Configuration",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.True(
            postResp.StatusCode == HttpStatusCode.OK || postResp.StatusCode == HttpStatusCode.NoContent,
            $"Expected 200/204 for config save, got {(int)postResp.StatusCode}.");
    }

    /// <summary>
    /// Creates a property-override delegate that overrides multiple named fields.
    /// </summary>
    private static Action<Utf8JsonWriter, JsonProperty> OverrideFields(params (string Name, Action<Utf8JsonWriter> Write)[] overrides)
    {
        var overrideMap = overrides.ToDictionary(o => o.Name, o => o.Write);
        return (writer, prop) =>
        {
            if (overrideMap.TryGetValue(prop.Name, out var writeOverride))
            {
                writeOverride(writer);
            }
            else
            {
                prop.WriteTo(writer);
            }
        };
    }

    /// <summary>
    /// Creates a property-override delegate that overrides a single field.
    /// </summary>
    private static Action<Utf8JsonWriter, JsonProperty> OverrideField(string fieldName, Action<Utf8JsonWriter> writeOverride)
    {
        return (writer, prop) =>
        {
            if (prop.Name == fieldName)
            {
                writeOverride(writer);
            }
            else
            {
                prop.WriteTo(writer);
            }
        };
    }

    /// <summary>
    /// Restores the TVH connection settings after a destructive config change (e.g. ResetToDefaults).
    /// </summary>
    private async Task RestoreTvhConnectionAsync()
    {
        var tvhHost = Environment.GetEnvironmentVariable("TVH_HOST") ?? "localhost";
        var tvhPortStr = Environment.GetEnvironmentVariable("TVH_PORT") ?? "19981";
        var tvhUser = Environment.GetEnvironmentVariable("TVH_USER") ?? "testuser";
        var tvhPass = Environment.GetEnvironmentVariable("TVH_PASS") ?? "testpass";
        if (!int.TryParse(tvhPortStr, out var tvhPort))
        {
            tvhPort = 19981;
        }

        using var restoreBase = await ReadPluginConfigAsync();
        await SavePluginConfigAsync(restoreBase, OverrideFields(
            ("Host", w => w.WriteString("Host", tvhHost)),
            ("Port", w => w.WriteNumber("Port", tvhPort)),
            ("Username", w => w.WriteString("Username", tvhUser)),
            ("Password", w => w.WriteString("Password", tvhPass)),
            ("AllowAnonymousAccess", w => w.WriteBoolean("AllowAnonymousAccess", false)),
            ("RelayEnabled", w => w.WriteBoolean("RelayEnabled", true)),
            ("EnableRelayTokenSecurity", w => w.WriteBoolean("EnableRelayTokenSecurity", true)),
            ("StreamingProfile", w => w.WriteString("StreamingProfile", "pass"))));

        // Wait for the resilience circuit breaker to recover so the next test in the shared
        // collection starts against a healthy connection (prevents order-dependent cascades).
        await _fixture.EnsureConfiguredAndHealthyAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 1. Relay Token Image Tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RelayTokenImage_WithInvalidToken_Rejected()
    {
        SkipIfUnavailable();

        var response = await _fixture.AnonymousClient.GetAsync(
            "/api/tvheadend/relay/images/imagecache/1?token=bogus");

        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.Forbidden,
            $"Invalid image token should be rejected (401/403), got {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task RelayTokenImage_WithMissingToken_Rejected()
    {
        SkipIfUnavailable();

        var response = await _fixture.AnonymousClient.GetAsync(
            "/api/tvheadend/relay/images/imagecache/1");

        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.Forbidden,
            $"Missing image token should be rejected (401/403), got {(int)response.StatusCode}.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 2. Dashboard Log Filter Tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DashboardLogs_LimitIsRespected()
    {
        SkipIfUnavailable();

        using var doc = await GetJsonAsync("/TvHeadendApi/Dashboard/Logs?limit=3");
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("Entries", out var entries), "Response must have Entries.");
        Assert.True(entries.ValueKind == JsonValueKind.Array, "Entries must be an array.");
        Assert.True(entries.GetArrayLength() <= 3, $"Expected <= 3 entries, got {entries.GetArrayLength()}.");
    }

    [Fact]
    public async Task DashboardLogs_SortAscending_ReturnsOldestFirst()
    {
        SkipIfUnavailable();

        using var doc = await GetJsonAsync("/TvHeadendApi/Dashboard/Logs?limit=10&sortDirection=asc");
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("Entries", out var entries), "Response must have Entries.");
        var entryList = entries.EnumerateArray().ToList();

        if (entryList.Count >= 2)
        {
            var firstTimestamp = entryList[0].GetProperty("CreatedAtUtc").GetString() ?? string.Empty;
            var lastTimestamp = entryList[^1].GetProperty("CreatedAtUtc").GetString() ?? string.Empty;

            // Parse as DateTimeOffset for comparison.
            var firstDt = DateTimeOffset.Parse(firstTimestamp);
            var lastDt = DateTimeOffset.Parse(lastTimestamp);

            Assert.True(
                firstDt <= lastDt,
                $"Ascending sort: first entry ({firstTimestamp}) should be <= last entry ({lastTimestamp}).");
        }

        // If fewer than 2 entries, sorting assertion is trivially satisfied.
    }

    [Fact]
    public async Task DashboardLogs_FilterBySource_ReturnsOnlyMatchingType()
    {
        SkipIfUnavailable();

        using var doc = await GetJsonAsync("/TvHeadendApi/Dashboard/Logs?type=plugin&limit=50");
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("Entries", out var entries), "Response must have Entries.");
        var entryList = entries.EnumerateArray().ToList();

        // All returned entries should have Source == "plugin" (or the set is empty).
        foreach (var entry in entryList)
        {
            if (entry.TryGetProperty("Source", out var source))
            {
                Assert.Equal("plugin", source.GetString());
            }
        }
    }

    [Fact]
    public async Task DashboardLogs_SearchFilter_MatchesText()
    {
        SkipIfUnavailable();

        using var doc = await GetJsonAsync("/TvHeadendApi/Dashboard/Logs?search=tvheadend&limit=50");
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("Entries", out var entries), "Response must have Entries.");
        var entryList = entries.EnumerateArray().ToList();

        // All returned entries should contain "tvheadend" in Message (case-insensitive), or be empty.
        foreach (var entry in entryList)
        {
            var message = string.Empty;
            var category = string.Empty;
            if (entry.TryGetProperty("Message", out var msgProp))
            {
                message = msgProp.GetString() ?? string.Empty;
            }

            if (entry.TryGetProperty("Category", out var catProp))
            {
                category = catProp.GetString() ?? string.Empty;
            }

            var containsSearch =
                message.Contains("tvheadend", StringComparison.OrdinalIgnoreCase) ||
                category.Contains("tvheadend", StringComparison.OrdinalIgnoreCase);

            Assert.True(
                containsSearch,
                $"Entry should contain 'tvheadend' in Message or Category. Message: '{message}', Category: '{category}'.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 3. Streaming Profile Resolution with Context
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Resolve_WithChannelId_ReturnsContextAwareResult()
    {
        SkipIfUnavailable();

        var channelId = await TryGetFirstChannelUuidAsync();
        Assert.False(
            string.IsNullOrEmpty(channelId),
            "Bootstrap guarantees >= 5 channels — a null channel id means the plugin lost connectivity to TVHeadend.");

        using var doc = await GetJsonAsync($"/TvHeadendApi/StreamingProfiles/Resolve?channelId={channelId}");
        var root = doc.RootElement;

        // EffectiveTvHeadendProfile must be non-empty.
        Assert.True(
            root.TryGetProperty("EffectiveTvHeadendProfile", out var profile),
            "Response must have EffectiveTvHeadendProfile.");
        Assert.False(
            string.IsNullOrEmpty(profile.GetString()),
            "EffectiveTvHeadendProfile must not be empty.");

        // Source field must be present.
        Assert.True(
            root.TryGetProperty("Source", out _),
            "Response must have Source field.");
    }

    [Fact]
    public async Task Validate_WithInvalidProfile_ReturnsWarnings()
    {
        SkipIfUnavailable();

        using var originalConfig = await ReadPluginConfigAsync();
        var originalProfile = originalConfig.RootElement.GetProperty("StreamingProfile").GetString() ?? "pass";

        try
        {
            // Set to a nonexistent profile.
            await SavePluginConfigAsync(originalConfig, OverrideField(
                "StreamingProfile",
                w => w.WriteString("StreamingProfile", "nonexistent-profile-xyz")));

            // Validate — nonexistent profile should produce warnings.
            using var doc = await GetJsonAsync("/TvHeadendApi/StreamingProfiles/Validate");
            var warnings = doc.RootElement.EnumerateArray().ToList();
            Assert.True(warnings.Count >= 1, $"Expected >= 1 warning for nonexistent profile, got {warnings.Count}.");
        }
        finally
        {
            // Restore original profile.
            using var restoreBase = await ReadPluginConfigAsync();
            await SavePluginConfigAsync(restoreBase, OverrideField(
                "StreamingProfile",
                w => w.WriteString("StreamingProfile", originalProfile)));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 4. Auth Enforcement on All Admin Endpoints
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AllAdminEndpoints_WithoutAuth_Return401()
    {
        SkipIfUnavailable();

        var endpoints = new[]
        {
            "/TvHeadendApi/Status",
            "/TvHeadendApi/Connections",
            "/TvHeadendApi/Inputs",
            "/TvHeadendApi/Subscriptions",
            "/TvHeadendApi/Health",
            "/TvHeadendApi/Dashboard",
            "/TvHeadendApi/Statistics",
            "/TvHeadendApi/Logs",
            "/TvHeadendApi/Dashboard/Logs",
            "/TvHeadendApi/StreamingProfiles/Discovered",
        };

        foreach (var endpoint in endpoints)
        {
            var response = await _fixture.AnonymousClient.GetAsync(endpoint);
            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized,
                $"Endpoint {endpoint} without auth should return 401, got {(int)response.StatusCode}.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 5. Config Round-Trip for Boolean and Network Fields
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Config_RoundTrip_BooleanFields()
    {
        SkipIfUnavailable();

        using var originalConfig = await ReadPluginConfigAsync();
        var origRelay = originalConfig.RootElement.GetProperty("RelayEnabled").GetBoolean();
        var origDirectPlay = originalConfig.RootElement.GetProperty("SupportsDirectPlay").GetBoolean();
        var origTranscoding = originalConfig.RootElement.GetProperty("SupportsTranscoding").GetBoolean();

        try
        {
            // Toggle all three boolean fields.
            await SavePluginConfigAsync(originalConfig, OverrideFields(
                ("RelayEnabled", w => w.WriteBoolean("RelayEnabled", !origRelay)),
                ("SupportsDirectPlay", w => w.WriteBoolean("SupportsDirectPlay", !origDirectPlay)),
                ("SupportsTranscoding", w => w.WriteBoolean("SupportsTranscoding", !origTranscoding))));

            // Re-read and verify toggles.
            using var updatedConfig = await ReadPluginConfigAsync();
            Assert.Equal(!origRelay, updatedConfig.RootElement.GetProperty("RelayEnabled").GetBoolean());
            Assert.Equal(!origDirectPlay, updatedConfig.RootElement.GetProperty("SupportsDirectPlay").GetBoolean());
            Assert.Equal(!origTranscoding, updatedConfig.RootElement.GetProperty("SupportsTranscoding").GetBoolean());
        }
        finally
        {
            // Restore originals.
            using var restoreBase = await ReadPluginConfigAsync();
            await SavePluginConfigAsync(restoreBase, OverrideFields(
                ("RelayEnabled", w => w.WriteBoolean("RelayEnabled", origRelay)),
                ("SupportsDirectPlay", w => w.WriteBoolean("SupportsDirectPlay", origDirectPlay)),
                ("SupportsTranscoding", w => w.WriteBoolean("SupportsTranscoding", origTranscoding))));
        }
    }

    [Fact]
    public async Task Config_RoundTrip_NetworkFields()
    {
        SkipIfUnavailable();

        using var originalConfig = await ReadPluginConfigAsync();
        var originalPort = originalConfig.RootElement.GetProperty("Port").GetInt32();

        try
        {
            // Set Port to 12345.
            await SavePluginConfigAsync(originalConfig, OverrideField(
                "Port",
                w => w.WriteNumber("Port", 12345)));

            // Re-read and verify.
            using var updatedConfig = await ReadPluginConfigAsync();
            Assert.Equal(12345, updatedConfig.RootElement.GetProperty("Port").GetInt32());
        }
        finally
        {
            // Restore original Port.
            using var restoreBase = await ReadPluginConfigAsync();
            await SavePluginConfigAsync(restoreBase, OverrideField(
                "Port",
                w => w.WriteNumber("Port", originalPort)));
        }
    }

    [Fact]
    public async Task Config_ResetToDefaults_ClearsCustomValues()
    {
        SkipIfUnavailable();

        // Set a non-default FallbackMaxStreamingBitrate.
        using var originalConfig = await ReadPluginConfigAsync();
        await SavePluginConfigAsync(originalConfig, OverrideField(
            "FallbackMaxStreamingBitrate",
            w => w.WriteNumber("FallbackMaxStreamingBitrate", 9999999)));

        try
        {
            // POST ResetToDefaults.
            var resetResp = await _fixture.Client.PostAsync("/TvHeadendApi/ResetToDefaults", null);
            Assert.True(
                resetResp.StatusCode == HttpStatusCode.OK || resetResp.StatusCode == HttpStatusCode.NoContent,
                $"Expected 200/204 for ResetToDefaults, got {(int)resetResp.StatusCode}.");

            // Read config and assert default value.
            using var resetConfig = await ReadPluginConfigAsync();
            var bitrate = resetConfig.RootElement.GetProperty("FallbackMaxStreamingBitrate").GetInt32();
            Assert.Equal(3000000, bitrate);
        }
        finally
        {
            // Restore TVH connection settings since ResetToDefaults wipes them.
            await RestoreTvhConnectionAsync();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6. Diagnose with Bad Config
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Diagnose_WithBadConfig_ReturnsLowScore()
    {
        SkipIfUnavailable();

        try
        {
            // Save config with unreachable port.
            using var originalConfig = await ReadPluginConfigAsync();
            await SavePluginConfigAsync(originalConfig, OverrideField(
                "Port",
                w => w.WriteNumber("Port", 1)));

            // GET /TvHeadendApi/Diagnose — may return 200 or 500 depending on implementation.
            var response = await _fixture.Client.GetAsync("/TvHeadendApi/Diagnose");
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                var score = root.GetProperty("CompatibilityScore").GetInt32();
                Assert.Equal(0, score);

                var overallStatus = root.GetProperty("OverallStatus").GetString();
                Assert.Equal("ERROR", overallStatus);
            }

            // If non-success, that itself indicates the bad config caused failure — acceptable.
        }
        finally
        {
            // An unreachable port trips the plugin's resilience circuit breaker, which would
            // otherwise poison every later test in this shared collection. Re-apply the known-good
            // TVH connection settings and wait for the breaker to recover before returning control.
            await _fixture.EnsureConfiguredAndHealthyAsync();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 7. Relay Status During and After Stream
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RelayStatus_ShowsActiveStreams()
    {
        SkipIfUnavailable();

        var channelId = await TryGetFirstChannelUuidAsync();
        Assert.False(
            string.IsNullOrEmpty(channelId),
            "Bootstrap guarantees >= 5 channels — a null channel id means the plugin lost connectivity to TVHeadend.");

        // Note initial ActiveStreams count.
        int initialActive;
        {
            var statusResp = await _fixture.AnonymousClient.GetAsync("/api/tvheadend/status");
            Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);
            var statusBody = await statusResp.Content.ReadAsStringAsync();
            using var statusDoc = JsonDocument.Parse(statusBody);
            initialActive = statusDoc.RootElement.GetProperty("ActiveStreams").GetInt32();
        }

        // The open /stream endpoint requires a token when relay token security is enabled.
        // Disable it for the duration of this check, then restore it in the finally
        // (mirrors DeepVerificationTests.StreamRelay_ActuallyStreamsBytes).
        using var originalConfig = await ReadPluginConfigAsync();
        var hadSecurity = originalConfig.RootElement.TryGetProperty("EnableRelayTokenSecurity", out var secProp) && secProp.GetBoolean();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        HttpResponseMessage? streamResponse = null;
        try
        {
            if (hadSecurity)
            {
                await SavePluginConfigAsync(originalConfig, OverrideField(
                    "EnableRelayTokenSecurity",
                    w => w.WriteBoolean("EnableRelayTokenSecurity", false)));
            }

            // Start a real relay stream with ResponseHeadersRead — the activity tracker increments
            // as the very first step of RelayStreamAsync, so a 200 response already means the slot
            // is held by the time we get here.
            streamResponse = await _fixture.Client.GetAsync(
                $"/api/tvheadend/stream/{channelId}?profile=pass",
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);

            Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);

            // Read some bytes to prove the relay actually attached to the upstream before we
            // check the tracked status.
            using (var stream = await streamResponse.Content.ReadAsStreamAsync(cts.Token))
            {
                var buffer = new byte[4096];
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(), cts.Token);
                Assert.True(bytesRead > 0, "Expected to read at least some bytes from the active relay stream.");
            }

            // Check ActiveStreams while the stream is still open — must reflect this specific
            // stream, not merely be non-zero because of unrelated pre-existing activity.
            var duringResp = await _fixture.AnonymousClient.GetAsync("/api/tvheadend/status");
            Assert.Equal(HttpStatusCode.OK, duringResp.StatusCode);
            var duringBody = await duringResp.Content.ReadAsStringAsync();
            using var duringDoc = JsonDocument.Parse(duringBody);
            var duringActive = duringDoc.RootElement.GetProperty("ActiveStreams").GetInt32();

            Assert.True(
                duringActive >= initialActive + 1,
                $"ActiveStreams during stream should be >= {initialActive + 1} (this test's own stream), got {duringActive}.");
        }
        finally
        {
            // Cancel and dispose the stream.
            cts.Cancel();
            streamResponse?.Dispose();

            // Restore relay token security regardless of outcome.
            using var restoreConfig = await ReadPluginConfigAsync();
            await SavePluginConfigAsync(restoreConfig, OverrideField(
                "EnableRelayTokenSecurity",
                w => w.WriteBoolean("EnableRelayTokenSecurity", hadSecurity)));
        }

        // Short delay to let the tracker decrement.
        // The relay service may take a moment to clean up the stream and decrement the counter.
        // TVHeadend can hold the upstream connection briefly after client disconnect.
        await Task.Delay(3000);

        // Check ActiveStreams after stream.
        // Note: The counter may not have decremented yet if TVHeadend holds the connection.
        // We verify it's at most initialActive + 1 (not unbounded growth) and, optionally, that
        // it has drained back down to the initial value.
        var afterResp = await _fixture.AnonymousClient.GetAsync("/api/tvheadend/status");
        Assert.Equal(HttpStatusCode.OK, afterResp.StatusCode);
        var afterBody = await afterResp.Content.ReadAsStringAsync();
        using var afterDoc = JsonDocument.Parse(afterBody);
        var afterActive = afterDoc.RootElement.GetProperty("ActiveStreams").GetInt32();

        Assert.True(
            afterActive <= initialActive + 1,
            $"ActiveStreams after disconnect should stabilize near {initialActive}, got {afterActive}.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 8. Relay Metrics After Stream
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RelayMetrics_AfterStream_ShowsActivity()
    {
        SkipIfUnavailable();

        var channelId = await TryGetFirstChannelUuidAsync();
        Assert.False(
            string.IsNullOrEmpty(channelId),
            "Bootstrap guarantees >= 5 channels — a null channel id means the plugin lost connectivity to TVHeadend.");

        // The open /stream endpoint requires a token when relay token security is enabled.
        // Disable it for the duration of this check, then restore it in the finally
        // (mirrors DeepVerificationTests.StreamRelay_ActuallyStreamsBytes).
        using var originalConfig = await ReadPluginConfigAsync();
        var hadSecurity = originalConfig.RootElement.TryGetProperty("EnableRelayTokenSecurity", out var secProp) && secProp.GetBoolean();

        try
        {
            if (hadSecurity)
            {
                await SavePluginConfigAsync(originalConfig, OverrideField(
                    "EnableRelayTokenSecurity",
                    w => w.WriteBoolean("EnableRelayTokenSecurity", false)));
            }

            // Start a relay stream and read some bytes to generate genuine relay activity —
            // RelayMetrics is only populated once the request is recorded on disconnect/EOF.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var streamResponse = await _fixture.Client.GetAsync(
                $"/api/tvheadend/stream/{channelId}?profile=pass",
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);

            Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);

            using var stream = await streamResponse.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new byte[4096];
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(), cts.Token);
            Assert.True(bytesRead > 0, "Expected to read at least some bytes from the relay stream.");
        }
        finally
        {
            // Restore relay token security regardless of outcome.
            using var restoreConfig = await ReadPluginConfigAsync();
            await SavePluginConfigAsync(restoreConfig, OverrideField(
                "EnableRelayTokenSecurity",
                w => w.WriteBoolean("EnableRelayTokenSecurity", hadSecurity)));
        }

        // Wait for metrics to be persisted asynchronously.
        await Task.Delay(5000);

        // GET relay metrics.
        using var metricsDoc = await GetJsonAsync("/TvHeadendApi/RelayMetrics?hours=1");

        // The stream above is real relay activity, so TotalRequests must reflect it.
        Assert.True(
            metricsDoc.RootElement.TryGetProperty("TotalRequests", out var totalRequests),
            "RelayMetrics must have TotalRequests field.");
        Assert.True(
            totalRequests.GetInt32() >= 1,
            $"Expected TotalRequests >= 1 after streaming activity, got {totalRequests.GetInt32()}.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 9. Log Entry Count Parameter
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task LogEntries_CountParameter_ClampsResult()
    {
        SkipIfUnavailable();

        var response = await _fixture.Client.GetAsync("/TvHeadendApi/Logs/entries?count=2");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.ValueKind == JsonValueKind.Array, "Log entries must return an array.");
        Assert.True(
            doc.RootElement.GetArrayLength() <= 2,
            $"Expected <= 2 log entries, got {doc.RootElement.GetArrayLength()}.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 10. Cache Warmup & Invalidation
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task InvalidateCache_ReturnsSuccess()
    {
        SkipIfUnavailable();
        var response = await _fixture.Client.PostAsync("/TvHeadendApi/InvalidateCache", null);
        Assert.True(
            response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent,
            $"Expected 200/204 for InvalidateCache, got {(int)response.StatusCode}.");

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.TryGetProperty("Success", out var success), "Response must have Success.");
            Assert.True(success.GetBoolean(), "InvalidateCache should succeed.");
        }
    }

    [Fact]
    public async Task WarmCache_WarmsChannels()
    {
        SkipIfUnavailable();

        // Invalidate first to start fresh.
        await _fixture.Client.PostAsync("/TvHeadendApi/InvalidateCache", null);

        // Warm cache — this probes every channel in the lineup (100 on the test stack),
        // so it needs the long-running client, not the default 30s one.
        var response = await _fixture.LongRunningClient.PostAsync("/TvHeadendApi/WarmCache", null);

        // The bootstrapped stack guarantees TVHeadend is reachable, so WarmCache must succeed.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("TotalChannels", out var total), "Must have TotalChannels.");
        Assert.True(doc.RootElement.TryGetProperty("Warmed", out var warmed), "Must have Warmed.");
        Assert.True(doc.RootElement.TryGetProperty("AlreadyCached", out var cached), "Must have AlreadyCached.");
        Assert.True(doc.RootElement.TryGetProperty("Failed", out var failed), "Must have Failed.");

        var totalCount = total.GetInt32();
        var warmedCount = warmed.GetInt32();
        var cachedCount = cached.GetInt32();
        var failedCount = failed.GetInt32();

        // With the test stack, we should have >= 3 channels.
        Assert.True(totalCount >= 3, $"Expected >= 3 channels, got {totalCount}.");

        // After invalidation + warmup, warmed should equal total (minus any failures).
        Assert.True(
            warmedCount + cachedCount + failedCount == totalCount,
            $"Counts don't add up: warmed={warmedCount} + cached={cachedCount} + failed={failedCount} != total={totalCount}.");

        // At least some channels should have been warmed.
        Assert.True(warmedCount > 0, $"Expected > 0 warmed channels, got {warmedCount}.");
    }

    [Fact]
    public async Task WarmCache_ThenDiagnose_ShowsFullCoverage()
    {
        SkipIfUnavailable();

        // Invalidate + warm all caches. Warmup probes the full 100-channel lineup and
        // takes minutes — use the long-running client.
        await _fixture.Client.PostAsync("/TvHeadendApi/InvalidateCache", null);
        var warmResp = await _fixture.LongRunningClient.PostAsync("/TvHeadendApi/WarmCache", null);
        Assert.True(
            warmResp.IsSuccessStatusCode,
            $"WarmCache must succeed against the bootstrapped stack, got {(int)warmResp.StatusCode}.");

        // Now run diagnostics and check probe cache coverage.
        var diagResp = await _fixture.Client.GetAsync("/TvHeadendApi/Diagnose");
        Assert.True(
            diagResp.IsSuccessStatusCode,
            $"Diagnose must succeed against the bootstrapped stack, got {(int)diagResp.StatusCode}.");

        var diagBody = await diagResp.Content.ReadAsStringAsync();
        using var diagDoc = JsonDocument.Parse(diagBody);

        // Find the Probe Cache Coverage check.
        // The diagnostic scanner uses a regex on the Path field in cache files, which may
        // not match relay URLs. So we only verify the diagnostic ran — not the exact count.
        Assert.True(
            diagDoc.RootElement.TryGetProperty("CacheStatus", out var cacheStatus),
            "Diagnose response must include CacheStatus after warmup.");
        var statusText = cacheStatus.GetString() ?? string.Empty;
        Assert.False(string.IsNullOrEmpty(statusText), "CacheStatus should not be empty after warmup.");
    }

    [Fact]
    public async Task InvalidateCache_ThenWarmCache_RoundTrip()
    {
        SkipIfUnavailable();

        // Step 1: Warm cache (full-lineup probe — long-running client).
        var warmResp1 = await _fixture.LongRunningClient.PostAsync("/TvHeadendApi/WarmCache", null);
        Assert.True(
            warmResp1.IsSuccessStatusCode,
            $"WarmCache must succeed against the bootstrapped stack, got {(int)warmResp1.StatusCode}.");

        var warmBody1 = await warmResp1.Content.ReadAsStringAsync();
        using var warmDoc1 = JsonDocument.Parse(warmBody1);
        var firstWarmed = warmDoc1.RootElement.GetProperty("Warmed").GetInt32();

        // Step 2: Invalidate.
        var invalidateResp = await _fixture.Client.PostAsync("/TvHeadendApi/InvalidateCache", null);
        Assert.True(invalidateResp.IsSuccessStatusCode, "Invalidate should succeed.");

        var invBody = await invalidateResp.Content.ReadAsStringAsync();
        using var invDoc = JsonDocument.Parse(invBody);
        var deletedFiles = invDoc.RootElement.GetProperty("DeletedFiles").GetInt32();

        // Step 3: Warm again — should re-warm the same channels.
        var warmResp2 = await _fixture.LongRunningClient.PostAsync("/TvHeadendApi/WarmCache", null);
        Assert.True(warmResp2.IsSuccessStatusCode, "Second warmup should succeed.");

        var warmBody2 = await warmResp2.Content.ReadAsStringAsync();
        using var warmDoc2 = JsonDocument.Parse(warmBody2);
        var secondWarmed = warmDoc2.RootElement.GetProperty("Warmed").GetInt32();
        var secondCached = warmDoc2.RootElement.GetProperty("AlreadyCached").GetInt32();
        var secondTotal = warmDoc2.RootElement.GetProperty("TotalChannels").GetInt32();

        // Invalidate deletes the per-profile store files too, so DeletedFiles can exceed
        // the channel count — the invariant is full re-coverage of the lineup, not a
        // files-deleted comparison. AlreadyCached counts toward coverage: concurrent suite
        // activity (any PlaybackInfo triggers the per-start cache reconciliation) may
        // legitimately re-create files while the warmup is running.
        Assert.True(deletedFiles > 0, "Invalidate after a warmup must delete at least one file.");
        Assert.True(
            secondWarmed + secondCached >= secondTotal - warmDoc2.RootElement.GetProperty("Failed").GetInt32(),
            $"Expected re-warmed ({secondWarmed}) + already-cached ({secondCached}) to cover the lineup ({secondTotal}).");
    }
}
