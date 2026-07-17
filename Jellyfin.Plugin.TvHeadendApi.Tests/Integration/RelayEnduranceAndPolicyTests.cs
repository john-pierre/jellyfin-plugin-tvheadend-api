using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Integration;

/// <summary>
/// End-to-end coverage for the scenarios the rest of the suite does not exercise:
/// sustained (soak) streaming, relay-token expiry during an active stream,
/// hierarchical streaming-profile rule resolution, and the full DVR
/// record-and-list cycle. Requires the full Docker test stack.
/// </summary>
[Trait("Category", "LiveIntegration")]
[Collection("JellyfinApi")]
public sealed class RelayEnduranceAndPolicyTests
{
    private static readonly string PluginId = "ae9f5148-d656-43ab-ab83-94192f9e840a";

    private readonly JellyfinApiFixture _fixture;

    public RelayEnduranceAndPolicyTests(JellyfinApiFixture fixture)
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
            Assert.Fail($"Jellyfin API fixture is not available ({_fixture.SetupError ?? "unknown reason"}).");
        }
    }

    private async Task<string> GetFirstChannelUuidAsync()
    {
        var response = await _fixture.Client.GetAsync("/TvHeadendApi/StreamingProfiles/Channels");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Channels endpoint failed: {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var first = doc.RootElement.EnumerateArray().FirstOrDefault();
        Assert.True(first.ValueKind == JsonValueKind.Object, "Bootstrapped stack must expose at least one channel.");
        var id = first.GetProperty("Id").GetString();
        Assert.False(string.IsNullOrEmpty(id), "Channel Id must not be empty.");
        return id!;
    }

    private async Task<JsonNode> ReadConfigAsync()
    {
        var resp = await _fixture.Client.GetAsync($"/Plugins/{PluginId}/Configuration");
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        return JsonNode.Parse(json) ?? throw new InvalidOperationException("Plugin configuration was empty.");
    }

    private async Task SaveConfigAsync(JsonNode config)
    {
        var resp = await _fixture.Client.PostAsync(
            $"/Plugins/{PluginId}/Configuration",
            new StringContent(config.ToJsonString(), Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Creates an HTTP client that talks directly to TVHeadend from the test host.
    /// TVHeadend challenges with Digest auth, which .NET's SocketsHttpHandler does not
    /// implement — reuse the plugin's own <see cref="Jellyfin.Plugin.TvHeadendApi.Service.Auth.DigestAuthHandler"/>.
    /// </summary>
    private static HttpClient CreateTvhClient()
    {
        var tvhUrl = Environment.GetEnvironmentVariable("TVHEADEND_URL") ?? "http://localhost:19981";
        var user = Environment.GetEnvironmentVariable("TVH_USER") ?? "testuser";
        var pass = Environment.GetEnvironmentVariable("TVH_PASS") ?? "testpass";
        var handler = new Jellyfin.Plugin.TvHeadendApi.Service.Auth.DigestAuthHandler(user, pass)
        {
            InnerHandler = new HttpClientHandler(),
        };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri(tvhUrl),
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    /// <summary>
    /// Reads from the stream until <paramref name="duration"/> elapses, failing when any
    /// single read stalls longer than 15 seconds. Returns the total bytes received.
    /// </summary>
    private static async Task<long> ReadContinuouslyAsync(System.IO.Stream stream, TimeSpan duration)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        var deadline = DateTime.UtcNow + duration;

        while (DateTime.UtcNow < deadline)
        {
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, readCts.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"Stream stalled: no data for 15s after {total} bytes.");
                return total;
            }

            Assert.True(read > 0, $"Stream ended prematurely after {total} bytes.");
            total += read;
        }

        return total;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 1. Sustained streaming (soak)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RelayStream_SustainedSoak_DeliversContinuousDataWithoutStalls()
    {
        SkipIfUnavailable();

        // Start from a healthy plugin/TVHeadend connection — earlier tests in the shared
        // collection may have tripped the circuit breaker or mutated the configuration.
        Assert.True(
            await _fixture.EnsureConfiguredAndHealthyAsync(),
            "Plugin/TVHeadend connection could not be made healthy — check the docker stack and TVH_HOST/TVH_PORT env propagation (WSLENV with /w on WSL).");

        // Default 60s keeps regular runs fast; the e2e runners can extend via env
        // (the 5-minute figure was proven manually — E2E_SOAK_SECONDS=300 codifies it).
        var soakSecondsStr = Environment.GetEnvironmentVariable("E2E_SOAK_SECONDS");
        var soakSeconds = int.TryParse(soakSecondsStr, out var parsed) && parsed > 0 ? parsed : 60;

        var channelId = await GetFirstChannelUuidAsync();

        // The open /stream endpoint requires a token when relay token security is on;
        // temporarily disable it for a raw byte-flow soak, then restore.
        var config = await ReadConfigAsync();
        var hadSecurity = config["EnableRelayTokenSecurity"]?.GetValue<bool>() ?? false;

        try
        {
            if (hadSecurity)
            {
                config["EnableRelayTokenSecurity"] = false;
                await SaveConfigAsync(config);
            }

            using var streamClient = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl), Timeout = Timeout.InfiniteTimeSpan };
            streamClient.DefaultRequestHeaders.Add("X-Emby-Token", _fixture.AccessToken);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(soakSeconds + 60));
            using var response = await streamClient.GetAsync(
                $"/api/tvheadend/stream/{channelId}?profile=pass",
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var total = await ReadContinuouslyAsync(stream, TimeSpan.FromSeconds(soakSeconds));

            // The IPTV simulator delivers ~900 kbit/s (~110 KB/s); 30 KB/s average is a
            // conservative floor that still catches broken/stuttering delivery.
            var floor = soakSeconds * 30_000L;
            Assert.True(total >= floor, $"Soak delivered only {total} bytes in {soakSeconds}s (floor {floor}).");
        }
        finally
        {
            var restore = await ReadConfigAsync();
            restore["EnableRelayTokenSecurity"] = hadSecurity;
            await SaveConfigAsync(restore);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 2. Token expiry mid-stream
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RelayStream_TokenExpiresMidStream_StreamContinues_NewRequestRejected()
    {
        SkipIfUnavailable();

        // Start from a healthy plugin/TVHeadend connection — earlier tests in the shared
        // collection may have tripped the circuit breaker or mutated the configuration.
        Assert.True(
            await _fixture.EnsureConfiguredAndHealthyAsync(),
            "Plugin/TVHeadend connection could not be made healthy — check the docker stack and TVH_HOST/TVH_PORT env propagation (WSLENV with /w on WSL).");

        var channelId = await GetFirstChannelUuidAsync();

        // Shorten the stream-token TTL so expiry happens inside the test window; restore afterwards.
        var config = await ReadConfigAsync();
        var originalTtl = config["StreamTokenTtlSeconds"]?.GetValue<int>() ?? 120;

        try
        {
            config["StreamTokenTtlSeconds"] = 20;
            config["EnableRelayTokenSecurity"] = true;
            config["RelayEnabled"] = true;
            await SaveConfigAsync(config);

            // Obtain a tokenized relay URL the way a client would: via Jellyfin PlaybackInfo.
            var usersResp = await _fixture.Client.GetAsync("/Users/Me");
            usersResp.EnsureSuccessStatusCode();
            using var meDoc = JsonDocument.Parse(await usersResp.Content.ReadAsStringAsync());
            var userId = meDoc.RootElement.GetProperty("Id").GetString();
            Assert.False(string.IsNullOrEmpty(userId), "Admin user id must be available.");

            // Jellyfin's Live TV channel cache may lag behind plugin recovery — refresh if empty.
            Assert.True(
                await _fixture.EnsureLiveTvChannelsAsync(),
                "Jellyfin must expose the bootstrapped Live TV channels (guide refresh did not surface any).");

            var channelsResp = await _fixture.Client.GetAsync($"/LiveTv/Channels?userId={userId}");
            var channelsBody = await channelsResp.Content.ReadAsStringAsync();
            Assert.True(channelsResp.IsSuccessStatusCode, $"LiveTv/Channels failed: {(int)channelsResp.StatusCode}: {channelsBody}");
            using var channelsDoc = JsonDocument.Parse(channelsBody);
            var items = channelsDoc.RootElement.GetProperty("Items");
            Assert.True(items.GetArrayLength() > 0, "Jellyfin must expose the bootstrapped Live TV channels.");
            var itemId = items[0].GetProperty("Id").GetString();

            var playbackResp = await _fixture.Client.PostAsync(
                $"/Items/{itemId}/PlaybackInfo?userId={userId}",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            var playbackBody = await playbackResp.Content.ReadAsStringAsync();
            Assert.True(playbackResp.IsSuccessStatusCode, $"PlaybackInfo failed: {(int)playbackResp.StatusCode}: {playbackBody}");

            using var playbackDoc = JsonDocument.Parse(playbackBody);
            var mediaSources = playbackDoc.RootElement.GetProperty("MediaSources");
            Assert.True(mediaSources.GetArrayLength() > 0, "PlaybackInfo must return at least one media source.");
            var path = mediaSources[0].GetProperty("Path").GetString();
            Assert.False(string.IsNullOrEmpty(path), "Media source path must be present.");
            Assert.Contains("token=", path!, StringComparison.Ordinal);

            // The path may carry the docker-internal host — request it against the test-host base URL.
            var pathAndQuery = new Uri(path!, UriKind.RelativeOrAbsolute).IsAbsoluteUri
                ? new Uri(path!).PathAndQuery
                : path!;

            using var streamClient = new HttpClient { BaseAddress = new Uri(_fixture.BaseUrl), Timeout = Timeout.InfiniteTimeSpan };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using var response = await streamClient.GetAsync(pathAndQuery, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"Tokenized stream request failed: {(int)response.StatusCode}.");

            // Stream well past the 20s TTL — validation happens at request start, so an
            // already-running stream must NOT be cut off by token expiry.
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var total = await ReadContinuouslyAsync(stream, TimeSpan.FromSeconds(35));
            Assert.True(total > 0, "Expected continuous stream data past token expiry.");

            // A NEW request with the now-expired token must be rejected.
            // The relay answers 401 (missing/invalid), 403 (policy), or 410 Gone (expired).
            using var rejected = await streamClient.GetAsync(pathAndQuery, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
            Assert.True(
                rejected.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Gone,
                $"Expired token must be rejected; got {(int)rejected.StatusCode}.");
        }
        finally
        {
            var restore = await ReadConfigAsync();
            restore["StreamTokenTtlSeconds"] = originalTtl;
            await SaveConfigAsync(restore);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 3. Streaming-profile rule resolution
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task StreamingProfileRules_ClientRule_ResolvesToRuleProfile()
    {
        SkipIfUnavailable();

        // Start from a healthy plugin/TVHeadend connection — earlier tests in the shared
        // collection may have tripped the circuit breaker or mutated the configuration.
        Assert.True(
            await _fixture.EnsureConfiguredAndHealthyAsync(),
            "Plugin/TVHeadend connection could not be made healthy — check the docker stack and TVH_HOST/TVH_PORT env propagation (WSLENV with /w on WSL).");

        var config = await ReadConfigAsync();
        var settings = config["StreamingProfileSettings"]?.AsObject()
            ?? throw new InvalidOperationException("StreamingProfileSettings missing from plugin configuration.");
        var originalRules = settings["ClientRules"]?.ToJsonString() ?? "[]";

        try
        {
            var rules = JsonNode.Parse(originalRules)!.AsArray();
            rules.Add(new JsonObject
            {
                ["Enabled"] = true,
                ["RuleName"] = "e2e-client-rule",
                ["Description"] = "Added by RelayEnduranceAndPolicyTests",
                ["Priority"] = 1,
                ["MatchType"] = 0, // ClientNameExact
                ["MatchValue"] = "E2EProbe",
                ["PlaybackMode"] = 0, // Auto
                ["TvHeadendProfileName"] = "test-pass",
            });
            settings["ClientRules"] = rules;
            await SaveConfigAsync(config);

            var resolveResp = await _fixture.Client.GetAsync("/TvHeadendApi/StreamingProfiles/Resolve?clientName=E2EProbe");
            var body = await resolveResp.Content.ReadAsStringAsync();
            Assert.True(resolveResp.IsSuccessStatusCode, $"Resolve failed: {(int)resolveResp.StatusCode}: {body}");

            // MatchedRuleName is omitted from the JSON when no rule matched, so read defensively
            // and surface the full resolution result (incl. Reasons) on failure.
            using var doc = JsonDocument.Parse(body);
            var effective = doc.RootElement.GetProperty("EffectiveTvHeadendProfile").GetString();
            var matchedRule = doc.RootElement.TryGetProperty("MatchedRuleName", out var ruleProp) ? ruleProp.GetString() : null;
            Assert.True(
                effective == "test-pass" && matchedRule == "e2e-client-rule",
                $"Client rule did not resolve as expected. Full result: {body}");

            // Negative control: an unrelated client must NOT hit the rule.
            var otherResp = await _fixture.Client.GetAsync("/TvHeadendApi/StreamingProfiles/Resolve?clientName=SomeOtherClient");
            var otherBody = await otherResp.Content.ReadAsStringAsync();
            Assert.True(otherResp.IsSuccessStatusCode, $"Resolve (control) failed: {(int)otherResp.StatusCode}: {otherBody}");
            using var otherDoc = JsonDocument.Parse(otherBody);
            var otherRule = otherDoc.RootElement.TryGetProperty("MatchedRuleName", out var otherProp) ? otherProp.GetString() : null;
            Assert.NotEqual("e2e-client-rule", otherRule);
        }
        finally
        {
            var restore = await ReadConfigAsync();
            var restoreSettings = restore["StreamingProfileSettings"]?.AsObject();
            if (restoreSettings is not null)
            {
                restoreSettings["ClientRules"] = JsonNode.Parse(originalRules);
                await SaveConfigAsync(restore);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 4. DVR: bootstrap timer visible + full record-and-list cycle
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Dvr_BootstrapScheduledRecording_IsVisibleThroughJellyfin()
    {
        SkipIfUnavailable();

        // Start from a healthy plugin/TVHeadend connection — earlier tests in the shared
        // collection may have tripped the circuit breaker or mutated the configuration.
        Assert.True(
            await _fixture.EnsureConfiguredAndHealthyAsync(),
            "Plugin/TVHeadend connection could not be made healthy — check the docker stack and TVH_HOST/TVH_PORT env propagation (WSLENV with /w on WSL).");

        // The bootstrap schedules "E2E Scheduled Recording" (fails closed if it cannot).
        // Depending on suite timing it is an upcoming timer, actively recording, or completed —
        // it must be visible through the plugin in one of those states.
        var timersResp = await _fixture.Client.GetAsync("/LiveTv/Timers");
        var timersBody = await timersResp.Content.ReadAsStringAsync();
        Assert.True(timersResp.IsSuccessStatusCode, $"LiveTv/Timers failed: {(int)timersResp.StatusCode}: {timersBody}");

        var recordingsResp = await _fixture.Client.GetAsync("/LiveTv/Recordings");
        var recordingsBody = await recordingsResp.Content.ReadAsStringAsync();
        Assert.True(recordingsResp.IsSuccessStatusCode, $"LiveTv/Recordings failed: {(int)recordingsResp.StatusCode}: {recordingsBody}");

        var visible = timersBody.Contains("E2E Scheduled Recording", StringComparison.Ordinal)
                      || recordingsBody.Contains("E2E Scheduled Recording", StringComparison.Ordinal);
        Assert.True(visible, "The bootstrap-scheduled recording must be visible via LiveTv/Timers or LiveTv/Recordings.");
    }

    [Fact]
    public async Task Dvr_RecordAndList_CompletedRecordingAppears()
    {
        SkipIfUnavailable();

        // Start from a healthy plugin/TVHeadend connection — earlier tests in the shared
        // collection may have tripped the circuit breaker or mutated the configuration.
        Assert.True(
            await _fixture.EnsureConfiguredAndHealthyAsync(),
            "Plugin/TVHeadend connection could not be made healthy — check the docker stack and TVH_HOST/TVH_PORT env propagation (WSLENV with /w on WSL).");

        var channelId = await GetFirstChannelUuidAsync();
        var title = $"E2E Cycle Recording {Guid.NewGuid():N}";

        using var tvh = CreateTvhClient();

        // Schedule a short immediate recording directly on TVHeadend.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var conf = JsonSerializer.Serialize(new
        {
            enabled = true,
            start = now + 5,
            stop = now + 50,
            channel = channelId,
            title = new { eng = title },
            comment = "record-and-list e2e cycle",
        });
        var createResp = await tvh.PostAsync(
            "/api/dvr/entry/create",
            new FormUrlEncodedContent(new[] { new System.Collections.Generic.KeyValuePair<string, string>("conf", conf) }));
        var createBody = await createResp.Content.ReadAsStringAsync();
        Assert.True(createResp.IsSuccessStatusCode, $"dvr/entry/create failed: {(int)createResp.StatusCode}: {createBody}");
        Assert.Contains("uuid", createBody, StringComparison.OrdinalIgnoreCase);

        // Wait for the recording to finish (start lead + 45s recording + finish margin).
        // Jellyfin serves /LiveTv/Recordings from library-scanned folders only, so the surface a
        // third-party provider gets is the timer list: the completed recording must appear in
        // /LiveTv/Timers with Status "Completed". Additionally verify on the TVHeadend side that
        // the recording physically finished (grid_finished with our title).
        var jellyfinSeesCompleted = false;
        var tvhFinished = false;
        string lastBody = string.Empty;
        for (var attempt = 0; attempt < 30 && !(jellyfinSeesCompleted && tvhFinished); attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));

            if (!tvhFinished)
            {
                var finishedResp = await tvh.GetAsync("/api/dvr/entry/grid_finished?limit=50");
                if (finishedResp.IsSuccessStatusCode)
                {
                    var finishedBody = await finishedResp.Content.ReadAsStringAsync();
                    tvhFinished = finishedBody.Contains(title, StringComparison.Ordinal);
                }
            }

            var resp = await _fixture.Client.GetAsync("/LiveTv/Timers");
            if (!resp.IsSuccessStatusCode)
            {
                continue;
            }

            lastBody = await resp.Content.ReadAsStringAsync();
            using var timersDoc = JsonDocument.Parse(lastBody);
            foreach (var item in timersDoc.RootElement.GetProperty("Items").EnumerateArray())
            {
                var name = item.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() : null;
                var status = item.TryGetProperty("Status", out var statusProp) ? statusProp.GetString() : null;
                if (name == title && string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase))
                {
                    jellyfinSeesCompleted = true;
                    break;
                }
            }
        }

        Assert.True(tvhFinished, $"TVHeadend never listed '{title}' in grid_finished — the recording itself failed.");
        Assert.True(
            jellyfinSeesCompleted,
            $"Completed recording '{title}' never appeared as a Completed timer in LiveTv/Timers. Last response: {Truncate(lastBody)}");
    }

    private static string Truncate(string value)
    {
        return value.Length <= 500 ? value : value[..500] + "…";
    }
}
