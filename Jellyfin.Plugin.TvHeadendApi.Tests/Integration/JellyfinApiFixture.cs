using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Integration;

/// <summary>
/// xUnit fixture that performs Jellyfin first-run setup, authenticates an admin user,
/// configures the TvHeadendApi plugin, and provides pre-configured HTTP clients
/// for end-to-end API testing against the running Jellyfin server.
/// </summary>
public sealed class JellyfinApiFixture : IAsyncLifetime
{
    private static readonly string PluginId = "ae9f5148-d656-43ab-ab83-94192f9e840a";

    private HttpClient? _authClient;
    private HttpClient? _anonClient;
    private HttpClient? _longClient;

    /// <summary>
    /// Gets the authenticated HTTP client with <c>X-Emby-Token</c> header set.
    /// </summary>
    public HttpClient Client => _authClient ?? throw new InvalidOperationException("Fixture not initialized.");

    /// <summary>
    /// Gets an anonymous HTTP client with no authentication headers.
    /// </summary>
    public HttpClient AnonymousClient => _anonClient ?? throw new InvalidOperationException("Fixture not initialized.");

    /// <summary>
    /// Gets an authenticated HTTP client with a 10-minute timeout for endpoints whose
    /// duration scales with the lineup size (e.g. cache warmup probing 100 channels).
    /// </summary>
    public HttpClient LongRunningClient => _longClient ?? throw new InvalidOperationException("Fixture not initialized.");

