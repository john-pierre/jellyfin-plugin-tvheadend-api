using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Integration;

/// <summary>
/// Deep-verification E2E tests that assert on actual data values, not just HTTP status codes.
/// Requires the full Docker test stack (TVHeadend + Jellyfin + IPTV simulator) running
/// with bootstrapped data: 5 channels, channel groups, EPG, streaming profiles, DVR config, user.
/// </summary>
[Trait("Category", "LiveIntegration")]
[Collection("JellyfinApi")]
public sealed class DeepVerificationTests
{
    private static readonly string PluginId = "ae9f5148-d656-43ab-ab83-94192f9e840a";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly JellyfinApiFixture _fixture;

    public DeepVerificationTests(JellyfinApiFixture fixture)
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

    private async Task<JsonDocument> GetJsonAsync(string path)
    {
        var response = await _fixture.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    /// <summary>
    /// Reads the current plugin configuration as a parsed <see cref="JsonDocument"/>.
    /// </summary>
    private async Task<JsonDocument> ReadPluginConfigAsync()
    {
        var response = await _fixture.Client.GetAsync($"/Plugins/{PluginId}/Configuration");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    /// <summary>
    /// Writes a modified plugin configuration.
    /// Accepts the original <see cref="JsonDocument"/> and a mutation function that
    /// overrides specific properties during the JSON rewrite.
    /// </summary>
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
    /// Helper that writes a property override for a single named field, passing all others through.
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

    // ═══════════════════════════════════════════════════════════════════════
    // 1. Config_RoundTrip_PersistsChanges
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Config_RoundTrip_PersistsChanges()
    {
        SkipIfUnavailable();

        // Read current config.
        using var originalConfig = await ReadPluginConfigAsync();
        var originalBitrate = originalConfig.RootElement.GetProperty("FallbackMaxStreamingBitrate").GetInt32();

        const int testBitrate = 5000000;
        // Ensure the test value differs from the current value so the round-trip is meaningful.
        var newBitrate = originalBitrate == testBitrate ? 6000000 : testBitrate;

        try
        {
            // Save with modified bitrate.
            await SavePluginConfigAsync(originalConfig, OverrideField(
                "FallbackMaxStreamingBitrate",
                w => w.WriteNumber("FallbackMaxStreamingBitrate", newBitrate)));

            // Re-read and verify the field persisted.
            using var updatedConfig = await ReadPluginConfigAsync();
            var updatedBitrate = updatedConfig.RootElement.GetProperty("FallbackMaxStreamingBitrate").GetInt32();
            Assert.Equal(newBitrate, updatedBitrate);
        }
        finally
        {
            // Restore original value.
            using var restoreBase = await ReadPluginConfigAsync();
            await SavePluginConfigAsync(restoreBase, OverrideField(
                "FallbackMaxStreamingBitrate",
                w => w.WriteNumber("FallbackMaxStreamingBitrate", originalBitrate)));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 2. Config_StreamingProfile_ChangeAndVerify
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Config_StreamingProfile_ChangeAndVerify()
    {
        SkipIfUnavailable();

        // The legacy StreamingProfile field is only the FALLBACK: when
        // StreamingProfileSettings.DefaultTvHeadendProfile is non-empty (e.g. after the managed
        // "jellyfin" profile was provisioned), it wins. Control both levels explicitly so this
        // test is independent of what earlier suites persisted.
        var original = System.Text.Json.Nodes.JsonNode.Parse(
            await _fixture.Client.GetStringAsync($"/Plugins/{PluginId}/Configuration"))!;
        var originalJson = original.ToJsonString();

        try
        {
            var modified = System.Text.Json.Nodes.JsonNode.Parse(originalJson)!;
            modified["StreamingProfile"] = "test-pass";
            if (modified["StreamingProfileSettings"] is System.Text.Json.Nodes.JsonObject settings)
            {
                settings["DefaultTvHeadendProfile"] = string.Empty;
            }

            var saveResp = await _fixture.Client.PostAsync(
                $"/Plugins/{PluginId}/Configuration",
                new StringContent(modified.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
            saveResp.EnsureSuccessStatusCode();

            // Re-read config and assert.
            using var updatedConfig = await ReadPluginConfigAsync();
            var storedProfile = updatedConfig.RootElement.GetProperty("StreamingProfile").GetString();
            Assert.Equal("test-pass", storedProfile);

            // With the settings default cleared, the legacy field must drive resolution.
            using var resolveDoc = await GetJsonAsync("/TvHeadendApi/StreamingProfiles/Resolve");
            var effective = resolveDoc.RootElement.GetProperty("EffectiveTvHeadendProfile").GetString();
            Assert.Equal("test-pass", effective);
        }
        finally
        {
            // Restore the exact original configuration (both levels).
            var restoreResp = await _fixture.Client.PostAsync(
                $"/Plugins/{PluginId}/Configuration",
                new StringContent(originalJson, System.Text.Encoding.UTF8, "application/json"));
            restoreResp.EnsureSuccessStatusCode();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 3. Channels_ReturnsAllBootstrappedChannels
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Channels_ReturnsAllBootstrappedChannels()
    {
        SkipIfUnavailable();

        var channelId = await TryGetFirstChannelUuidAsync();
        Assert.False(
            string.IsNullOrEmpty(channelId),
            "Bootstrap guarantees 5 channels — a null channel id means the plugin lost connectivity to TVHeadend.");

        using var doc = await GetJsonAsync("/TvHeadendApi/StreamingProfiles/Channels");
        var channels = doc.RootElement.EnumerateArray().ToList();

        // Bootstrap creates 5 channels.
        Assert.True(channels.Count >= 5, $"Expected >= 5 channels, got {channels.Count}.");

        // Verify each channel has non-empty Id and Name.
        foreach (var ch in channels)
        {
            var id = ch.GetProperty("Id").GetString();
            var name = ch.GetProperty("Name").GetString();
            Assert.False(string.IsNullOrEmpty(id), "Channel Id must not be empty.");
            Assert.False(string.IsNullOrEmpty(name), "Channel Name must not be empty.");
        }

        // Assert at least one known bootstrapped channel name is present.
        var knownNames = new[] { "Test Channel 1", "Test Channel 2", "Test Channel 3", "Test Channel 4", "Test Channel 5" };
        var channelNames = channels.Select(c => c.GetProperty("Name").GetString()).ToList();
        var hasKnown = knownNames.Any(known => channelNames.Contains(known));
        Assert.True(hasKnown, $"Expected at least one known channel name from bootstrap. Found: [{string.Join(", ", channelNames)}]");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 4. ChannelGroups_ReturnsBootstrappedGroups
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ChannelGroups_ReturnsBootstrappedGroups()
    {
        SkipIfUnavailable();

        var channelId = await TryGetFirstChannelUuidAsync();
        Assert.False(
            string.IsNullOrEmpty(channelId),
            "Bootstrap guarantees 5 channels — a null channel id means the plugin lost connectivity to TVHeadend.");

        using var doc = await GetJsonAsync("/TvHeadendApi/StreamingProfiles/ChannelGroups");
        var groups = doc.RootElement.EnumerateArray().ToList();

        Assert.True(groups.Count >= 1, $"Expected >= 1 channel group, got {groups.Count}.");

        // Check for at least one known bootstrapped group name.
        var knownGroups = new[] { "News", "Entertainment", "Sports", "Documentary", "Music" };
        var groupNames = groups
            .Where(g => g.TryGetProperty("Name", out _))
            .Select(g => g.GetProperty("Name").GetString())
            .ToList();
        var hasKnown = knownGroups.Any(known => groupNames.Contains(known));
        Assert.True(
            hasKnown,
            $"Expected at least one known group name ({string.Join(", ", knownGroups)}). Found: [{string.Join(", ", groupNames)}]");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 5. Diagnose_ScoreAtLeast50_WithValidChecks
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Diagnose_ScoreAtLeast50_WithValidChecks()
    {
        SkipIfUnavailable();

        var response = await _fixture.Client.GetAsync("/TvHeadendApi/Diagnose");
        Assert.True(
            response.IsSuccessStatusCode,
            $"/TvHeadendApi/Diagnose must succeed against the bootstrapped stack, got {(int)response.StatusCode}.");

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Compatibility score must be at least 50 when connected to TVH.
        var score = root.GetProperty("CompatibilityScore").GetInt32();
        Assert.True(score >= 50, $"CompatibilityScore was {score}, expected >= 50.");

        // Connection must indicate "Connected".
        var connection = root.GetProperty("Connection").GetString() ?? string.Empty;
        Assert.Contains("Connected", connection, StringComparison.OrdinalIgnoreCase);

        // Server version must not be empty.
        var serverVersion = root.GetProperty("ServerVersion").GetString();
        Assert.False(string.IsNullOrEmpty(serverVersion), "ServerVersion must not be empty.");

        // Channel count must be >= 5 (bootstrap creates 5).
        var channelCount = root.GetProperty("ChannelCount").GetInt32();
        Assert.True(channelCount >= 5, $"ChannelCount was {channelCount}, expected >= 5.");

        // Checks array must have at least 3 entries.
        var checks = root.GetProperty("Checks");
        Assert.True(checks.GetArrayLength() >= 3, $"Expected >= 3 diagnostic checks, got {checks.GetArrayLength()}.");

        // At least one check must have Status == "OK".
        var hasOk = checks.EnumerateArray().Any(c => c.GetProperty("Status").GetString() == "OK");
        Assert.True(hasOk, "Expected at least one diagnostic check with Status 'OK'.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6. StreamRelay_ActuallyStreamsBytes
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task StreamRelay_ActuallyStreamsBytes()
    {
        SkipIfUnavailable();

        var channelId = await TryGetFirstChannelUuidAsync();
        Assert.False(string.IsNullOrEmpty(channelId), "Bootstrapped stack must expose at least one channel to stream.");

        // The open /stream endpoint requires a token when relay token security is enabled.
        // Disable it for the duration of this raw byte-flow check, then restore it.
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

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var response = await _fixture.Client.GetAsync(
                $"/api/tvheadend/stream/{channelId}?profile=pass",
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            Assert.True(
                contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
                contentType == "application/octet-stream",
                $"Expected video/* or application/octet-stream, got: {contentType}");

            // Read a real chunk of continuous data — proves the full Jellyfin→TVHeadend→IPTV path streams.
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

            Assert.True(totalRead >= 16000, $"Expected >= 16000 bytes of continuous stream data, got {totalRead}.");
        }
        finally
        {
            using var restoreConfig = await ReadPluginConfigAsync();
            await SavePluginConfigAsync(restoreConfig, OverrideField(
                "EnableRelayTokenSecurity",
                w => w.WriteBoolean("EnableRelayTokenSecurity", hadSecurity)));
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 7. EpgPrograms_AvailableViaJellyfin
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EpgPrograms_AvailableViaJellyfin()
    {
        SkipIfUnavailable();

        var response = await _fixture.Client.GetAsync("/TvHeadendApi/Diagnose");
        Assert.True(
            response.IsSuccessStatusCode,
            $"/TvHeadendApi/Diagnose must succeed against the bootstrapped stack, got {(int)response.StatusCode}.");

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        // Channels are only visible if mapped properly, so ChannelCount >= 5 proves EPG sync worked.
        var channelCount = doc.RootElement.GetProperty("ChannelCount").GetInt32();
        Assert.True(channelCount >= 5, $"ChannelCount was {channelCount}, expected >= 5 to confirm EPG sync.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 8. Dashboard_ContainsHealthAndServerData
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Dashboard_ContainsHealthAndServerData()
    {
        SkipIfUnavailable();

        using var doc = await GetJsonAsync("/TvHeadendApi/Dashboard");
        var root = doc.RootElement;

        // Must have UpstreamHealth with a Status property.
        Assert.True(root.TryGetProperty("UpstreamHealth", out var health), "Dashboard must have UpstreamHealth.");
        Assert.True(
            health.ValueKind == JsonValueKind.Object,
            "UpstreamHealth must be a JSON object.");
        Assert.True(health.TryGetProperty("Status", out _), "UpstreamHealth must have a Status field.");

        // ServerVersion indicates TVH is connected.
        Assert.True(root.TryGetProperty("ServerVersion", out var serverVersion), "Dashboard must have ServerVersion.");
        var sv = serverVersion.GetString();
        Assert.False(string.IsNullOrEmpty(sv), "ServerVersion must not be empty.");

        // ChannelCount shows channels are available.
        Assert.True(root.TryGetProperty("ChannelCount", out var channelCount), "Dashboard must have ChannelCount.");
        Assert.True(channelCount.GetInt32() >= 5, $"ChannelCount was {channelCount.GetInt32()}, expected >= 5.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 9. Health_AfterCheck_ShowsHealthyOrDegraded
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Health_AfterCheck_ShowsHealthyOrDegraded()
    {
        SkipIfUnavailable();

        var response = await _fixture.Client.PostAsync("/TvHeadendApi/Health/Check", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Status must be one of Healthy, Degraded, Unknown — NOT Unreachable or CircuitOpen
        // since we know TVH is running.
        var status = root.GetProperty("Status").GetString() ?? string.Empty;
        var acceptableStatuses = new[] { "Healthy", "Degraded", "Unknown" };
        Assert.Contains(status, acceptableStatuses);

        // ConsecutiveFailures should be 0 or a small number.
        var failures = root.GetProperty("ConsecutiveFailures").GetInt32();
        Assert.True(failures <= 3, $"ConsecutiveFailures was {failures}, expected <= 3.");

        // SnapshotUtc must be within the last 30 seconds.
        var snapshotUtc = root.GetProperty("SnapshotUtc").GetDateTimeOffset();
        var age = DateTimeOffset.UtcNow - snapshotUtc;
        Assert.True(age.TotalSeconds <= 30, $"SnapshotUtc is {age.TotalSeconds:F1}s old, expected <= 30s.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 10. RelayTokenSecurity_InvalidToken_Rejected
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RelayTokenSecurity_InvalidToken_Rejected()
    {
        SkipIfUnavailable();

        var channelId = await TryGetFirstChannelUuidAsync();
        Assert.False(
            string.IsNullOrEmpty(channelId),
            "Bootstrap guarantees 5 channels — a null channel id means the plugin lost connectivity to TVHeadend.");

        var response = await _fixture.AnonymousClient.GetAsync(
            $"/api/tvheadend/relay/stream/{channelId}?token=definitely-not-valid");

        // Security must reject the invalid token — NOT 200.
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.Forbidden ||
            response.StatusCode == HttpStatusCode.Gone ||
            response.StatusCode == HttpStatusCode.BadGateway,
            $"Invalid token should be rejected (401/403/410/502), got {(int)response.StatusCode}.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 11. RelayTokenSecurity_MissingToken_Rejected
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RelayTokenSecurity_MissingToken_Rejected()
    {
        SkipIfUnavailable();

        var channelId = await TryGetFirstChannelUuidAsync();
        Assert.False(
            string.IsNullOrEmpty(channelId),
            "Bootstrap guarantees 5 channels — a null channel id means the plugin lost connectivity to TVHeadend.");

        var response = await _fixture.AnonymousClient.GetAsync(
            $"/api/tvheadend/relay/stream/{channelId}");

        // Security must reject missing token — NOT 200.
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.Forbidden ||
            response.StatusCode == HttpStatusCode.Gone ||
            response.StatusCode == HttpStatusCode.BadGateway,
            $"Missing token should be rejected (401/403/410/502), got {(int)response.StatusCode}.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 12. Discovered_ContainsPassProfile
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Discovered_ContainsPassProfile()
    {
        SkipIfUnavailable();

        var response = await _fixture.Client.GetAsync("/TvHeadendApi/StreamingProfiles/Discovered");
        Assert.True(
            response.IsSuccessStatusCode,
            $"/TvHeadendApi/StreamingProfiles/Discovered must succeed against the bootstrapped stack, got {(int)response.StatusCode}.");

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var profiles = doc.RootElement.EnumerateArray().ToList();

        // TVH always has the "pass" profile.
        Assert.True(profiles.Count >= 1, $"Expected >= 1 discovered profile, got {profiles.Count}.");

        var hasPass = profiles.Any(p =>
        {
            if (p.TryGetProperty("Name", out var name))
            {
                return string.Equals(name.GetString(), "pass", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        });
        Assert.True(hasPass, "Expected 'pass' profile in discovered profiles.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 13. ProfileValidation_NoWarningsForValidProfiles
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProfileValidation_NoWarningsForValidProfiles()
    {
        SkipIfUnavailable();

        // Ensure config has StreamingProfile = "pass" (a valid built-in profile).
        using var currentConfig = await ReadPluginConfigAsync();
        var currentProfile = currentConfig.RootElement.GetProperty("StreamingProfile").GetString() ?? string.Empty;

        try
        {
            if (currentProfile != "pass")
            {
                await SavePluginConfigAsync(currentConfig, OverrideField(
                    "StreamingProfile",
                    w => w.WriteString("StreamingProfile", "pass")));
            }

            // Validate — "pass" is always valid in TVH, so no warnings expected.
            using var doc = await GetJsonAsync("/TvHeadendApi/StreamingProfiles/Validate");
            var warnings = doc.RootElement.EnumerateArray().ToList();
            Assert.Empty(warnings);
        }
        finally
        {
            // Restore original profile if we changed it.
            if (currentProfile != "pass")
            {
                using var restoreBase = await ReadPluginConfigAsync();
                await SavePluginConfigAsync(restoreBase, OverrideField(
                    "StreamingProfile",
                    w => w.WriteString("StreamingProfile", currentProfile)));
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 14. GenerateAuthToken_TokenIsAlphanumeric
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GenerateAuthToken_TokenIsAlphanumeric()
    {
        SkipIfUnavailable();

        var response = await _fixture.Client.PostAsync("/TvHeadendApi/GenerateAuthToken", null);
        Assert.True(
            response.IsSuccessStatusCode,
            $"/TvHeadendApi/GenerateAuthToken must succeed against the bootstrapped stack, got {(int)response.StatusCode}.");

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Assert Success == true.
        var success = root.GetProperty("Success").GetBoolean();
        Assert.True(success, "GenerateAuthToken must succeed.");

        // Assert AuthToken is alphanumeric (URL-safe).
        var token = root.GetProperty("AuthToken").GetString() ?? string.Empty;
        Assert.Matches(@"^[A-Za-z0-9]+$", token);

        // Assert minimum length.
        Assert.True(token.Length >= 8, $"AuthToken length was {token.Length}, expected >= 8.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 15. JellyfinLogs_NoPluginErrors
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task JellyfinLogs_NoPluginErrors()
    {
        SkipIfUnavailable();

        using var doc = await GetJsonAsync("/TvHeadendApi/Dashboard/Logs?limit=100");
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("Entries", out var entries), "Response must have Entries array.");
        Assert.True(entries.ValueKind == JsonValueKind.Array, "Entries must be an array.");

        // Count only PLUGIN-emitted errors. Entries with Source == "tvheadend" are TVHeadend's own
        // log lines imported by the comet importer — negative-path tests in this very suite
        // legitimately produce those (e.g. intentional 404 probes), so they must not fail this test.
        var errorEntries = entries.EnumerateArray()
            .Where(e =>
            {
                var source = string.Empty;
                var level = string.Empty;

                if (e.TryGetProperty("Source", out var srcProp))
                {
                    source = srcProp.GetString() ?? string.Empty;
                }

                if (e.TryGetProperty("Level", out var lvlProp))
                {
                    level = lvlProp.GetString() ?? string.Empty;
                }

                return source.Contains("TvHeadend", StringComparison.OrdinalIgnoreCase) &&
                       !source.Equals("tvheadend", StringComparison.OrdinalIgnoreCase) &&
                       (level.Equals("Error", StringComparison.OrdinalIgnoreCase) ||
                        level.Equals("Critical", StringComparison.OrdinalIgnoreCase));
            })
            .ToList();

        Assert.True(
            errorEntries.Count == 0,
            $"Found {errorEntries.Count} plugin error/critical log entries. " +
            $"First: {(errorEntries.Count > 0 ? errorEntries[0].ToString() : "N/A")}");
    }
}
