using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Auth;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Auth;

/// <summary>
/// Creates and refreshes TVHeadend auth tokens until a FFmpeg-compatible token is produced.
/// </summary>
internal sealed class TokenService : ITokenService
{
    private readonly ILogger<TokenService> _logger;
    private readonly IApiClient _apiClient;
    private readonly IUrlBuilder _urlBuilder;
    private readonly PluginConfigurationSaver _configSaver;

    /// <summary>
    /// Initializes a new instance of the <see cref="TokenService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="apiClient">TVHeadend API client.</param>
    /// <param name="urlBuilder">TVHeadend URL builder.</param>
    /// <param name="configSaver">Saves configuration changes to the plugin.</param>
    public TokenService(
        ILogger<TokenService> logger,
        IApiClient apiClient,
        IUrlBuilder urlBuilder,
        PluginConfigurationSaver configSaver)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _urlBuilder = urlBuilder ?? throw new ArgumentNullException(nameof(urlBuilder));
        _configSaver = configSaver ?? throw new ArgumentNullException(nameof(configSaver));
    }

    /// <inheritdoc />
    public async Task<AuthTokenGenerationResult> GenerateValidTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = _apiClient.GetCurrentConfiguration();
            if (config == null)
            {
                return new AuthTokenGenerationResult { Success = false, Message = "Plugin configuration is not available." };
            }

            if (string.IsNullOrWhiteSpace(config.Username))
            {
                return new AuthTokenGenerationResult
                {
                    Success = false,
                    Message = "TVHeadend username is required to generate or refresh an auth token."
                };
            }

            using var httpClient = _apiClient.CreateApiHttpClient(config);
            var userUuid = await ResolveUserUuidAsync(httpClient, config, config.Username, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(userUuid))
            {
                return new AuthTokenGenerationResult
                {
                    Success = false,
                    Message = $"TVHeadend user '{config.Username}' was not found. Check the configured username."
                };
            }

            var maxAttempts = config.AuthTokenMaxAttempts > 0 ? config.AuthTokenMaxAttempts : 5;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var useRefresh = attempt > 1;
                var userSnapshot = await LoadUserSnapshotAsync(httpClient, config, userUuid, cancellationToken).ConfigureAwait(false);
                if (userSnapshot == null)
                {
                    return new AuthTokenGenerationResult
                    {
                        Success = false,
                        AttemptCount = attempt,
                        Message = "Could not load TVHeadend user details for token generation."
                    };
                }

                _logger.LogInformation(
                    "{Action} TVHeadend auth token for user '{Username}' (attempt {Attempt}/{MaxAttempts}).",
                    useRefresh ? "Refreshing" : "Creating",
                    config.Username,
                    attempt,
                    maxAttempts);

                await SaveUserAuthTokenAsync(httpClient, config, userSnapshot, useRefresh, cancellationToken).ConfigureAwait(false);

                var tokenSnapshot = await LoadUserSnapshotAsync(httpClient, config, userUuid, cancellationToken).ConfigureAwait(false);
                var token = tokenSnapshot?.AuthToken;
                if (string.IsNullOrWhiteSpace(token))
                {
                    _logger.LogWarning("Attempt {Attempt}/{MaxAttempts} returned no token for user '{Username}'.", attempt, maxAttempts, config.Username);
                    continue;
                }

                if (TokenValidator.IsValidTokenFormat(token))
                {
                    return new AuthTokenGenerationResult
                    {
                        Success = true,
                        AuthToken = token,
                        AttemptCount = attempt,
                        UsedRefresh = useRefresh,
                        Message = useRefresh
                            ? $"Auth token refreshed successfully after {attempt} attempt(s)."
                            : "Auth token generated successfully."
                    };
                }

                _logger.LogWarning(
                    "Attempt {Attempt}/{MaxAttempts} produced token with unsupported characters for user '{Username}'.",
                    attempt,
                    maxAttempts,
                    config.Username);
            }

            return new AuthTokenGenerationResult
            {
                Success = false,
                AttemptCount = maxAttempts,
                Message = "Could not generate an alphanumeric auth token. TVHeadend generated unsupported characters in all attempts."
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to TVHeadend for token generation/refresh.");
            return new AuthTokenGenerationResult
            {
                Success = false,
                Message = $"Cannot connect to TVHeadend: {ex.Message}. Check your connection and authentication settings."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating TVHeadend auth token.");
            return new AuthTokenGenerationResult { Success = false, Message = $"Unexpected error: {ex.Message}" };
        }
    }

    /// <inheritdoc />
    public async Task<AuthTokenGenerationResult> GenerateAndStoreTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tokenResult = await GenerateValidTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!tokenResult.Success || string.IsNullOrWhiteSpace(tokenResult.AuthToken))
            {
                return tokenResult;
            }

            var token = tokenResult.AuthToken;
            _configSaver.Save(cfg => cfg.AuthToken = token);
            _logger.LogInformation("Auth token saved to plugin configuration.");

            return new AuthTokenGenerationResult
            {
                Success = true,
                AuthToken = tokenResult.AuthToken,
                AttemptCount = tokenResult.AttemptCount,
                UsedRefresh = tokenResult.UsedRefresh,
                Message = tokenResult.Message + " Saved to plugin configuration."
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to TVHeadend for token generation.");
            return new AuthTokenGenerationResult
            {
                Success = false,
                Message = $"Cannot connect to TVHeadend: {ex.Message}. Check your connection and authentication settings."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating auth token.");
            return new AuthTokenGenerationResult { Success = false, Message = $"Unexpected error: {ex.Message}" };
        }
    }

    private async Task<string?> ResolveUserUuidAsync(
        HttpClient httpClient,
        Configuration.PluginConfiguration config,
        string username,
        CancellationToken cancellationToken)
    {
        // passwd/entry/grid returns a proper idnode grid with "uuid" and "username" per entry.
        // access/entry/userlist only returns key=val=username pairs (no UUID) and cannot be used here.
        var listUrl = _urlBuilder.BuildApiUrl(config, "api/passwd/entry/grid");
        var response = await _apiClient.GetStringAsync(httpClient, listUrl, cancellationToken).ConfigureAwait(false);
        var userList = JsonSerializer.Deserialize<UserListResponse>(response, JsonDefaults.Api);
        if (userList == null || userList.Entries.Length == 0)
        {
            return null;
        }

        foreach (var entry in userList.Entries)
        {
            var entryUser = entry.Username ?? entry.Val ?? entry.Title ?? string.Empty;
            if (!string.Equals(entryUser, username, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var uuid = entry.Uuid ?? entry.Key;
            if (!string.IsNullOrWhiteSpace(uuid))
            {
                return uuid;
            }
        }

        return null;
    }

    private async Task<UserSnapshot?> LoadUserSnapshotAsync(
        HttpClient httpClient,
        Configuration.PluginConfiguration config,
        string userUuid,
        CancellationToken cancellationToken)
    {
        var loadUrl = _urlBuilder.BuildApiUrl(config, "api/idnode/load");
        using var response = await _apiClient.PostFormAsync(
            httpClient,
            loadUrl,
            new[]
            {
                new KeyValuePair<string, string>("uuid", JsonSerializer.Serialize(new[] { userUuid })),
                new KeyValuePair<string, string>("grid", "1"),
                new KeyValuePair<string, string>("list", "enabled,username,password,authcode,comment")
            },
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var loadResponse = JsonSerializer.Deserialize<IdNodeUserLoadResponse>(body, JsonDefaults.Api);
        if (loadResponse == null || loadResponse.Entries.Length == 0)
        {
            return null;
        }

        var entry = loadResponse.Entries[0];
        return new UserSnapshot
        {
            Uuid = userUuid,
            Enabled = entry.Enabled ?? true,
            Username = string.IsNullOrWhiteSpace(entry.Username) ? config.Username : entry.Username.Trim(),
            Password = string.IsNullOrWhiteSpace(entry.Password) ? config.Password : entry.Password,
            Comment = entry.Comment ?? string.Empty,
            AuthToken = (entry.AuthCode ?? string.Empty).Trim(),
        };
    }

    private async Task SaveUserAuthTokenAsync(
        HttpClient httpClient,
        Configuration.PluginConfiguration config,
        UserSnapshot userSnapshot,
        bool resetToken,
        CancellationToken cancellationToken)
    {
        var saveUrl = _urlBuilder.BuildApiUrl(config, "api/idnode/save");
        var authModes = new List<string> { "enable" };
        if (resetToken)
        {
            authModes.Add("reset");
        }

        var node = new UserTokenSaveNodeRequest
        {
            Enabled = userSnapshot.Enabled,
            Username = userSnapshot.Username,
            Password = userSnapshot.Password,
            Auth = authModes.ToArray(),
            Comment = userSnapshot.Comment,
            Uuid = userSnapshot.Uuid,
        };

        using var response = await _apiClient.PostFormAsync(
            httpClient,
            saveUrl,
            new[]
            {
                new KeyValuePair<string, string>("node", JsonSerializer.Serialize(node, JsonDefaults.Api))
            },
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    private sealed class UserSnapshot
    {
        public string Uuid { get; init; } = string.Empty;

        public bool Enabled { get; init; }

        public string Username { get; init; } = string.Empty;

        public string Password { get; init; } = string.Empty;

        public string Comment { get; init; } = string.Empty;

        public string AuthToken { get; init; } = string.Empty;
    }
}
