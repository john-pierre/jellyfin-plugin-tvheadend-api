using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service;

/// <summary>
/// Provides Live TV integration for Jellyfin using TVHeadEnd as the backend.
/// Implements the <see cref="ILiveTvService"/> interface to manage channels, EPG data, recordings, and streams.
/// </summary>
public sealed class LiveTvService : ILiveTvService, IDisposable
{
    private const char StreamIdDelimiter = '_';

    private readonly ILogger<LiveTvService> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly object _httpClientSync = new();
    private HttpClient _httpClient;
    private string? _httpClientConfigurationKey;
    private bool _disposed;
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Time-to-live for cached profile container. After this period the plugin re-queries TVHeadend.
    /// </summary>
    private static readonly TimeSpan ProfileContainerCacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Lock to prevent concurrent profile container lookups.
    /// </summary>
    private readonly SemaphoreSlim _profileContainerLock = new(1, 1);

    /// <summary>
    /// Cached container string derived from the configured TVHeadend streaming profile.
    /// Invalidated when the profile name changes or when the TTL expires.
    /// </summary>
    private ContainerCacheEntry? _profileContainerCache;

    /// <summary>
    /// A static dictionary mapping ETSI EN 300 468 content type IDs to their human-readable descriptions.
    /// This mapping is based on the standardized genres defined in the DVB specification, which categorizes TV content
    /// into predefined types such as "Movie/Drama", "Sports", and "News/Current Affairs".
    ///
    /// The dictionary's key represents the ETSI genre ID (integer), while the value is the corresponding description (string).
    /// Example:
    /// - Key: 16, Value: "Movie/Drama"
    /// - Key: 64, Value: "Sports"
    ///
    /// This mapping is used throughout the plugin to enrich TVHeadend's raw API data with meaningful genre descriptions,
    /// improving the user experience by providing easily understandable information about TV programs.
    /// </summary>
    private static readonly Dictionary<int, string> EtsiGenreMapping = new()
    {
        // Undefined and reserved categories
        { 0, "Undefined" },
        { 240, "Reserved for future use" },

        // Movie/Drama categories
        { 16, "Movie/Drama" },
        { 17, "Detective/Thriller" },
        { 18, "Adventure/Western/War" },
        { 19, "Science Fiction/Fantasy/Horror" },
        { 20, "Comedy" },
        { 21, "Soap/Melodrama/Drama" },
        { 22, "Romance" },
        { 23, "Serious/Classical/Religious/Historical Movie/Drama" },
        { 24, "Adult Movie/Drama" },

        // News/Current Affairs categories
        { 32, "News/Current Affairs" },
        { 33, "News/Weather Report" },
        { 34, "News Magazine" },
        { 35, "Documentary" },
        { 36, "Discussion/Interview/Debate" },

        // Show/Game Show categories
        { 48, "Show/Game Show" },
        { 49, "Game Show/Quiz/Contest" },
        { 50, "Variety Show" },
        { 51, "Talk Show" },

        // Sports categories
        { 64, "Sports" },
        { 65, "Special Event" },
        { 66, "Sports Magazine" },
        { 67, "Football/Soccer" },
        { 68, "Tennis/Squash" },
        { 69, "Team Sports" },
        { 70, "Athletics" },
        { 71, "Motor Sport" },
        { 72, "Water Sport" },
        { 73, "Winter Sport" },
        { 74, "Equestrian" },
        { 75, "Martial Sports" },

        // Children's/Youth Programs categories
        { 80, "Children's/Youth Programs" },
        { 81, "Pre-school Children's Programs" },
        { 82, "Entertainment/Cartoons" },
        { 83, "Educational/School Programs" },

        // Music/Ballet/Dance categories
        { 96, "Music/Ballet/Dance" },
        { 97, "Rock/Pop" },
        { 98, "Classical Music" },
        { 99, "Folk/Traditional Music" },
        { 100, "Jazz" },
        { 101, "Opera" },
        { 102, "Ballet" },

        // Arts/Culture categories
        { 112, "Arts/Culture (without music)" },
        { 113, "Performing Arts" },
        { 114, "Fine Arts" },
        { 115, "Religion" },
        { 116, "Popular Culture/Tradition" },
        { 117, "Literature" },
        { 118, "Film/Cinema" },
        { 119, "Experimental Film/Video" },
        { 120, "Broadcasting/Press" },
        { 121, "New Media" },
        { 122, "Arts/Culture Magazine" },
        { 123, "Fashion" },

        // Social/Political/Economic categories
        { 128, "Social/Political/Economic" },
        { 129, "Magazines/Reports/Documentary" },
        { 130, "Economics/Social Advisory" },
        { 131, "Remarkable People" },

        // Education/Science/Factual categories
        { 144, "Education/Science/Factual" },
        { 145, "Nature/Animals/Environment" },
        { 146, "Technology/Medical" },
        { 147, "Foreign Countries/Expeditions" },
        { 148, "Social/Spiritual Sciences" },
        { 149, "Further Education" },
        { 150, "Languages" },

        // Leisure Hobbies categories
        { 160, "Leisure Hobbies" },
        { 161, "Tourism/Travel" },
        { 162, "Handicraft" },
        { 163, "Gardening" },
        { 164, "Motors" },
        { 165, "Fitness/Health" },
        { 166, "Cooking" },
        { 167, "Advertisement/Shopping" },
        { 168, "Community" }
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveTvService"/> class, which integrates
    /// the TVHeadEnd backend with Jellyfin for managing Live TV functionality.
    ///
    /// This constructor sets up the following components:
    /// - An HTTP client for making API requests to the TVHeadEnd server.
    /// - SSL/TLS configuration based on default settings (e.g., checking the certificate revocation list).
    ///
    /// Note:
    /// SSL certificate errors are not ignored by default. If needed, handling for such scenarios
    /// must be explicitly configured externally.
    ///
    /// The service relies on the provided logger to record operational messages, warnings, and errors.
    /// </summary>
    /// <param name="logger">
    /// The logger instance used to log messages related to the Live TV service. This parameter is mandatory and must not be null.
    /// </param>
    /// <param name="libraryManager">
    /// Jellyfin library manager used to resolve internal item ids from external identifiers.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown if the <paramref name="logger"/> parameter is null, as logging is critical for service operation.
    /// </exception>
    public LiveTvService(ILogger<LiveTvService> logger, ILibraryManager libraryManager)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));

        var handler = new HttpClientHandler
        {
            CheckCertificateRevocationList = true
        };

        _httpClient = new HttpClient(handler);
    }

    /// <summary>
    /// Gets the name of the Live TV service.
    /// This property returns a constant string identifying the service as "TVHeadEnd",
    /// which is used throughout the Jellyfin plugin system to distinguish this Live TV backend.
    /// </summary>
    public string Name => "TvHeadendApi";

    /// <summary>
    /// Gets the base URL of the TVHeadend server as configured in the plugin settings.
    /// This property retrieves the host name or IP address of the server, which is used as the
    /// starting point for API calls and user-facing links.
    /// </summary>
    public string HomePageUrl => "https://tvheadend.org";

    /// <summary>
    /// Gets a value indicating whether TVHeadend DVR functionality is enabled in the plugin configuration.
    /// </summary>
    private static bool IsTvhDvrEnabled =>
        Plugin.Instance?.Configuration.EnableTvhDvr ?? true;

    /// <summary>
    /// Releases the resources used by the <see cref="LiveTvService"/> class, including
    /// the HTTP client used for communication with the TVHeadEnd server.
    ///
    /// This method ensures that all managed resources are properly cleaned up to prevent
    /// memory leaks, particularly when the plugin is unloaded or reinitialized.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            // Dispose the HTTP client to release underlying network resources
            _httpClient.Dispose();

            // Dispose the profile container lock semaphore
            _profileContainerLock.Dispose();

            // Mark the service as disposed to prevent multiple calls to Dispose
            _disposed = true;
        }
    }

    private void EnsureHttpClientConfigured(PluginConfiguration config)
    {
        var configurationKey = string.Join(
            "|",
            config.UseSSL,
            config.IgnoreCertificateErrors,
            config.AllowAnonymousAccess,
            config.Host,
            config.Port,
            config.Username,
            config.Password);

        if (string.Equals(_httpClientConfigurationKey, configurationKey, StringComparison.Ordinal))
        {
            return;
        }

        lock (_httpClientSync)
        {
            if (string.Equals(_httpClientConfigurationKey, configurationKey, StringComparison.Ordinal))
            {
                return;
            }

            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 20,
                EnableMultipleHttp2Connections = true,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
                KeepAlivePingDelay = TimeSpan.FromSeconds(15),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
            };

            if (config.UseSSL && config.IgnoreCertificateErrors)
            {
                _logger.LogWarning("Ignoring SSL certificate errors. This is not recommended for production environments.");
#pragma warning disable CA5359 // User explicitly opts in via config
                handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
#pragma warning restore CA5359
            }

            var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri($"{(config.UseSSL ? "https" : "http")}://{config.Host}:{config.Port}/"),
                Timeout = TimeSpan.FromSeconds(15),
            };

            if (!config.AllowAnonymousAccess && !string.IsNullOrWhiteSpace(config.Username))
            {
                var headerCredentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{config.Username}:{config.Password}"));
                httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", headerCredentials);
            }

            var previousClient = _httpClient;
            _httpClient = httpClient;
            _httpClientConfigurationKey = configurationKey;
            previousClient.Dispose();
        }
    }

    /// <summary>
    /// Constructs a complete API URL for the TVHeadend service.
    /// This method supports different authentication methods (Basic Auth in URL, Header, or URL parameter).
    /// If anonymous access is allowed, no authentication is used regardless of the specified method.
    /// </summary>
    /// <param name="endpoint">The API endpoint relative to the TVHeadend base URL. Example: "stream/channel/1234".</param>
    /// <param name="authMethod">The authentication method to use: "url", "header", or "parameter". Default is "header".</param>
    /// <returns>A fully constructed URL including the web root and authentication token or credentials, if applicable.</returns>
    public string ConstructUrl(string endpoint, string authMethod = "header")
    {
        // Dynamically fetch the current configuration
        var config = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin configuration is not available.");

        EnsureHttpClientConfigured(config);

        // Ensure the web root is properly formatted with a single trailing slash
        var webRoot = string.IsNullOrWhiteSpace(config.Webroot) ? "/" : config.Webroot.TrimEnd('/') + "/";

        // Construct the base URL with SSL settings and web root
        var baseUrl = $"{(config.UseSSL ? "https" : "http")}://{config.Host}:{config.Port}{webRoot}{endpoint.TrimStart('/')}";

        if (config.AllowAnonymousAccess)
        {
            // If anonymous access is allowed, return the base URL without any authentication
            return baseUrl;
        }

        switch (authMethod.ToLowerInvariant())
        {
            case "url":
                // Append Basic Auth credentials directly in the URL
                var credentials = $"{Uri.EscapeDataString(config.Username)}:{Uri.EscapeDataString(config.Password)}";
                baseUrl = baseUrl.Replace("http://", $"http://{credentials}@", StringComparison.Ordinal)
                                 .Replace("https://", $"https://{credentials}@", StringComparison.Ordinal);
                break;

            case "parameter":
                // Append the authentication token as a query parameter
                if (!string.IsNullOrWhiteSpace(config.AuthToken))
                {
                    var separator = baseUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
                    baseUrl = $"{baseUrl}{separator}auth={Uri.EscapeDataString(config.AuthToken)}";
                }

                break;

            case "header":
            default:
                break;
        }

        return baseUrl;
    }

    /// <summary>
    /// Masks sensitive data within a given string by replacing occurrences of the authentication token
    /// and Basic Auth credentials with placeholders (e.g., "***").
    ///
    /// This method ensures that sensitive information, such as authentication tokens and credentials,
    /// is not exposed in logs or error messages. It operates on the raw string input and applies
    /// the masking operation wherever sensitive data appears.
    /// </summary>
    /// <param name="input">The input string where sensitive data might be present. Example: a URL with an auth token or credentials.</param>
    /// <returns>
    /// The input string with sensitive data replaced by placeholders.
    /// If the token and credentials are not set or not found in the input, the original string is returned.
    /// </returns>
    public string MaskSensitiveData(string input)
    {
        // Dynamically fetch the current configuration
        var config = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin configuration is not available.");

        if (!string.IsNullOrWhiteSpace(config.AuthToken))
        {
            // Replace occurrences of the authentication token with "***"
            input = input.Replace(config.AuthToken, "***", StringComparison.Ordinal);
        }

        if (!string.IsNullOrWhiteSpace(config.Username) && !string.IsNullOrWhiteSpace(config.Password))
        {
            // Encode Basic Auth credentials
            var credentials = $"{config.Username}:{config.Password}";
            var encodedCredentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(credentials));

            // Replace plain text and encoded credentials in the URL
            input = input.Replace(credentials, "***", StringComparison.Ordinal);
            input = input.Replace(encodedCredentials, "***", StringComparison.Ordinal);
        }

        return input;
    }

    /// <summary>
    /// Fetches the list of channels from the TVHeadend API and converts them into Jellyfin-compatible channel objects.
    /// This method queries the `/api/channel/grid` endpoint of TVHeadend, deserializes the response, and maps it to Jellyfin's <see cref="ChannelInfo"/> format.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation if necessary.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a list of <see cref="ChannelInfo"/> objects.</returns>
    public async Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Define the query limit and construct the API URL
            const int limit = 10000;
            // Build the URL for the API endpoint
            var url = ConstructUrl($"api/channel/grid?limit={limit}");

            // Log the URL being used for the API call (sensitive data is masked)
            _logger.LogInformation("Fetching channels from TVHeadEnd at {Url}...", MaskSensitiveData(url));

            // Make an HTTP GET request and deserialize directly from the response stream
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync<TvhApiChannelGridResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);

            // Check if the response contains valid channel entries
            if (result?.Entries != null && result.Entries.Any())
            {
                // Map the TVHeadEnd channels to Jellyfin's ChannelInfo format
                var channels = result.Entries.Select(channel => new ChannelInfo
                {
                    Id = channel.Uuid, // Unique identifier for the channel
                    Name = channel.Name, // Display name of the channel
                    Number = channel.Number % 1 == 0
                        ? ((int)channel.Number).ToString(CultureInfo.InvariantCulture)
                        : channel.Number.ToString(CultureInfo.InvariantCulture),
                    ImageUrl = !string.IsNullOrWhiteSpace(channel.IconPublicUrl)
                        ? ConstructUrl($"api/TvHeadendApi/ImageProxy?imagePath={Uri.EscapeDataString(channel.IconPublicUrl.TrimStart('/'))}")
                        : null, // URL to the image proxy endpoint (credentials never exposed)
                    HasImage = !string.IsNullOrWhiteSpace(channel.IconPublicUrl) // Set HasImage based on the existence of ImageUrl
                }).ToList();

                // Log the number of channels successfully retrieved and mapped
                _logger.LogInformation("Successfully retrieved {Count} channels from TVHeadEnd.", channels.Count);

                // Return the list of mapped channels
                return channels;
            }

            // Log a warning if no channels were found
            _logger.LogWarning("No channels retrieved from TVHeadEnd.");
            return Enumerable.Empty<ChannelInfo>();
        }
        catch (Exception ex)
        {
            // Log any errors that occur during the channel retrieval process
            _logger.LogError(ex, "Error fetching channels from TVHeadEnd.");

            // Return an empty list if an error occurs
            return Enumerable.Empty<ChannelInfo>();
        }
    }

    /// <summary>
    /// Cancels an existing recording timer on TVHeadend.
    /// This method sends a request to the TVHeadend API to cancel a scheduled recording identified by its timer ID.
    /// </summary>
    /// <param name="timerId">The unique identifier of the timer to be canceled.</param>
    /// <param name="cancellationToken">A token to cancel the operation if necessary.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="ArgumentException">Thrown if the provided <paramref name="timerId"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the API request to cancel the timer fails.</exception>
    public async Task CancelTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        if (!IsTvhDvrEnabled)
        {
            _logger.LogInformation("TVHeadend DVR is disabled. Skipping CancelTimerAsync.");
            throw new NotSupportedException("TVHeadend DVR is disabled in the plugin configuration.");
        }

        try
        {
            // Validate the timerId parameter to ensure it is not null or empty
            if (string.IsNullOrWhiteSpace(timerId))
            {
                throw new ArgumentException("Timer ID cannot be null or empty.", nameof(timerId));
            }

            // Build the URL for the API endpoint
            var url = ConstructUrl("api/dvr/entry/cancel");

            // Log the cancellation request for debugging purposes
            _logger.LogInformation("Sending cancel timer request to TVHeadEnd for timer ID: {TimerId} via {Url}.", timerId, MaskSensitiveData(url));

            // Prepare the HTTP client and request content
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("uuid", timerId) // Pass the timer ID as a form parameter
            });

            // Send a POST request to the TVHeadEnd API to cancel the timer
            using var response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

            // Ensure the response indicates success (HTTP status code 200-299)
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully canceled timer with ID: {TimerId}.", timerId);
            }
            else
            {
                // Log a warning if the API response indicates a failure
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"Failed to cancel timer with ID: {timerId}. HTTP Status: {response.StatusCode}. Response: {responseContent}.");
            }
        }
        catch (Exception ex)
        {
            // Log any errors that occur during the cancellation process
            _logger.LogError(ex, "Error occurred while attempting to cancel timer with ID: {TimerId}.", timerId);
            throw; // Re-throw the exception to ensure the caller is aware of the failure
        }
    }

    /// <summary>
    /// Cancels a scheduled series timer on TVHeadend.
    /// This method sends a request to the TVHeadend API to cancel all scheduled recordings for a series identified by its timer ID.
    /// </summary>
    /// <param name="timerId">The unique identifier of the series timer to be canceled.</param>
    /// <param name="cancellationToken">A token to cancel the operation if necessary.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="ArgumentException">Thrown if the provided <paramref name="timerId"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the API request to cancel the series timer fails.</exception>
    public async Task CancelSeriesTimerAsync(string timerId, CancellationToken cancellationToken)
    {
        if (!IsTvhDvrEnabled)
        {
            _logger.LogInformation("TVHeadend DVR is disabled. Skipping CancelSeriesTimerAsync.");
            throw new NotSupportedException("TVHeadend DVR is disabled in the plugin configuration.");
        }

        try
        {
            // Validate the timerId parameter to ensure it is not null or empty
            if (string.IsNullOrWhiteSpace(timerId))
            {
                throw new ArgumentException("Series timer ID cannot be null or empty.", nameof(timerId));
            }

            // Build the URL for the API endpoint
            var url = ConstructUrl("api/idnode/delete");

            // Log the cancellation request for debugging purposes
            _logger.LogInformation("Sending cancel series timer request to TVHeadEnd for timer ID: {TimerId} via {Url}.", timerId, MaskSensitiveData(url));

            // Prepare the HTTP client and request content
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("uuid", timerId) // Pass the timer ID as a form parameter
            });

            // Send a POST request to the TVHeadEnd API to cancel the series timer
            using var response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

            // Ensure the response indicates success (HTTP status code 200-299)
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully canceled series timer with ID: {TimerId}.", timerId);
            }
            else
            {
                // Log a warning if the API response indicates a failure
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"Failed to cancel series timer with ID: {timerId}. HTTP Status: {response.StatusCode}. Response: {responseContent}.");
            }
        }
        catch (Exception ex)
        {
            // Log any errors that occur during the cancellation process
            _logger.LogError(ex, "Error occurred while attempting to cancel series timer with ID: {TimerId}.", timerId);
            throw; // Re-throw the exception to ensure the caller is aware of the failure
        }
    }

    /// <summary>
    /// Creates a new recording timer on TVHeadend for a specific program or channel.
    /// This method sends a JSON object to the TVHeadend API to schedule a new recording.
    /// If a ProgramId is provided, it uses the `dvr/entry/create_by_event` endpoint.
    /// </summary>
    /// <param name="info">The timer information, including the program details and scheduling preferences.</param>
    /// <param name="cancellationToken">A token to cancel the operation if necessary.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="info"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="info.ChannelId"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the API request fails.</exception>
    public async Task CreateTimerAsync(TimerInfo info, CancellationToken cancellationToken)
    {
        if (!IsTvhDvrEnabled)
        {
            _logger.LogInformation("TVHeadend DVR is disabled. Skipping CreateTimerAsync.");
            throw new NotSupportedException("TVHeadend DVR is disabled in the plugin configuration.");
        }

        try
        {
            // Validate the TimerInfo parameter
            if (info == null)
            {
                throw new ArgumentNullException(nameof(info), "TimerInfo cannot be null.");
            }

            if (string.IsNullOrWhiteSpace(info.ChannelId))
            {
                throw new ArgumentException("Channel ID cannot be null or empty.", nameof(info));
            }

            if (info.StartDate == default || info.EndDate == default || info.StartDate >= info.EndDate)
            {
                throw new ArgumentException("Invalid start or end date for the timer.", nameof(info));
            }

            // Dynamically fetch the current configuration
            var config = Plugin.Instance?.Configuration
                ?? throw new InvalidOperationException("Plugin configuration is not available.");

            // Get the recording profile UUID once
            var configUuid = await GetRecordingProfileUuidAsync(config.RecordingProfile, cancellationToken).ConfigureAwait(false);

            // Determine the endpoint and content based on whether ProgramId is provided
            string path;
            FormUrlEncodedContent content;

            if (!string.IsNullOrWhiteSpace(info.ProgramId))
            {
                // Use the EPG-based endpoint if ProgramId is provided
                path = "api/dvr/entry/create_by_event";
                _logger.LogInformation("Using 'create_by_event' endpoint for ProgramId: {ProgramId}.", info.ProgramId);

                // Prepare URL-encoded content for the EPG-based request
                content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("config_uuid", configUuid),
                    new KeyValuePair<string, string>("event_id", info.ProgramId)
                });
            }
            else
            {
                // Use the standard endpoint for channel-based timers
                path = "api/dvr/entry/create";
                _logger.LogInformation("Using 'create' endpoint for channel ID: {ChannelId}.", info.ChannelId);

                // Prepare the JSON object for the request
                var timerJson = new
                {
                    channel = info.ChannelId,
                    start = new DateTimeOffset(info.StartDate).ToUnixTimeSeconds(),
                    stop = new DateTimeOffset(info.EndDate).ToUnixTimeSeconds(),
                    start_extra = (int)Math.Round((double)info.PrePaddingSeconds / 60),
                    stop_extra = (int)Math.Round((double)info.PostPaddingSeconds / 60),
                    disp_title = info.Name,
                    disp_extratext = info.Overview,
                    pri = config.Priority,
                    config_name = configUuid
                };

                // Serialize and URL-encode the JSON object
                var serializedJson = JsonSerializer.Serialize(timerJson, JsonOptions);
                content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("conf", serializedJson)
                });
            }

            // Build the complete URL
            var url = ConstructUrl(path);

            // Send the POST request
            using var response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

            // Ensure the response indicates success (HTTP status code 200-299)
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully created timer for program '{ProgramName}' on channel ID: {ChannelId} via {Url}.", info.Name, info.ChannelId, MaskSensitiveData(url));
            }
            else
            {
                // Log a warning if the API response indicates a failure
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"Failed to create timer for program '{info.Name}' on channel ID: '{info.ChannelId}'. HTTP Status: {response.StatusCode}. Response: {responseContent}");
            }
        }
        catch (Exception ex)
        {
            // Log any errors that occur during the timer creation process
            _logger.LogError(ex, "Error occurred while attempting to create timer for program '{ProgramName}' on channel ID: {ChannelId}.", info.Name, info.ChannelId);
            throw; // Re-throw the exception to ensure the caller is aware of the failure
        }
    }

    /// <summary>
    /// Creates a new series timer on TVHeadend to automatically record all episodes of a series.
    /// Depending on the provided information, it uses either `dvr/autorec/create` or `dvr/autorec/create_by_series`.
    /// </summary>
    /// <param name="info">The series timer information, including scheduling preferences and metadata.</param>
    /// <param name="cancellationToken">A token to cancel the operation if necessary.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="info"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="info.ChannelId"/> or <paramref name="info.Name"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the API request to create the series timer fails.</exception>
    public async Task CreateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        if (!IsTvhDvrEnabled)
        {
            _logger.LogInformation("TVHeadend DVR is disabled. Skipping CreateSeriesTimerAsync.");
            throw new NotSupportedException("TVHeadend DVR is disabled in the plugin configuration.");
        }

        try
        {
            // Validate the SeriesTimerInfo parameter
            if (info == null)
            {
                throw new ArgumentNullException(nameof(info), "SeriesTimerInfo cannot be null.");
            }

            if (string.IsNullOrWhiteSpace(info.ChannelId))
            {
                throw new ArgumentException("Channel ID cannot be null or empty.", nameof(info));
            }

            if (string.IsNullOrWhiteSpace(info.Name))
            {
                throw new ArgumentException("Series name cannot be null or empty.", nameof(info));
            }

            // Dynamically fetch the current configuration
            var config = Plugin.Instance?.Configuration
                ?? throw new InvalidOperationException("Plugin configuration is not available.");

            // Get the recording profile UUID once
            var configUuid = await GetRecordingProfileUuidAsync(config.RecordingProfile, cancellationToken).ConfigureAwait(false);

            // Determine the endpoint and content based on whether ProgramId is provided
            string path;
            FormUrlEncodedContent content;

            if (!string.IsNullOrWhiteSpace(info.ProgramId))
            {
                // Use the CRID-based endpoint if ProgramId is provided
                path = "api/dvr/autorec/create_by_series";
                _logger.LogInformation("Using 'create_by_series' endpoint for ProgramId: {ProgramId}.", info.ProgramId);

                // Prepare URL-encoded content for the CRID-based request
                content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("config_uuid", configUuid),
                    new KeyValuePair<string, string>("event_id", info.ProgramId)
                });
            }
            else
            {
                // Use the standard endpoint for search-parameter-based series timers
                path = "api/dvr/autorec/create";
                _logger.LogInformation("Using 'create' endpoint for channel ID: {ChannelId}.", info.ChannelId);

                // Prepare the JSON object for the request
                var seriesTimerJson = new
                {
                    channel = info.ChannelId,
                    title = info.Name,
                    description = info.Overview,
                    record_any_time = info.RecordAnyTime,
                    record_any_channel = info.RecordAnyChannel,
                    record_new_only = info.RecordNewOnly,
                    priority = config.Priority,
                    start_extra = (int)Math.Round((double)info.PrePaddingSeconds / 60),
                    stop_extra = (int)Math.Round((double)info.PostPaddingSeconds / 60),
                    weekdays = new List<int> { 1, 2, 3, 4, 5, 6, 7 }, // Represents all weekdays (Mon-Sun)
                    config_uuid = configUuid
                };

                // Serialize the JSON object to a URL-encoded string
                var serializedJson = JsonSerializer.Serialize(seriesTimerJson, JsonOptions);
                content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("conf", serializedJson)
                });
            }

            // Build the complete URL
            var url = ConstructUrl(path);

            // Send the POST request
            using var response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully created series timer for series '{SeriesName}' on channel ID: {ChannelId} via {Url}.", info.Name, info.ChannelId, MaskSensitiveData(url));
            }
            else
            {
                // Log a warning if the API response indicates a failure
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"Failed to create series timer for series '{info.Name}' on channel ID: {info.ChannelId}. HTTP Status: {response.StatusCode}. Response: {responseContent}.");
            }
        }
        catch (Exception ex)
        {
            // Log any errors that occur during the series timer creation process
            _logger.LogError(ex, "Error occurred while attempting to create series timer for series '{SeriesName}' on channel ID: {ChannelId}.", info.Name, info.ChannelId);
            throw; // Re-throw the exception to ensure the caller is aware of the failure
        }
    }

    /// <summary>
    /// Updates an existing recording timer on TVHeadend.
    /// This method uses the `idnode/save` API to update the timer fields directly with a form-encoded request.
    /// </summary>
    /// <param name="updatedTimer">The updated timer information, including scheduling preferences and metadata.</param>
    /// <param name="cancellationToken">A token to cancel the operation if necessary.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="updatedTimer"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="updatedTimer.Id"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the API request fails.</exception>
    public async Task UpdateTimerAsync(TimerInfo updatedTimer, CancellationToken cancellationToken)
    {
        if (!IsTvhDvrEnabled)
        {
            _logger.LogInformation("TVHeadend DVR is disabled. Skipping UpdateTimerAsync.");
            throw new NotSupportedException("TVHeadend DVR is disabled in the plugin configuration.");
        }

        try
        {
            // Validate the TimerInfo parameter to ensure it contains valid data
            if (updatedTimer == null)
            {
                throw new ArgumentNullException(nameof(updatedTimer), "Updated timer information cannot be null.");
            }

            if (string.IsNullOrWhiteSpace(updatedTimer.Id))
            {
                throw new ArgumentException("Timer ID cannot be null or empty.", nameof(updatedTimer));
            }

            var url = ConstructUrl("api/idnode/save");

            // Prepare the fields to update for a normal recording
            var updates = new Dictionary<string, object>
            {
                { "uuid", updatedTimer.Id },
                { "start_extra", (int)Math.Round((double)updatedTimer.PrePaddingSeconds / 60) },
                { "stop_extra", (int)Math.Round((double)updatedTimer.PostPaddingSeconds / 60) }
            };

            // Serialize the updates into a JSON object
            var jsonNode = JsonSerializer.Serialize(new[] { updates });

            // Prepare the form-encoded content
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("node", jsonNode)
            });

            // Log the update request
            _logger.LogInformation("Updating timer with ID: {TimerId}.", updatedTimer.Id);

            // Send the POST request to the TVHeadEnd API
            using var response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

            // Check if the response was successful
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully updated timer with ID: {TimerId}.", updatedTimer.Id);
            }
            else
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"Failed to update timer with ID: {updatedTimer.Id}. HTTP Status: {response.StatusCode}. Response: {responseContent}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while attempting to update timer with ID: {TimerId}.", updatedTimer.Id);
            throw; // Re-throw the exception to ensure the caller is aware of the failure
        }
    }

    /// <summary>
    /// Updates an existing series timer on TVHeadend.
    /// This method uses the `idnode/save` API to update the series timer fields directly with a form-encoded request.
    /// </summary>
    /// <param name="info">The updated series timer information, including scheduling preferences and metadata.</param>
    /// <param name="cancellationToken">A token to cancel the operation if necessary.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="info"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="info.Id"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the API request fails.</exception>
    public async Task UpdateSeriesTimerAsync(SeriesTimerInfo info, CancellationToken cancellationToken)
    {
        if (!IsTvhDvrEnabled)
        {
            _logger.LogInformation("TVHeadend DVR is disabled. Skipping UpdateSeriesTimerAsync.");
            throw new NotSupportedException("TVHeadend DVR is disabled in the plugin configuration.");
        }

        try
        {
            // Validate the SeriesTimerInfo parameter to ensure it contains valid data
            if (info == null)
            {
                throw new ArgumentNullException(nameof(info), "Updated series timer information cannot be null.");
            }

            if (string.IsNullOrWhiteSpace(info.Id))
            {
                throw new ArgumentException("Series Timer ID cannot be null or empty.", nameof(info));
            }

            var url = ConstructUrl("api/idnode/save");

            // Prepare the fields to update for a series recording
            var updates = new Dictionary<string, object>
            {
                { "uuid", info.Id },
                { "channel", info.ChannelId },
                { "record_any_time", info.RecordAnyTime },
                { "record_new_only", info.RecordNewOnly },
                { "start_extra", (int)Math.Round((double)info.PrePaddingSeconds / 60) },
                { "stop_extra", (int)Math.Round((double)info.PostPaddingSeconds / 60) }
            };

            // Serialize the updates into a JSON object
            var jsonNode = JsonSerializer.Serialize(new[] { updates });

            // Prepare the form-encoded content
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("node", jsonNode)
            });

            // Log the update request
            _logger.LogInformation("Updating series timer with ID: {SeriesTimerId}.", info.Id);

            // Send the POST request to the TVHeadEnd API
            using var response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

            // Check if the response was successful
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully updated series timer with ID: {SeriesTimerId}.", info.Id);
            }
            else
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException($"Failed to update series timer with ID: {info.Id}. HTTP Status: {response.StatusCode}. Response: {responseContent}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while attempting to update series timer with ID: {SeriesTimerId}.", info.Id);
            throw; // Re-throw the exception to ensure the caller is aware of the failure
        }
    }

    /// <summary>
    /// Fetches all active recording timers from TVHeadend.
    /// This method queries the TVHeadend API to retrieve the list of currently scheduled recordings.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation if necessary.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains an enumerable list of <see cref="TimerInfo"/> objects representing the active timers.
    /// </returns>
    public async Task<IEnumerable<TimerInfo>> GetTimersAsync(CancellationToken cancellationToken)
    {
        if (!IsTvhDvrEnabled)
        {
            _logger.LogInformation("TVHeadend DVR is disabled. Returning empty timer list.");
            return Enumerable.Empty<TimerInfo>();
        }

        try
        {
            const int limit = 10000;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var url = ConstructUrl($"api/dvr/entry/grid?limit={limit}");

            _logger.LogInformation("Fetching timers from TVHeadEnd at {Url}...", MaskSensitiveData(url));

            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync<TvhApiDvrEntryGridResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);

            var timers = result?.Entries?
                .Where(entry => entry.Enabled && entry.FileRemoved == 0 && entry.Stop >= now)
                .Select(entry => new TimerInfo
                {
                    Id = entry.Uuid,
                    ProgramId = entry.Broadcast > 0 ? entry.Broadcast.ToString(CultureInfo.InvariantCulture) : null,
                    ChannelId = entry.Channel,
                    Name = string.IsNullOrWhiteSpace(entry.DispTitle) ? entry.ChannelName : entry.DispTitle,
                    Overview = string.IsNullOrWhiteSpace(entry.DispDescription)
                        ? string.IsNullOrWhiteSpace(entry.DispExtraText)
                            ? entry.DispSummary
                            : entry.DispExtraText
                        : entry.DispDescription,
                    StartDate = DateTimeOffset.FromUnixTimeSeconds(entry.Start).UtcDateTime,
                    EndDate = DateTimeOffset.FromUnixTimeSeconds(entry.Stop).UtcDateTime,
                    PrePaddingSeconds = Math.Max(0, entry.StartExtra * 60),
                    PostPaddingSeconds = Math.Max(0, entry.StopExtra * 60)
                })
                .ToList();

            if (timers != null && timers.Count != 0)
            {
                _logger.LogInformation("Successfully retrieved {Count} timers from TVHeadEnd.", timers.Count);
                return timers;
            }

            _logger.LogInformation("No timers retrieved from TVHeadEnd.");
            return Enumerable.Empty<TimerInfo>();
        }
        catch (Exception ex)
        {
            // Log any errors that occur during the timer retrieval process
            _logger.LogError(ex, "Error occurred while fetching timers from TVHeadEnd.");
            return Enumerable.Empty<TimerInfo>();
        }
    }

    /// <summary>
    /// Provides default values for a new recording timer.
    /// This method generates default values for a timer based on the provided program information.
    /// If no program information is provided, general default values are returned.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation if necessary.</param>
    /// <param name="program">Optional program information to prefill the timer with specific details.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains a <see cref="SeriesTimerInfo"/> object with default values.
    /// </returns>
    public Task<SeriesTimerInfo> GetNewTimerDefaultsAsync(CancellationToken cancellationToken, ProgramInfo program)
    {
        if (!IsTvhDvrEnabled)
        {
            _logger.LogInformation("TVHeadend DVR is disabled. Returning minimal timer defaults.");
            return Task.FromResult(new SeriesTimerInfo());
        }

        // Log the generation of default timer settings
        _logger.LogInformation("Generating default timer settings...");

        // Dynamically fetch the current configuration
        var config = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin configuration is not available.");

        // Create a new TimerInfo object with default values
        var defaultTimer = new SeriesTimerInfo
        {
            Id = Guid.NewGuid().ToString(),
            ChannelId = program?.ChannelId,
            Name = program?.Name,
            Overview = program?.Overview,
            RecordNewOnly = true,
            RecordAnyTime = true,
            RecordAnyChannel = false,
            Priority = config.Priority,
            PrePaddingSeconds = config.PrePaddingSeconds,
            PostPaddingSeconds = config.PostPaddingSeconds,
            KeepUntil = KeepUntil.UntilDeleted,
            Days = Enum.GetValues(typeof(DayOfWeek)).Cast<DayOfWeek>().ToList() // Generates all days of the week
        };

        // Log the generated default timer for debugging
        _logger.LogInformation("Default timer generated: ID={Id}, Name={Name}, ChannelId={ChannelId}", defaultTimer.Id, defaultTimer.Name, defaultTimer.ChannelId);

        // Return the generated TimerInfo as a completed task
        return Task.FromResult(defaultTimer);
    }

    /// <summary>
    /// Fetches all active series timers from TVHeadend.
    /// This method queries the TVHeadend API to retrieve the list of currently scheduled series timers.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation if necessary.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains an enumerable list of <see cref="SeriesTimerInfo"/> objects representing the active series timers.
    /// </returns>
    public async Task<IEnumerable<SeriesTimerInfo>> GetSeriesTimersAsync(CancellationToken cancellationToken)
    {
        if (!IsTvhDvrEnabled)
        {
            _logger.LogInformation("TVHeadend DVR is disabled. Returning empty series timer list.");
            return Enumerable.Empty<SeriesTimerInfo>();
        }

        try
        {
            // Define the relative path for the API endpoint
            var url = ConstructUrl("api/dvr/autorec/grid");

            // Log the request for debugging purposes
            _logger.LogInformation("Fetching series timers from TVHeadEnd at {Url}...", MaskSensitiveData(url));

            // Send an HTTP GET request and deserialize directly from the response stream
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync<TvhApiDvrAutoRecGridResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);

            // Check if the response contains valid series timer entries
            if (result?.Entries != null && result.Entries.Any())
            {
                // Map TVHeadEnd series timers to Jellyfin's SeriesTimerInfo format
                var seriesTimers = result.Entries.Select(entry => new SeriesTimerInfo
                {
                    Id = entry.Uuid, // Maps to the UUID of the recording rule.
                    Name = entry.Name, // Maps to the name of the recording rule.
                    ChannelId = entry.Channel, // Maps to the channel ID associated with the recording rule.
                    Priority = entry.Priority, // Maps to the recording priority.
                    Overview = entry.Comment, // Maps the comment field as an overview or description.
                    Days = entry.Weekdays?
                        .Where(day => day >= 1 && day <= 7) // Ensure the day is in a valid range (1=Monday, ..., 7=Sunday)
                        .Select(day => (DayOfWeek)(day - 1)) // Convert to DayOfWeek enum
                        .ToList() ?? new List<DayOfWeek>(),
                    RecordNewOnly = false, // Default value as no direct mapping is available in the data.
                    StartDate = DateTime.UtcNow, // Default start date as no specific data is provided.
                    EndDate = DateTime.UtcNow.AddHours(1), // Default end date as no specific data is provided.
                    RecordAnyTime = true, // Default value; assumes recording at any time as no specific constraints exist.
                    RecordAnyChannel = false // Default value; assumes the recording is specific to a single channel.
                }).ToList();

                // Log the number of series timers successfully retrieved and mapped
                _logger.LogInformation("Successfully retrieved {Count} series timers from TVHeadEnd.", seriesTimers.Count);

                return seriesTimers;
            }

            // If no series timers were found, log and return an empty list
            _logger.LogInformation("No series timers retrieved from TVHeadEnd.");
            return Enumerable.Empty<SeriesTimerInfo>();
        }
        catch (Exception ex)
        {
            // Log any errors that occur during the series timer retrieval process
            _logger.LogError(ex, "Error occurred while fetching series timers from TVHeadEnd.");
            return Enumerable.Empty<SeriesTimerInfo>();
        }
    }

    /// <summary>
    /// Fetches the EPG (Electronic Program Guide) data for a specific channel within a given time range.
    /// This method queries the TVHeadend API to retrieve program listings for a specific channel
    /// and maps genres based on ETSI EN 300 468 content type values.
    /// </summary>
    /// <param name="channelId">The unique identifier of the channel to retrieve programs for.</param>
    /// <param name="startDateUtc">The start of the time range in UTC.</param>
    /// <param name="endDateUtc">The end of the time range in UTC.</param>
    /// <param name="cancellationToken">A token to cancel the operation if necessary.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains an enumerable list of <see cref="ProgramInfo"/> objects
    /// with genres and attributes mapped from ETSI EN 300 468.
    /// </returns>
    public async Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
    {
        try
        {
            // Validate the channelId parameter
            if (string.IsNullOrWhiteSpace(channelId))
            {
                throw new ArgumentException("Channel ID cannot be null or empty.", nameof(channelId));
            }

            // Define the query limit and construct the API URL
            const int limit = 10000;
            var url = ConstructUrl($"api/epg/events/grid?channel={channelId}&limit={limit}");

            // Log the request for debugging purposes
            _logger.LogInformation("Fetching EPG data from TVHeadEnd at {Url} for channel ID: {ChannelId}, between {StartDate} and {EndDate}.", MaskSensitiveData(url), channelId, startDateUtc, endDateUtc);

            // Send the GET request to the TVHeadend API
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            // Handle unsuccessful responses
            if (!response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogError("Failed to fetch EPG data for channel ID: {ChannelId}. HTTP Status: {StatusCode}. Response: {Response}.", channelId, response.StatusCode, responseContent);
                return Enumerable.Empty<ProgramInfo>();
            }

            // Deserialize the JSON response directly from the response stream
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync<TvhApiEpgEventsGridResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);

            // Filter and map the results
            var filteredPrograms = result?.Entries?
                .Where(entry =>
                {
                    var programStart = DateTimeOffset.FromUnixTimeSeconds(entry.Start).UtcDateTime;
                    var programEnd = DateTimeOffset.FromUnixTimeSeconds(entry.Stop).UtcDateTime;
                    return entry.ChannelUuid == channelId
                        && programStart < endDateUtc
                        && programEnd > startDateUtc;
                })
                .Select(entry =>
                {
                    // Determine the image URL (use proxy for imagecache paths)
                    string? imageUrl = null;
                    if (!string.IsNullOrWhiteSpace(entry.Image))
                    {
                        imageUrl = entry.Image.StartsWith("imagecache/", StringComparison.Ordinal)
                            ? ConstructUrl($"api/TvHeadendApi/ImageProxy?imagePath={Uri.EscapeDataString(entry.Image)}")
                            : entry.Image;
                    }
                    else if (!string.IsNullOrWhiteSpace(entry.ChannelIcon))
                    {
                        imageUrl = ConstructUrl($"api/TvHeadendApi/ImageProxy?imagePath={Uri.EscapeDataString(entry.ChannelIcon)}");
                    }

                    // Map to ProgramInfo
                    return new ProgramInfo
                    {
                        Id = entry.EventId.ToString(CultureInfo.InvariantCulture),
                        ChannelId = entry.ChannelUuid,
                        Name = entry.Title,
                        Overview = entry.Description,
                        ShortOverview = entry.Summary,
                        StartDate = DateTimeOffset.FromUnixTimeSeconds(entry.Start).UtcDateTime,
                        EndDate = DateTimeOffset.FromUnixTimeSeconds(entry.Stop).UtcDateTime,
                        Genres = entry.Genre?.Select(genreId =>
                            EtsiGenreMapping.TryGetValue(genreId, out var genreDescription)
                                ? genreDescription
                                : $"Unknown ({genreId})").ToList() ?? new List<string>(),
                        IsHD = entry.IsHD == 1,
                        EpisodeTitle = entry.Subtitle,
                        OfficialRating = entry.RatingLabel,
                        ImageUrl = imageUrl,
                        HasImage = !string.IsNullOrEmpty(imageUrl),
                        IsMovie = entry.Genre?.Any(genreId => genreId >= 16 && genreId <= 24) ?? false,
                        IsSports = entry.Genre?.Any(genreId => genreId >= 64 && genreId <= 75) ?? false,
                        IsNews = entry.Genre?.Any(genreId => genreId >= 32 && genreId <= 36) ?? false,
                        IsKids = entry.Genre?.Any(genreId => genreId >= 80 && genreId <= 83) ?? false,
                        IsSeries = entry.Genre?.Any(genreId => genreId >= 48 && genreId <= 51) ?? false
                    };
                })
                .ToList();

            // Log the number of programs retrieved
            _logger.LogInformation("Successfully retrieved {Count} programs for channel ID: {ChannelId}.", filteredPrograms?.Count ?? 0, channelId);

            return filteredPrograms ?? Enumerable.Empty<ProgramInfo>();
        }
        catch (Exception ex)
        {
            // Log any errors that occur during the EPG retrieval process
            _logger.LogError(ex, "Error occurred while fetching EPG data for channel ID: {ChannelId}.", channelId);
            return Enumerable.Empty<ProgramInfo>();
        }
    }

    /// <summary>
    /// Reads a string property from a <see cref="JsonElement"/>. Returns null when the property is missing.
    /// </summary>
    private static string? GetJsonStringProp(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var prop))
        {
            return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
        }

        return null;
    }

    /// <summary>
    /// Reads an integer property from a <see cref="JsonElement"/>. Returns null when the property is missing.
    /// </summary>
    private static int? GetJsonIntProp(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var intVal))
            {
                return intVal;
            }

            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads a string property either from an idnode entry or from its params[] value payload.
    /// </summary>
    private static string? GetJsonStringPropOrParam(JsonElement element, string name)
    {
        return GetJsonStringProp(element, name) ?? GetJsonParamStringProp(element, name);
    }

    /// <summary>
    /// Reads an integer property either from an idnode entry or from its params[] value payload.
    /// </summary>
    private static int? GetJsonIntPropOrParam(JsonElement element, string name)
    {
        return GetJsonIntProp(element, name) ?? GetJsonParamIntProp(element, name);
    }

    private static string? GetJsonParamStringProp(JsonElement element, string name)
    {
        if (!TryGetJsonParamValue(element, name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int? GetJsonParamIntProp(JsonElement element, string name)
    {
        if (!TryGetJsonParamValue(element, name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static bool TryGetJsonParamValue(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
        {
            foreach (var parameter in parameters.EnumerateArray())
            {
                var id = GetJsonStringProp(parameter, "id");
                if (!string.Equals(id, name, StringComparison.OrdinalIgnoreCase)
                    || !parameter.TryGetProperty("value", out value))
                {
                    continue;
                }

                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Builds the exact Jellyfin mediainfo cache filename from LiveTV token parts.
    /// </summary>
    /// <param name="providerTypeOrHash">
    /// Either provider type full name (for example <c>Jellyfin.LiveTv.LiveTvMediaSourceProvider</c>)
    /// or an already hashed 32-char provider hash.
    /// </param>
    /// <param name="itemTypeName">Item type name (for example <c>LiveTvChannel</c>).</param>
    /// <param name="itemIdN">Item id in <c>N</c> format.</param>
    /// <param name="sourceId">Source id, or empty when not available.</param>
    /// <returns>Cache file name like <c>d966....json</c>.</returns>
    private static string BuildMediainfoCacheFileName(
        string providerTypeOrHash,
        string itemTypeName,
        string itemIdN,
        string? sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerTypeOrHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemTypeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemIdN);

        static string GetJellyfinHashN(string value)
        {
            var bytes = Encoding.Unicode.GetBytes(value); // UTF-16LE
#pragma warning disable CA5351
            var hashBytes = MD5.HashData(bytes);
#pragma warning restore CA5351
            return new Guid(hashBytes).ToString("N");
        }

        static bool IsHex32(string value)
            => value.Length == 32 && value.All(Uri.IsHexDigit);

        // MediaSourceManager uses MD5(provider.GetType().FullName) as prefix.
        var providerHash = IsHex32(providerTypeOrHash)
            ? providerTypeOrHash.ToLowerInvariant()
            : GetJellyfinHashN(providerTypeOrHash);

        // LiveTvMediaSourceProvider builds: ItemType_ItemId_SourceId
        var coreOpenToken = string.Join(
            StreamIdDelimiter,
            itemTypeName,
            itemIdN,
            sourceId ?? string.Empty);

        // MediaSourceManager.SetKeyProperties prefixes provider hash.
        var openToken = string.Join(StreamIdDelimiter, providerHash, coreOpenToken);

        // LiveStreamHelper uses MD5(openToken) + ".json"
        return GetJellyfinHashN(openToken) + ".json";
    }

    /// <summary>
    /// Uses Jellyfin core id generation to resolve the internal channel id from the external id.
    /// </summary>
    /// <param name="serviceName">Live TV service name (for example <c>TvHeadendApi</c>).</param>
    /// <param name="externalId">External provider channel id.</param>
    /// <returns>Internal Jellyfin channel id.</returns>
    private Guid GetInternalChannelId(string serviceName, string externalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        const string internalVersionNumber = "4";
        var name = serviceName + externalId + internalVersionNumber;
        return _libraryManager.GetNewItemId(name.ToLowerInvariant(), typeof(LiveTvChannel));
    }

    /// <summary>
    /// Builds a <see cref="MediaSourceInfo"/> for a given channel.
    /// <para>
    /// When <see cref="PluginConfiguration.EnableMediaInfoCacheWrite"/> is enabled, the method
    /// pre-creates a Jellyfin mediainfo cache file with H264+AAC metadata so that Jellyfin's
    /// <c>AddMediaInfoWithProbe</c> finds it and skips actual FFmpeg probing.
    /// </para>
    /// </summary>
    private async Task<MediaSourceInfo> BuildMediaSourceInfoAsync(string channelId, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var streamUrl = ConstructUrl($"stream/channel/{channelId}?profile={config.StreamingProfile}", "url");
        _logger.LogInformation("Generated stream URL {Url} for channel ID {ChannelId}", MaskSensitiveData(streamUrl), channelId);

        var container = await GetStreamingProfileContainerAsync(config, cancellationToken).ConfigureAwait(false);

        var mediaSource = new MediaSourceInfo
        {
            Id = channelId,
            Path = streamUrl,
            Name = $"LiveTV {channelId}",

            // Always HTTP - TVHeadend's streaming API is HTTP-based.
            Protocol = MediaProtocol.Http,
            Container = container,

            // Always remote - TVHeadend is a network service.
            IsRemote = true,

            // Playback capabilities
            SupportsDirectPlay = config.SupportsDirectPlay,
            SupportsDirectStream = config.SupportsDirectStream,
            SupportsTranscoding = config.SupportsTranscoding,
            IsInfiniteStream = config.IsInfiniteStream,
            IgnoreDts = config.IgnoreDts,
            FallbackMaxStreamingBitrate = config.FallbackMaxStreamingBitrate,

            UseMostCompatibleTranscodingProfile = config.SupportsTranscoding,

            // HTTP streams must be opened/closed on channel switch.
            RequiresOpening = true,
            RequiresClosing = true,

            // Live TV delivers in real-time; throttling causes buffering.
            ReadAtNativeFramerate = false,

            SupportsProbing = config.SupportsProbing,
            AnalyzeDurationMs = config.AnalyzeDurationMs > 0 ? config.AnalyzeDurationMs : 200,
        };

        if (config.BufferMs > 0)
        {
            mediaSource.BufferMs = config.BufferMs;
        }

        // Write a pre-fabricated cache file so Jellyfin's AddMediaInfoWithProbe finds it
        // and skips actual FFmpeg probing - even the very first tune becomes fast.
        if (config.EnableMediaInfoCacheWrite)
        {
            await TryWriteMediaInfoCacheAsync(channelId, streamUrl, container).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Built MediaSourceInfo for channel {ChannelId}: Container={Container}, Probing={Probing}, CacheWrite={CacheWrite}.",
            channelId,
            container,
            config.SupportsProbing,
            config.EnableMediaInfoCacheWrite);

        return mediaSource;
    }

    /// <summary>
    /// Fetches the streaming URL for a specific channel from TVHeadEnd.
    /// Constructs the URL based on the channel ID and returns it as a <see cref="MediaSourceInfo"/> object.
    /// </summary>
    /// <param name="channelId">The unique identifier of the channel to stream.</param>
    /// <param name="streamId">The stream identifier, not used in this implementation but required by the interface.</param>
    /// <param name="cancellationToken">A token to cancel the operation if necessary.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a <see cref="MediaSourceInfo"/> object with the streaming information.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="channelId"/> is null or empty.</exception>
    public async Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(channelId))
            {
                throw new ArgumentException("Channel ID cannot be null or empty.", nameof(channelId));
            }

            var config = Plugin.Instance?.Configuration
                ?? throw new InvalidOperationException("Plugin configuration is not available.");

            return await BuildMediaSourceInfoAsync(channelId, config, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while generating stream URL for channel ID {ChannelId}.", channelId);
            throw;
        }
    }

    /// <summary>
    /// Fetches the available media sources for streaming a specific channel from TVHeadEnd.
    /// </summary>
    /// <param name="channelId">The unique identifier of the channel to stream.</param>
    /// <param name="cancellationToken">A token to cancel the operation if necessary.</param>
    /// <returns>A task containing a list of <see cref="MediaSourceInfo"/> objects.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="channelId"/> is null or empty.</exception>
    public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(channelId))
            {
                throw new ArgumentException("Channel ID cannot be null or empty.", nameof(channelId));
            }

            var config = Plugin.Instance?.Configuration
                ?? throw new InvalidOperationException("Plugin configuration is not available.");

            var mediaSource = await BuildMediaSourceInfoAsync(channelId, config, cancellationToken).ConfigureAwait(false);

            var providerTypeFullName = "Jellyfin.LiveTv.LiveTvMediaSourceProvider";
            var itemTypeName = "LiveTvChannel";
            var internalChannelId = GetInternalChannelId(Name, channelId);
            var itemIdN = internalChannelId.ToString("N", CultureInfo.InvariantCulture);
            var sourceIdFromMediaSource = mediaSource.Id ?? string.Empty;
            var cacheFile = BuildMediainfoCacheFileName(providerTypeFullName, itemTypeName, itemIdN, sourceIdFromMediaSource);

            _logger.LogInformation(
                "GetInternalChannelId resolved external channel id {ExternalChannelId} to internal id {InternalChannelId}.",
                channelId,
                itemIdN);

            _logger.LogInformation(
                "Mediainfo cache key on media-source request: Provider={Provider}, ItemType={ItemType}, ItemId={ItemId}, SourceIdFromMediaSource={SourceIdFromMediaSource}, CacheFile={CacheFile}",
                providerTypeFullName,
                itemTypeName,
                itemIdN,
                sourceIdFromMediaSource,
                cacheFile);

            return new List<MediaSourceInfo> { mediaSource };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while generating media sources for channel ID {ChannelId}.", channelId);
            throw;
        }
    }

    /// <summary>
    /// Logs a message indicating that closing a live stream is not supported by TVHeadEnd.
    /// </summary>
    /// <param name="id">The unique identifier of the live stream to be closed (not used).</param>
    /// <param name="cancellationToken">A token to cancel the operation if necessary.</param>
    /// <returns>
    /// A completed task indicating that no action was taken.
    /// </returns>
    public Task CloseLiveStream(string id, CancellationToken cancellationToken)
    {
        // Log the lack of functionality for closing a live stream
        if (string.IsNullOrWhiteSpace(id))
        {
            _logger.LogWarning("Stream ID is null or empty. No action required.");
        }
        else
        {
            _logger.LogInformation("TVHeadEnd does not support closing live streams directly. No action taken for Stream ID: {StreamId}.", id);
        }

        // Return a completed task
        return Task.CompletedTask;
    }

    /// <summary>
    /// Logs a message indicating that resetting a tuner is not supported or necessary for TVHeadEnd.
    /// </summary>
    /// <param name="id">The unique identifier of the tuner to be reset (not used).</param>
    /// <param name="cancellationToken">A token to cancel the operation if necessary.</param>
    /// <returns>
    /// A completed task indicating that no action was taken.
    /// </returns>
    public Task ResetTuner(string id, CancellationToken cancellationToken)
    {
        // Log that the tuner reset is not supported
        if (string.IsNullOrWhiteSpace(id))
        {
            _logger.LogWarning("Tuner ID is null or empty. No reset action required.");
        }
        else
        {
            _logger.LogInformation("TVHeadEnd does not require or support resetting tuners. No action taken for Tuner ID: {TunerId}.", id);
        }

        // Return a completed task
        return Task.CompletedTask;
    }

    /// <summary>
    /// Fetches the list of content types from the TVHeadend API.
    /// This method retrieves the available content categories used for EPG data.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation if necessary.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains a dictionary
    /// where the key is the content type ID and the value is the content type description.
    /// </returns>
    public async Task<Dictionary<int, string>> GetContentTypesAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Define the relative path for the API endpoint
            var url = ConstructUrl("api/epg/content_type/list");

            // Log the request for debugging purposes
            _logger.LogInformation("Fetching content types from TVHeadEnd at {Url}.", MaskSensitiveData(url));

            // Send the GET request to the TVHeadEnd API
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            // Ensure the response indicates success (HTTP status code 200-299)
            if (!response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogError("Failed to fetch content types. HTTP Status: {StatusCode}. Response: {Response}.", response.StatusCode, responseContent);
                return new Dictionary<int, string>();
            }

            // Deserialize the JSON response directly from the response stream
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync<TvhApiEpgContentTypeListResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);

            // Convert the list to a dictionary
            var contentTypes = result?.Entries?.ToDictionary(entry => entry.Key, entry => entry.Val) ?? new Dictionary<int, string>();

            // Log the number of content types retrieved
            _logger.LogInformation("Successfully retrieved {Count} content types from TVHeadEnd.", contentTypes.Count);

            return contentTypes;
        }
        catch (Exception ex)
        {
            // Log any errors that occur during the process
            _logger.LogError(ex, "Error occurred while fetching content types from TVHeadend.");
            return new Dictionary<int, string>();
        }
    }

    /// <summary>
    /// Fetches the list of channel tags from the TVHeadEnd API.
    /// This method retrieves all channel tags and provides a mapping of tag IDs to their descriptions.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation if necessary.</param>
    /// <returns>
    /// A dictionary where the key is the channel tag ID and the value is the channel tag description.
    /// </returns>
    public async Task<Dictionary<string, string>> GetChannelTagsAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Define the relative path for the API endpoint
            var url = ConstructUrl("api/channeltag/list");

            // Log the request for debugging purposes
            _logger.LogInformation("Fetching channel tags from TVHeadEnd at {Url}.", MaskSensitiveData(url));

            // Send the GET request to the TVHeadEnd API
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            // Ensure the response indicates success (HTTP status code 200-299)
            if (!response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogError("Failed to fetch channel tags. HTTP Status: {StatusCode}. Response: {Response}.", response.StatusCode, responseContent);
                return new Dictionary<string, string>();
            }

            // Deserialize the JSON response directly from the response stream
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync<TvhApiChannelTagResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);

            // Convert the list to a dictionary
            var channelTags = result?.Entries?.ToDictionary(entry => entry.Key, entry => entry.Val) ?? new Dictionary<string, string>();

            // Log the number of channel tags retrieved
            _logger.LogInformation("Successfully retrieved {Count} channel tags from TVHeadEnd.", channelTags.Count);

            return channelTags;
        }
        catch (Exception ex)
        {
            // Log any errors that occur during the process
            _logger.LogError(ex, "Error occurred while fetching channel tags from TVHeadend.");
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// Maps the recording profile name stored in the plugin configuration to its corresponding UUID from the TVHeadend API.
    /// </summary>
    /// <param name="profileName">The name of the recording profile to map.</param>
    /// <param name="cancellationToken">A token to cancel the operation if necessary.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains the UUID of the recording profile if found; otherwise, null.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="profileName"/> is null or empty.</exception>
    public async Task<string> GetRecordingProfileUuidAsync(string profileName, CancellationToken cancellationToken)
    {
        try
        {
            // Validate the profile name parameter
            if (string.IsNullOrWhiteSpace(profileName))
            {
                throw new ArgumentException("Recording Profile name cannot be null or empty.", nameof(profileName));
            }

            // Define the API endpoint for fetching recording profiles
            var url = ConstructUrl("api/dvr/config/grid");

            // Log the request to fetch recording profiles
            _logger.LogInformation("Fetching recording profiles from TVHeadEnd at {Url}...", MaskSensitiveData(url));

            // Fetch and deserialize the recording profiles directly from the response stream
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync<TvhApiDvrConfigGridResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);

            // Check if profiles are returned
            if (result?.Entries == null || !result.Entries.Any())
            {
                throw new InvalidOperationException("No recording profiles retrieved from TVHeadend.");
            }

            // Find the profile matching the provided name
            var matchingProfile = result.Entries.FirstOrDefault(profile =>
                string.Equals(profile.Name, profileName, StringComparison.OrdinalIgnoreCase));

            if (matchingProfile?.Uuid == null)
            {
                throw new InvalidOperationException($"No matching recording profile found for '{profileName}' in TVHeadend.");
            }

            // Log the successful mapping
            _logger.LogInformation("Recording profile '{ProfileName}' mapped to UUID '{ProfileUuid}'.", profileName, matchingProfile.Uuid);

            return matchingProfile.Uuid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while mapping recording profile.");
            throw; // Re-throw the exception to notify the caller
        }
    }

    // -- Streaming profile container detection ----------------------------

    /// <summary>
    /// Maps TVHeadend container values (numeric enum or string) to FFmpeg container names.
    /// </summary>
    private static string MapContainer(string raw)
    {
        return raw.ToLowerInvariant() switch
        {
            "0" or "" or "not set" => string.Empty,
            "1" or "matroska" or "mkv" => "matroska",
            "2" or "mpegts" or "ts" => "mpegts",
            "3" or "mpegps" or "ps" => "mpegps",
            "9" => "mp4",
            "4" or "mp4" => "mp4",
            _ => raw.ToLowerInvariant()
        };
    }

    /// <summary>
    /// Derives the container format from a TVHeadend profile class name for non-transcode profiles.
    /// </summary>
    private static string MapProfileClassToContainer(string profileClass)
    {
        return profileClass.ToLowerInvariant() switch
        {
            "profile-matroska" => "matroska",
            "profile-mpegts" or "profile-mpegts-pass" or "profile-mpegts-spawn" => "mpegts",
            "profile-htsp" => "mpegts",
            _ => "mpegts" // safe default for pass-through / unknown profiles
        };
    }

    /// <summary>
    /// Queries TVHeadend for the configured streaming profile and returns its output container format.
    /// The result is cached for <see cref="ProfileContainerCacheTtl"/> and invalidated when the
    /// configured profile name changes.
    /// </summary>
    private async Task<string> GetStreamingProfileContainerAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        var profileName = config.StreamingProfile;

        // Fast path: return cached result if still valid
        if (_profileContainerCache is { } cached
            && string.Equals(cached.ProfileName, profileName, StringComparison.OrdinalIgnoreCase)
            && DateTime.UtcNow - cached.Timestamp < ProfileContainerCacheTtl)
        {
            return cached.Container;
        }

        await _profileContainerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring lock
            if (_profileContainerCache is { } cached2
                && string.Equals(cached2.ProfileName, profileName, StringComparison.OrdinalIgnoreCase)
                && DateTime.UtcNow - cached2.Timestamp < ProfileContainerCacheTtl)
            {
                return cached2.Container;
            }

            var container = await DetectProfileContainerAsync(profileName, cancellationToken).ConfigureAwait(false);
            _profileContainerCache = new ContainerCacheEntry(DateTime.UtcNow, profileName, container);
            return container;
        }
        finally
        {
            _profileContainerLock.Release();
        }
    }

    /// <summary>
    /// Performs TVHeadend API calls to detect the output container format for a streaming profile.
    /// For transcode profiles the container is read from the profile's <c>container</c> property.
    /// For pass-through profiles the container is derived from the profile class name.
    /// </summary>
    private async Task<string> DetectProfileContainerAsync(string profileName, CancellationToken cancellationToken)
    {
        const string fallbackContainer = "mpegts";

        if (string.IsNullOrWhiteSpace(profileName))
        {
            _logger.LogWarning("No streaming profile configured. Defaulting container to '{Container}'.", fallbackContainer);
            return fallbackContainer;
        }

        try
        {
            // Step 1: Find the profile UUID via api/profile/list
            var listUrl = ConstructUrl("api/profile/list");
            using var listResponse = await _httpClient.GetAsync(
                listUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            listResponse.EnsureSuccessStatusCode();
            var listBody = await listResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            string? profileUuid = null;
            using (var listDoc = JsonDocument.Parse(listBody))
            {
                if (listDoc.RootElement.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in entries.EnumerateArray())
                    {
                        var val = GetJsonStringProp(entry, "val");
                        if (string.Equals(val, profileName, StringComparison.OrdinalIgnoreCase))
                        {
                            profileUuid = GetJsonStringProp(entry, "key");
                            break;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(profileUuid))
            {
                _logger.LogWarning(
                    "Streaming profile '{ProfileName}' not found in TVHeadend. Defaulting container to '{Container}'.",
                    profileName,
                    fallbackContainer);
                return fallbackContainer;
            }

            // Step 2: Load the profile details via api/idnode/load
            var loadUrl = ConstructUrl($"api/idnode/load?uuid={Uri.EscapeDataString(profileUuid)}");
            using var loadResponse = await _httpClient.GetAsync(
                loadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            loadResponse.EnsureSuccessStatusCode();
            var loadBody = await loadResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(loadBody);
            if (!doc.RootElement.TryGetProperty("entries", out var profileEntries) || profileEntries.GetArrayLength() == 0)
            {
                _logger.LogWarning(
                    "TVHeadend returned empty data for profile UUID '{Uuid}'. Defaulting container to '{Container}'.",
                    profileUuid,
                    fallbackContainer);
                return fallbackContainer;
            }

            var profileEntry = profileEntries[0];
            var profileClass = GetJsonStringPropOrParam(profileEntry, "class") ?? string.Empty;

            // Transcode profiles: read container from the profile
            if (profileClass.Contains("transcode", StringComparison.OrdinalIgnoreCase))
            {
                var rawContainer = GetJsonStringPropOrParam(profileEntry, "container")
                    ?? GetJsonIntPropOrParam(profileEntry, "container")?.ToString(CultureInfo.InvariantCulture)
                    ?? string.Empty;

                var mappedContainer = MapContainer(rawContainer);
                if (string.IsNullOrWhiteSpace(mappedContainer))
                {
                    _logger.LogWarning(
                        "Transcode profile '{ProfileName}' has no container set (raw='{Raw}'). Defaulting to '{Container}'.",
                        profileName,
                        rawContainer,
                        fallbackContainer);
                    return fallbackContainer;
                }

                _logger.LogInformation(
                    "Streaming profile '{ProfileName}' (class={ProfileClass}) uses container '{Container}'.",
                    profileName,
                    profileClass,
                    mappedContainer);
                return mappedContainer;
            }

            // Non-transcode profiles: derive from profile class
            var classContainer = MapProfileClassToContainer(profileClass);
            _logger.LogInformation(
                "Streaming profile '{ProfileName}' (class={ProfileClass}) ? container '{Container}' (derived from class).",
                profileName,
                profileClass,
                classContainer);
            return classContainer;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to detect container for streaming profile '{ProfileName}'. Defaulting to '{Container}'.",
                profileName,
                fallbackContainer);
            return fallbackContainer;
        }
    }

    // -- Mediainfo cache file writing --------------------------------------

    /// <summary>
    /// Pre-creates a Jellyfin mediainfo cache file for a channel if none exists yet.
    /// The file contains H264+AAC stream metadata matching the output of TVHeadend's
    /// "jellyfin" transcode profile, so that Jellyfin's <c>AddMediaInfoWithProbe</c> finds
    /// the cache on the very first tune and skips actual FFmpeg probing.
    /// </summary>
    private async Task TryWriteMediaInfoCacheAsync(string channelId, string streamUrl, string container)
    {
        try
        {
            if (!string.Equals(container, "mp4", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug(
                    "Skipping mediainfo cache write for channel {ChannelId}: container '{Container}' is not compatible with the MP4 cache template.",
                    channelId,
                    container);
                return;
            }

            var cachePath = Plugin.Instance?.CachePath;
            if (string.IsNullOrWhiteSpace(cachePath))
            {
                return;
            }

            var providerTypeFullName = "Jellyfin.LiveTv.LiveTvMediaSourceProvider";
            var itemTypeName = "LiveTvChannel";
            var internalChannelId = GetInternalChannelId(Name, channelId);
            var itemIdN = internalChannelId.ToString("N", CultureInfo.InvariantCulture);
            var cacheFileName = BuildMediainfoCacheFileName(providerTypeFullName, itemTypeName, itemIdN, channelId);
            var mediaInfoDir = Path.Combine(cachePath, "mediainfo");
            var cacheFilePath = Path.Combine(mediaInfoDir, cacheFileName);

            if (File.Exists(cacheFilePath))
            {
                _logger.LogDebug(
                    "Mediainfo cache file already exists for channel {ChannelId}: {Path}.",
                    channelId,
                    cacheFilePath);
                return;
            }

            if (!Directory.Exists(mediaInfoDir))
            {
                Directory.CreateDirectory(mediaInfoDir);
            }

            // Build cache content matching Jellyfin's probe cache format.
            // The structure mirrors what FFprobe would produce for H264+AAC MP4 output.
            var cacheContent = new Dictionary<string, object?>
            {
                ["Chapters"] = Array.Empty<object>(),
                ["Artists"] = Array.Empty<object>(),
                ["AlbumArtists"] = Array.Empty<object>(),
                ["Studios"] = Array.Empty<object>(),
                ["Genres"] = Array.Empty<object>(),
                ["People"] = Array.Empty<object>(),
                ["ProviderIds"] = new Dictionary<string, string>(),
                ["Protocol"] = "Http",
                ["Path"] = streamUrl,
                ["Type"] = "Default",
                ["Container"] = "mov,mp4,m4a,3gp,3g2,mj2",
                ["IsRemote"] = true,
                ["RunTimeTicks"] = 0,
                ["ReadAtNativeFramerate"] = false,
                ["IgnoreDts"] = false,
                ["IgnoreIndex"] = false,
                ["GenPtsInput"] = false,
                ["SupportsTranscoding"] = true,
                ["SupportsDirectStream"] = true,
                ["SupportsDirectPlay"] = true,
                ["IsInfiniteStream"] = false,
                ["UseMostCompatibleTranscodingProfile"] = false,
                ["RequiresOpening"] = false,
                ["RequiresClosing"] = false,
                ["RequiresLooping"] = false,
                ["SupportsProbing"] = true,
                ["MediaStreams"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["Codec"] = "h264",
                        ["CodecTag"] = "avc1",
                        ["Language"] = "und",
                        ["TimeBase"] = "1/16384",
                        ["VideoRange"] = "SDR",
                        ["VideoRangeType"] = "SDR",
                        ["AudioSpatialFormat"] = "None",
                        ["DisplayTitle"] = "720p H264 SDR",
                        ["NalLengthSize"] = "4",
                        ["IsInterlaced"] = false,
                        ["IsAVC"] = true,
                        ["BitDepth"] = 8,
                        ["RefFrames"] = 1,
                        ["IsDefault"] = true,
                        ["IsForced"] = false,
                        ["IsHearingImpaired"] = false,
                        ["Height"] = 720,
                        ["Width"] = 1280,
                        ["AverageFrameRate"] = 25.0,
                        ["RealFrameRate"] = 25.0,
                        ["ReferenceFrameRate"] = 25.0,
                        ["Profile"] = "Main",
                        ["Type"] = "Video",
                        ["AspectRatio"] = "16:9",
                        ["Index"] = 0,
                        ["IsExternal"] = false,
                        ["IsTextSubtitleStream"] = false,
                        ["SupportsExternalStream"] = false,
                        ["PixelFormat"] = "yuv420p",
                        ["Level"] = 30,
                        ["IsAnamorphic"] = false,
                    },
                    new Dictionary<string, object?>
                    {
                        ["Codec"] = "aac",
                        ["CodecTag"] = "mp4a",
                        ["Language"] = "ger",
                        ["TimeBase"] = "1/48000",
                        ["VideoRange"] = "Unknown",
                        ["VideoRangeType"] = "Unknown",
                        ["AudioSpatialFormat"] = "None",
                        ["DisplayTitle"] = "AAC - Stereo",
                        ["IsInterlaced"] = false,
                        ["IsAVC"] = false,
                        ["ChannelLayout"] = "stereo",
                        ["BitRate"] = 128000,
                        ["Channels"] = 2,
                        ["SampleRate"] = 48000,
                        ["IsDefault"] = true,
                        ["IsForced"] = false,
                        ["IsHearingImpaired"] = false,
                        ["Profile"] = "LC",
                        ["Type"] = "Audio",
                        ["Index"] = 1,
                        ["IsExternal"] = false,
                        ["IsTextSubtitleStream"] = false,
                        ["SupportsExternalStream"] = false,
                        ["Level"] = 0,
                    },
                },
                ["MediaAttachments"] = Array.Empty<object>(),
                ["Formats"] = Array.Empty<string>(),
                ["Bitrate"] = 128000,
                ["RequiredHttpHeaders"] = new Dictionary<string, string>(),
                ["TranscodingSubProtocol"] = "http",
                ["HasSegments"] = false,
            };

            var json = JsonSerializer.Serialize(cacheContent);
            await File.WriteAllTextAsync(cacheFilePath, json).ConfigureAwait(false);

            _logger.LogInformation(
                "Created mediainfo cache file for channel {ChannelId} at {Path}.",
                channelId,
                cacheFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write mediainfo cache file for channel {ChannelId}.", channelId);
        }
    }

    /// <summary>
    /// Constructs a complete URL for a TVHeadEnd image endpoint (e.g., imagecache/1715).
    /// This is used by the image proxy to fetch images and return them without exposing credentials.
    /// </summary>
    /// <param name="imagePath">The relative image path from TVHeadend (e.g., "imagecache/1715" or just the image ID).</param>
    /// <returns>The full URL with embedded authentication if needed.</returns>
    public string ConstructImageUrl(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            throw new ArgumentException("Image path cannot be null or empty.", nameof(imagePath));
        }

        // Remove leading slash if present
        var cleanPath = imagePath.TrimStart('/');

        // Use ConstructUrl with "url" auth method to embed credentials directly
        return ConstructUrl(cleanPath, "url");
    }

    /// <summary>
    /// Fetches raw image data from TVHeadend using the provided URL.
    /// This method is used by the image proxy endpoint to stream images to clients.
    /// </summary>
    /// <param name="imageUrl">The full TVHeadend image URL with embedded authentication.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An HTTP response message with the image data.</returns>
    public async Task<HttpResponseMessage> FetchImageAsync(string imageUrl, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin configuration is not available.");

        EnsureHttpClientConfigured(config);

        _logger.LogDebug("Fetching image from TVHeadend: {Url}", MaskSensitiveData(imageUrl));
        return await _httpClient.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Cached container string extracted from a TVHeadend streaming profile.
    /// </summary>
    private sealed record ContainerCacheEntry(DateTime Timestamp, string ProfileName, string Container);
}