    /// <summary>
    /// Gets the Jellyfin base URL.
    /// </summary>
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the Jellyfin access token obtained during authentication.
    /// </summary>
    public string AccessToken { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the plugin relay auth token generated during setup.
    /// </summary>
    public string RelayAuthToken { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the error message from fixture setup failure, if any.
    /// </summary>
    public string? SetupError { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the fixture setup completed successfully.
    /// When <c>false</c>, tests should skip gracefully.
    /// </summary>
    public bool IsAvailable { get; private set; }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        BaseUrl = Environment.GetEnvironmentVariable("JELLYFIN_URL") ?? "http://localhost:18096";
        var tvhHost = Environment.GetEnvironmentVariable("TVH_HOST") ?? "localhost";
        var tvhPortStr = Environment.GetEnvironmentVariable("TVH_PORT") ?? "19981";
        var tvhUser = Environment.GetEnvironmentVariable("TVH_USER") ?? "testuser";
        var tvhPass = Environment.GetEnvironmentVariable("TVH_PASS") ?? "testpass";

        if (!int.TryParse(tvhPortStr, out var tvhPort))
        {
            tvhPort = 19981;
        }

        try
        {
            // Create a temporary client for setup (no auth yet).
            using var setupClient = new HttpClient
            {
                BaseAddress = new Uri(BaseUrl),
                Timeout = TimeSpan.FromSeconds(30),
            };

            // ── Step 1: Complete Jellyfin first-run setup ──────────────────────
            await CompleteFirstRunSetup(setupClient).ConfigureAwait(false);

            // ── Step 2: Authenticate ───────────────────────────────────────────
            AccessToken = await AuthenticateAsync(setupClient).ConfigureAwait(false);

            // ── Step 3: Build authenticated + anonymous clients ────────────────
            _authClient = new HttpClient
            {
                BaseAddress = new Uri(BaseUrl),
                Timeout = TimeSpan.FromSeconds(30),
            };
            _authClient.DefaultRequestHeaders.Add("X-Emby-Token", AccessToken);

            _anonClient = new HttpClient
            {
                BaseAddress = new Uri(BaseUrl),
                Timeout = TimeSpan.FromSeconds(30),
            };

            _longClient = new HttpClient
            {
                BaseAddress = new Uri(BaseUrl),
                Timeout = TimeSpan.FromMinutes(10),
            };
            _longClient.DefaultRequestHeaders.Add("X-Emby-Token", AccessToken);

            // ── Step 4: Configure the plugin ───────────────────────────────────
            await ConfigurePluginAsync(_authClient, tvhHost, tvhPort, tvhUser, tvhPass).ConfigureAwait(false);

            // ── Step 5: Generate relay auth token ──────────────────────────────
            RelayAuthToken = await GenerateAuthTokenAsync(_authClient).ConfigureAwait(false);

            IsAvailable = true;

            // ── Step 6: Wait for the plugin to actually reach TVHeadend ────────
            // A freshly booted Jellyfin starts the plugin with default settings; its hosted
            // services fail against the wrong host and open the circuit breaker BEFORE this
            // fixture applies the correct configuration. Without this wait, the first test
            // classes run inside the ~30s open window and fail with bogus connectivity errors.
            await EnsureConfiguredAndHealthyAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Setup failed — tests will be skipped via IsAvailable == false.
            SetupError = $"{ex.GetType().Name}: {ex.Message}";
            IsAvailable = false;
        }
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _authClient?.Dispose();
        _anonClient?.Dispose();
        _longClient?.Dispose();
        return Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Setup helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static async Task CompleteFirstRunSetup(HttpClient client)
    {
        try
        {
            // Check if the startup wizard has already been completed.
            var publicInfoResp = await client.GetAsync("/System/Info/Public").ConfigureAwait(false);
            if (publicInfoResp.IsSuccessStatusCode)
            {
                var publicInfoJson = await publicInfoResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var publicInfoDoc = JsonDocument.Parse(publicInfoJson);
                if (publicInfoDoc.RootElement.TryGetProperty("StartupWizardCompleted", out var wizardProp)
                    && wizardProp.GetBoolean())
                {
                    // Wizard already completed (likely by the Docker start script) — skip setup.
                    return;
                }
            }

            // POST /Startup/Configuration
            var configBody = JsonSerializer.Serialize(new
            {
                UICulture = "en-US",
                MetadataCountryCode = "US",
                PreferredMetadataLanguage = "en",
            });
            var configResp = await client.PostAsync(
                "/Startup/Configuration",
                new StringContent(configBody, Encoding.UTF8, "application/json")).ConfigureAwait(false);

            // If first-run is already complete, Jellyfin returns 403/404 — that's fine.
            if (!configResp.IsSuccessStatusCode)
            {
                return;
            }

            // POST /Startup/User
            var userBody = JsonSerializer.Serialize(new
            {
                Name = "admin",
                Password = "admin123",
            });
            await client.PostAsync(
                "/Startup/User",
                new StringContent(userBody, Encoding.UTF8, "application/json")).ConfigureAwait(false);

            // POST /Startup/Complete
            await client.PostAsync("/Startup/Complete", null).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // Jellyfin might not be in first-run state — proceed to authentication.
        }
    }

    private static async Task<string> AuthenticateAsync(HttpClient client)
    {
        var authBody = JsonSerializer.Serialize(new
        {
            Username = "admin",
            Pw = "admin123",
        });

        // Retry authentication — Jellyfin may still be initializing after the startup
        // wizard completes (e.g. user database not yet fully committed).
        const int maxAttempts = 10;
        const int delayMs = 2000;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName")
            {
                Content = new StringContent(authBody, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add(
                "X-Emby-Authorization",
                "MediaBrowser Client=\"TestRunner\", Device=\"E2E\", DeviceId=\"e2e-test\", Version=\"1.0\"");

            var response = await client.SendAsync(request).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("AccessToken").GetString()
                       ?? throw new InvalidOperationException("AccessToken was null in auth response.");
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"Authentication failed after {maxAttempts} attempts. Jellyfin may not have a valid admin user.");
    }

    /// <summary>
    /// Re-applies the working TVHeadend connection configuration and waits for the plugin's
    /// resilience circuit breaker to recover (connection healthy again). Destructive tests that
    /// reset configuration or intentionally break connectivity must call this in their <c>finally</c>
    /// so they leave the shared fixture in a known-good state for subsequent tests in the collection.
    /// </summary>
    /// <returns><c>true</c> when the plugin reports a healthy TVHeadend connection (ChannelCount &gt; 0);
    /// <c>false</c> when it never became healthy within the timeout. Tests that REQUIRE a healthy
    /// backend should assert on this; finally-block callers may ignore it.</returns>
    public async Task<bool> EnsureConfiguredAndHealthyAsync()
    {
        if (!IsAvailable || _authClient == null)
        {
            return false;
        }

        // NOTE: on WSL with the Windows dotnet SDK these env vars only arrive when listed in
        // WSLENV with the /w flag — otherwise the localhost fallbacks silently misconfigure
        // the in-container plugin (ConnectionRefused for everything).
        var tvhHost = Environment.GetEnvironmentVariable("TVH_HOST") ?? "localhost";
        var tvhPortStr = Environment.GetEnvironmentVariable("TVH_PORT") ?? "19981";
        var tvhUser = Environment.GetEnvironmentVariable("TVH_USER") ?? "testuser";
        var tvhPass = Environment.GetEnvironmentVariable("TVH_PASS") ?? "testpass";
        if (!int.TryParse(tvhPortStr, out var tvhPort))
        {
            tvhPort = 19981;
        }

        await ConfigurePluginAsync(_authClient, tvhHost, tvhPort, tvhUser, tvhPass).ConfigureAwait(false);

        // The circuit breaker stays open ~30s after a bad-config/reset test, so re-applying the
        // correct config is not enough — poll until the plugin can actually reach TVHeadend again.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                var resp = await _authClient.GetAsync("/TvHeadendApi/Diagnose").ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("ChannelCount", out var cc) && cc.GetInt32() > 0)
                    {
                        return true;
                    }
                }
            }
            catch (HttpRequestException)
            {
                // Retry until healthy or timeout.
            }

            await Task.Delay(2000).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Ensures Jellyfin's own Live TV channel cache is populated. When the plugin was
    /// unreachable for a while (e.g. after a misconfiguration), Jellyfin's cached channel list
    /// stays empty until the next scheduled guide refresh — this triggers the "RefreshGuide"
    /// scheduled task and polls until channel items appear.
    /// </summary>
    /// <returns><c>true</c> when Jellyfin exposes at least one Live TV channel item.</returns>
    public async Task<bool> EnsureLiveTvChannelsAsync()
    {
        if (!IsAvailable || _authClient == null)
        {
            return false;
        }

        if (await CountLiveTvChannelsAsync().ConfigureAwait(false) > 0)
        {
            return true;
        }

        // Find and trigger the guide-refresh scheduled task.
        var tasksResp = await _authClient.GetAsync("/ScheduledTasks").ConfigureAwait(false);
        if (tasksResp.IsSuccessStatusCode)
        {
            var tasksJson = await tasksResp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var tasksDoc = JsonDocument.Parse(tasksJson);
            foreach (var task in tasksDoc.RootElement.EnumerateArray())
            {
                if (task.TryGetProperty("Key", out var key)
                    && string.Equals(key.GetString(), "RefreshGuide", StringComparison.OrdinalIgnoreCase)
                    && task.TryGetProperty("Id", out var idProp))
                {
                    await _authClient.PostAsync($"/ScheduledTasks/Running/{idProp.GetString()}", null).ConfigureAwait(false);
                    break;
                }
            }
        }

        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(3000).ConfigureAwait(false);
            if (await CountLiveTvChannelsAsync().ConfigureAwait(false) > 0)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<int> CountLiveTvChannelsAsync()
    {
        try
        {
            var resp = await _authClient!.GetAsync("/LiveTv/Channels?limit=1").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return 0;
            }

            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("Items", out var items) ? items.GetArrayLength() : 0;
        }
        catch (HttpRequestException)
        {
            return 0;
        }
    }

    private static async Task ConfigurePluginAsync(HttpClient client, string tvhHost, int tvhPort, string tvhUser, string tvhPass)
    {
        // GET current plugin config.
        var getResp = await client.GetAsync($"/Plugins/{PluginId}/Configuration").ConfigureAwait(false);
        getResp.EnsureSuccessStatusCode();

        var configJson = await getResp.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var configDoc = JsonDocument.Parse(configJson);

        // Build modified config — merge with existing to preserve unknown fields.
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
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
                    case "AuthToken":
                        writer.WriteString("AuthToken", string.Empty);
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

        var updatedConfig = Encoding.UTF8.GetString(ms.ToArray());

        // POST updated config.
        var postResp = await client.PostAsync(
            $"/Plugins/{PluginId}/Configuration",
            new StringContent(updatedConfig, Encoding.UTF8, "application/json")).ConfigureAwait(false);
        postResp.EnsureSuccessStatusCode();
    }

    private static async Task<string> GenerateAuthTokenAsync(HttpClient client)
    {
        var response = await client.PostAsync("/TvHeadendApi/GenerateAuthToken", null).ConfigureAwait(false);

        // Token generation may fail if TVHeadend is not yet fully reachable — return empty.
        if (!response.IsSuccessStatusCode)
        {
            return string.Empty;
        }

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("Success", out var successProp) && successProp.GetBoolean()
            && doc.RootElement.TryGetProperty("AuthToken", out var tokenProp))
        {
            return tokenProp.GetString() ?? string.Empty;
        }

        return string.Empty;
    }
}
