using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Model;
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
    private readonly ILogger<LiveTvService> _logger;
    private readonly object _httpClientSync = new();
    private HttpClient _httpClient;
    private string? _httpClientConfigurationKey;
    private bool _disposed;
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Time-to-live for cached profile details. After this period the plugin re-queries TVHeadend.
    /// </summary>
    private static readonly TimeSpan ProfileDetailsCacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Lock to prevent concurrent profile container lookups.
    /// </summary>
    private readonly SemaphoreSlim _profileContainerLock = new(1, 1);

    /// <summary>
    /// Cached profile details (container, codecs, bitrates) derived from the configured
    /// TVHeadend streaming profile. Invalidated when the profile name changes or when the TTL expires.
    /// </summary>
    private ProfileCacheEntry? _profileDetailsCache;

    /// <summary>
    /// JSON options for deserializing Jellyfin's mediainfo cache files.
    /// Uses <see cref="JsonStringEnumConverter"/> because Jellyfin serialises enums as strings
    /// (e.g. <c>"Type": "Video"</c> for <see cref="MediaStreamType"/>).
    /// </summary>
    private static readonly JsonSerializerOptions MediaInfoCacheJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Lazy index mapping channel UUID → mediainfo cache file path.
    /// Built by scanning the <c>Path</c> property inside each cache file for a
    /// TVHeadend <c>stream/channel/{uuid}</c> pattern. Rebuilt on cache miss.
    /// </summary>
    private Dictionary<string, string>? _probeCacheIndex;

    /// <summary>
    /// Regex to extract the 32-character hex channel UUID from a TVHeadend stream URL
    /// stored in the <c>Path</c> property of Jellyfin's mediainfo cache files.
    /// </summary>
    private static readonly Regex ChannelIdFromPathRegex = new(
        @"stream/channel/([0-9a-f]{32})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
    /// <exception cref="ArgumentNullException">
    /// Thrown if the <paramref name="logger"/> parameter is null, as logging is critical for service operation.
    /// </exception>
    public LiveTvService(ILogger<LiveTvService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
                        ? ConstructUrl(channel.IconPublicUrl.TrimStart('/'), "parameter")
                        : null, // URL to the channel's icon image
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
            var response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

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
            var response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

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
            var response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

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
            var response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

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
            var response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

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
            var response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);

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
                    // Determine the image URL
                    string? imageUrl = null;
                    if (!string.IsNullOrWhiteSpace(entry.Image))
                    {
                        imageUrl = entry.Image.StartsWith("imagecache/", StringComparison.Ordinal)
                            ? ConstructUrl(entry.Image, "parameter")
                            : entry.Image;
                    }
                    else if (!string.IsNullOrWhiteSpace(entry.ChannelIcon))
                    {
                        imageUrl = ConstructUrl(entry.ChannelIcon, "parameter");
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

    // ═══════════════════════════════════════════════════════════════════════
    //  Jellyfin mediainfo probe cache – index by channel UUID
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Scans Jellyfin's mediainfo cache directory and builds a channel UUID → file path
    /// index by reading the <c>Path</c> property from each cache file.
    /// </summary>
    private Dictionary<string, string> BuildProbeCacheIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var cachePath = Plugin.Instance?.CachePath;
        if (string.IsNullOrWhiteSpace(cachePath))
        {
            return index;
        }

        var mediaInfoDir = Path.Combine(cachePath, "mediainfo");
        if (!Directory.Exists(mediaInfoDir))
        {
            _logger.LogDebug("Mediainfo cache directory {Dir} does not exist.", mediaInfoDir);
            return index;
        }

        var files = Directory.GetFiles(mediaInfoDir, "*.json");
        foreach (var file in files)
        {
            try
            {
                // Read only enough to extract the Path property (first few hundred bytes suffice).
                var json = File.ReadAllText(file);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("Path", out var pathEl))
                {
                    continue;
                }

                var path = pathEl.GetString();
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var match = ChannelIdFromPathRegex.Match(path);
                if (!match.Success)
                {
                    continue;
                }

                var channelId = match.Groups[1].Value;
                // Always prefer the newest file (last one wins when iterating alphabetically).
                index[channelId] = file;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping unreadable mediainfo cache file: {File}.", file);
            }
        }

        _logger.LogInformation(
            "Built mediainfo probe cache index: {Indexed}/{Total} files matched a channel UUID.",
            index.Count,
            files.Length);

        return index;
    }

    /// <summary>
    /// Tries to load probe-quality stream data from Jellyfin's mediainfo cache for the given
    /// channel. Looks up the file via an in-memory index of channel UUID → file path.
    /// On cache miss the index is rebuilt (new files may have appeared after a first probe).
    /// Returns <c>null</c> when no cache entry exists for this channel.
    /// </summary>
    private async Task<CachedProbeResult?> TryLoadMediaInfoCacheAsync(string channelId)
    {
        // Build index lazily on first access.
        _probeCacheIndex ??= BuildProbeCacheIndex();

        // If not found, rebuild the index once (a new cache file may have been written).
        if (!_probeCacheIndex.TryGetValue(channelId, out var cacheFilePath))
        {
            _probeCacheIndex = BuildProbeCacheIndex();
            if (!_probeCacheIndex.TryGetValue(channelId, out cacheFilePath))
            {
                _logger.LogDebug("No mediainfo cache file found for channel {ChannelId}.", channelId);
                return null;
            }
        }

        // Verify the file still exists (could have been deleted).
        if (!File.Exists(cacheFilePath))
        {
            _logger.LogDebug("Mediainfo cache file {Path} no longer exists for channel {ChannelId}.", cacheFilePath, channelId);
            _probeCacheIndex.Remove(channelId);
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(cacheFilePath).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Deserialise MediaStreams array.
            if (!root.TryGetProperty("MediaStreams", out var streamsEl))
            {
                _logger.LogDebug("Mediainfo cache file {Path} has no MediaStreams property.", cacheFilePath);
                return null;
            }

            var mediaStreams = JsonSerializer.Deserialize<List<MediaStream>>(
                streamsEl.GetRawText(),
                MediaInfoCacheJsonOptions);

            if (mediaStreams == null || mediaStreams.Count == 0)
            {
                _logger.LogDebug("Mediainfo cache file {Path} contains no streams.", cacheFilePath);
                return null;
            }

            // Ensure there is at least one video or audio stream.
            bool hasUsefulStreams = mediaStreams.Any(
                s => s.Type == MediaStreamType.Video || s.Type == MediaStreamType.Audio);
            if (!hasUsefulStreams)
            {
                _logger.LogDebug("Mediainfo cache file {Path} has no video/audio streams.", cacheFilePath);
                return null;
            }

            // Extract optional container and bitrate.
            string? container = null;
            if (root.TryGetProperty("Container", out var containerEl))
            {
                container = containerEl.GetString();
            }

            int bitrate = 0;
            if (root.TryGetProperty("Bitrate", out var bitrateEl) && bitrateEl.TryGetInt32(out var br))
            {
                bitrate = br;
            }

            _logger.LogInformation(
                "Loaded Jellyfin mediainfo cache for channel {ChannelId} from {Path}: " +
                "{VideoCount} video, {AudioCount} audio, {SubCount} subtitle streams, container={Container}, bitrate={Bitrate} bps.",
                channelId,
                cacheFilePath,
                mediaStreams.Count(s => s.Type == MediaStreamType.Video),
                mediaStreams.Count(s => s.Type == MediaStreamType.Audio),
                mediaStreams.Count(s => s.Type == MediaStreamType.Subtitle),
                container ?? "(unknown)",
                bitrate);

            return new CachedProbeResult(mediaStreams, container, bitrate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read mediainfo cache file {Path} for channel {ChannelId}.", cacheFilePath, channelId);
            return null;
        }
    }

    /// <summary>
    /// Builds a <see cref="MediaSourceInfo"/> for a given channel.
    /// <para>
    /// The method reads Jellyfin's on-disk mediainfo cache to obtain probe-quality stream data
    /// (codecs, resolution, bitrate, audio details) so that Jellyfin can start playback without
    /// re-probing the stream.
    /// </para>
    /// <para>
    /// When no cache entry exists (e.g. first tune) or the cache is empty, the method falls back
    /// to enabling probing so Jellyfin can discover the format on its own and populate the cache
    /// for subsequent tunes.
    /// </para>
    /// </summary>
    private async Task<MediaSourceInfo> BuildMediaSourceInfoAsync(string channelId, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var streamUrl = ConstructUrl($"stream/channel/{channelId}?profile={config.StreamingProfile}", "url");
        _logger.LogInformation("Generated stream URL {Url} for channel ID {ChannelId}", MaskSensitiveData(streamUrl), channelId);

        // ── Look up Jellyfin's mediainfo cache for this channel ──────────
        CachedProbeResult? probeResult = null;
        if (config.EnableMediaInfoCache)
        {
            probeResult = await TryLoadMediaInfoCacheAsync(channelId).ConfigureAwait(false);
        }
        else
        {
            _logger.LogDebug("Mediainfo cache lookup disabled by configuration for channel {ChannelId}.", channelId);
        }

        var hasProbeCache = probeResult != null;

        // ── Container ───────────────────────────────────────────────────
        // Prefer container from probe cache; fall back to TVH profile detection.
        string container;
        if (hasProbeCache && !string.IsNullOrWhiteSpace(probeResult!.Container))
        {
            container = probeResult.Container;
        }
        else
        {
            container = await GetStreamingProfileContainerAsync(config, cancellationToken).ConfigureAwait(false);
        }

        var mediaSource = new MediaSourceInfo
        {
            Id = channelId,
            Path = streamUrl,
            Name = $"LiveTV {channelId}",

            // Always HTTP – TVHeadend's streaming API is HTTP-based.
            Protocol = MediaProtocol.Http,
            Container = container,

            // Always remote – TVHeadend is a network service.
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
        };

        if (config.BufferMs > 0)
        {
            mediaSource.BufferMs = config.BufferMs;
        }

        // ── Use cached probe data from Jellyfin mediainfo cache ──────────
        // When we have probe-quality data we provide the MediaStreams so Jellyfin
        // can make informed playback decisions (explicit -map flags in FFmpeg).
        // Whether probing is also disabled depends on the SupportsProbing config:
        //   SupportsProbing=false → fast path, Jellyfin calls AddMediaInfo (instant)
        //   SupportsProbing=true  → Jellyfin calls AddMediaInfoWithProbe but our
        //                           MediaStreams are already populated so it hits
        //                           the "has valid indices" fast path anyway.
        if (hasProbeCache && probeResult!.MediaStreams.Count > 0)
        {
            mediaSource.SupportsProbing = config.SupportsProbing;
            mediaSource.AnalyzeDurationMs = config.AnalyzeDurationMs > 0 ? config.AnalyzeDurationMs : 200;
            mediaSource.MediaStreams = probeResult.MediaStreams;

            if (probeResult.Bitrate > 0)
            {
                mediaSource.Bitrate = probeResult.Bitrate;
            }

            _logger.LogInformation(
                "Built MediaSourceInfo for channel {ChannelId} from Jellyfin probe cache: Container={Container}, " +
                "{VideoCount} video, {AudioCount} audio, {SubCount} subtitle streams, " +
                "bitrate={Bitrate} bps. Probing={Probing}.",
                channelId,
                container,
                probeResult.MediaStreams.Count(s => s.Type == MediaStreamType.Video),
                probeResult.MediaStreams.Count(s => s.Type == MediaStreamType.Audio),
                probeResult.MediaStreams.Count(s => s.Type == MediaStreamType.Subtitle),
                probeResult.Bitrate,
                config.SupportsProbing);
        }
        else
        {
            // No probe cache entry available for this channel.
            // Try profile-based fallback: if the streaming profile is a transcode profile,
            // we know the output codecs and can build synthetic MediaStreams so Jellyfin
            // can make better playback decisions without probing.
            var profileDetails = await GetProfileDetailsAsync(config, cancellationToken).ConfigureAwait(false);
            if (profileDetails.IsTranscodeProfile
                && (!string.IsNullOrWhiteSpace(profileDetails.VideoCodec) || !string.IsNullOrWhiteSpace(profileDetails.AudioCodec)))
            {
                mediaSource.SupportsProbing = false;
                mediaSource.AnalyzeDurationMs = config.AnalyzeDurationMs > 0 ? config.AnalyzeDurationMs : 200;

                var syntheticStreams = new List<MediaStream>();
                int syntheticIndex = 0;
                int syntheticBitrate = 0;

                // Synthetic video stream from profile
                if (!string.IsNullOrWhiteSpace(profileDetails.VideoCodec))
                {
                    var videoBitrateEstimate = profileDetails.VideoBitrateKbps > 0
                        ? profileDetails.VideoBitrateKbps * 1000
                        : config.FallbackMaxStreamingBitrate;

                    var vs = new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        Index = syntheticIndex++,
                        Codec = profileDetails.VideoCodec,
                        IsDefault = true,
                        BitRate = videoBitrateEstimate,
                        // Mark as interlaced only if we know the profile does NOT deinterlace
                        // (conservative default: assume deinterlaced output from transcode)
                        IsInterlaced = false,
                    };

                    if (profileDetails.VideoHeight > 0)
                    {
                        vs.Height = profileDetails.VideoHeight;
                        vs.Width = profileDetails.VideoHeight * 16 / 9; // assume 16:9
                    }

                    // Codec profile hints for common codecs
                    switch (profileDetails.VideoCodec)
                    {
                        case "h264":
                            vs.Profile = "High";
                            vs.Level = 41;
                            vs.PixelFormat = "yuv420p";
                            vs.BitDepth = 8;
                            break;
                        case "hevc":
                            vs.Profile = "Main";
                            vs.Level = 120;
                            vs.PixelFormat = "yuv420p";
                            vs.BitDepth = 8;
                            break;
                    }

                    syntheticBitrate += videoBitrateEstimate;
                    syntheticStreams.Add(vs);
                }

                // Synthetic audio stream from profile
                if (!string.IsNullOrWhiteSpace(profileDetails.AudioCodec))
                {
                    var audioBitrateEstimate = profileDetails.AudioBitrateKbps > 0
                        ? profileDetails.AudioBitrateKbps * 1000
                        : profileDetails.AudioCodec switch
                        {
                            "ac3" => 384000,
                            "eac3" => 640000,
                            "aac" => 128000,
                            _ => 128000
                        };

                    var audioChannelCount = profileDetails.AudioChannels > 0
                        ? profileDetails.AudioChannels
                        : profileDetails.AudioCodec is "ac3" or "eac3" ? 6 : 2;

                    var audioStream = new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        Index = syntheticIndex++,
                        Codec = profileDetails.AudioCodec,
                        IsDefault = true,
                        BitRate = audioBitrateEstimate,
                        Channels = audioChannelCount,
                        SampleRate = 48000,
                        ChannelLayout = audioChannelCount switch
                        {
                            1 => "mono",
                            2 => "stereo",
                            6 => "5.1",
                            8 => "7.1",
                            _ => $"{audioChannelCount}.0"
                        },
                    };

                    if (profileDetails.AudioCodec == "aac")
                    {
                        audioStream.Profile = "LC";
                    }

                    syntheticBitrate += audioBitrateEstimate;
                    syntheticStreams.Add(audioStream);
                }

                mediaSource.MediaStreams = syntheticStreams;
                if (syntheticBitrate > 0)
                {
                    mediaSource.Bitrate = syntheticBitrate;
                }

                _logger.LogInformation(
                    "No probe cache entry for channel {ChannelId}. " +
                    "Built synthetic MediaStreams from transcode profile '{ProfileName}': " +
                    "video={VideoCodec}, audio={AudioCodec}, bitrate≈{Bitrate} bps. Probing disabled.",
                    channelId,
                    profileDetails.ProfileName,
                    profileDetails.VideoCodec,
                    profileDetails.AudioCodec,
                    syntheticBitrate);
            }
            else
            {
                // No transcode profile or no codec info – respect user's probing preference.
                // NOTE: When SupportsProbing is true, Jellyfin's MediaSourceManager.AddMediaInfoWithProbe
                // will override AnalyzeDurationMs to 3000 ms for live streams and wait at least 3 s
                // before probing.  The value we set here therefore only takes real effect when
                // SupportsProbing is false (in which case Jellyfin does not call AddMediaInfoWithProbe).
                // See: Emby.Server.Implementations/Library/MediaSourceManager.cs → AddMediaInfoWithProbe
                mediaSource.SupportsProbing = config.SupportsProbing;

                if (config.SupportsProbing)
                {
                    // Probing enabled: set AnalyzeDurationMs if explicitly configured,
                    // otherwise let Jellyfin use its own default.
                    if (config.AnalyzeDurationMs > 0)
                    {
                        mediaSource.AnalyzeDurationMs = config.AnalyzeDurationMs;
                    }

                    _logger.LogWarning(
                        "No probe cache entry for channel {ChannelId}. Jellyfin will probe the stream.",
                        channelId);
                }
                else
                {
                    // Probing disabled: always set an explicit AnalyzeDurationMs so that
                    // Jellyfin does not fall back to its 3 000 ms default.
                    mediaSource.AnalyzeDurationMs = config.AnalyzeDurationMs > 0 ? config.AnalyzeDurationMs : 200;

                    _logger.LogInformation(
                        "No probe cache entry for channel {ChannelId}. Probing disabled per config; AnalyzeDuration={Duration}ms.",
                        channelId,
                        mediaSource.AnalyzeDurationMs);
                }
            }
        }

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
            _logger.LogError(ex, "Error occurred while fetching content types from TVHeadEnd.");
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
            _logger.LogError(ex, "Error occurred while fetching channel tags from TVHeadEnd.");
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// Maps the recording profile name stored in the plugin configuration to its corresponding UUID from the TVHeadEnd API.
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

    // ── Streaming profile container detection ────────────────────────────

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
    /// <para>
    /// For transcode profiles the container is read from the profile's <c>container</c> property.
    /// For pass-through profiles the container is derived from the profile class name.
    /// </para>
    /// The result is cached for <see cref="ProfileDetailsCacheTtl"/> and invalidated when the
    /// configured profile name changes.
    /// </summary>
    private async Task<string> GetStreamingProfileContainerAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        var entry = await GetProfileDetailsAsync(config, cancellationToken).ConfigureAwait(false);
        return entry.Container;
    }

    /// <summary>
    /// Returns the full cached profile details (container, codecs, bitrates) for the configured
    /// streaming profile. Performs a TVHeadend API call on first access or when the cache expires.
    /// </summary>
    private async Task<ProfileCacheEntry> GetProfileDetailsAsync(PluginConfiguration config, CancellationToken cancellationToken)
    {
        var profileName = config.StreamingProfile;

        // Fast path: return cached result if still valid
        if (_profileDetailsCache is { } cached
            && string.Equals(cached.ProfileName, profileName, StringComparison.OrdinalIgnoreCase)
            && DateTime.UtcNow - cached.Timestamp < ProfileDetailsCacheTtl)
        {
            return cached;
        }

        await _profileContainerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring lock
            if (_profileDetailsCache is { } cached2
                && string.Equals(cached2.ProfileName, profileName, StringComparison.OrdinalIgnoreCase)
                && DateTime.UtcNow - cached2.Timestamp < ProfileDetailsCacheTtl)
            {
                return cached2;
            }

            var entry = await DetectProfileDetailsAsync(profileName, cancellationToken).ConfigureAwait(false);
            _profileDetailsCache = entry;
            return entry;
        }
        finally
        {
            _profileContainerLock.Release();
        }
    }

    /// <summary>
    /// Performs the actual TVHeadend API calls to detect container format and codec details
    /// for a streaming profile. Returns a <see cref="ProfileCacheEntry"/> with all available info.
    /// </summary>
    private async Task<ProfileCacheEntry> DetectProfileDetailsAsync(string profileName, CancellationToken cancellationToken)
    {
        const string fallbackContainer = "mpegts";

        if (string.IsNullOrWhiteSpace(profileName))
        {
            _logger.LogWarning("No streaming profile configured. Defaulting container to '{Container}'.", fallbackContainer);
            return new ProfileCacheEntry(DateTime.UtcNow, profileName ?? string.Empty, fallbackContainer);
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
                return new ProfileCacheEntry(DateTime.UtcNow, profileName, fallbackContainer);
            }

            // Step 2: Load the profile details via api/idnode/load
            var loadUrl = ConstructUrl($"api/idnode/load?uuid={Uri.EscapeDataString(profileUuid)}");
            using var loadResponse = await _httpClient.GetAsync(
                loadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            loadResponse.EnsureSuccessStatusCode();
            var loadBody = await loadResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using (var doc = JsonDocument.Parse(loadBody))
            {
                if (!doc.RootElement.TryGetProperty("entries", out var profileEntries) || profileEntries.GetArrayLength() == 0)
                {
                    _logger.LogWarning(
                        "TVHeadend returned empty data for profile UUID '{Uuid}'. Defaulting container to '{Container}'.",
                        profileUuid,
                        fallbackContainer);
                    return new ProfileCacheEntry(DateTime.UtcNow, profileName, fallbackContainer);
                }

                var profileEntry = profileEntries[0];
                var profileClass = GetJsonStringProp(profileEntry, "class") ?? string.Empty;

                // Transcode profiles: read container and codec fields
                if (profileClass.Contains("transcode", StringComparison.OrdinalIgnoreCase))
                {
                    var rawContainer = GetJsonStringProp(profileEntry, "container")
                        ?? GetJsonIntProp(profileEntry, "container")?.ToString(CultureInfo.InvariantCulture)
                        ?? string.Empty;

                    var mappedContainer = MapContainer(rawContainer);
                    if (string.IsNullOrWhiteSpace(mappedContainer))
                    {
                        _logger.LogWarning(
                            "Transcode profile '{ProfileName}' has no container set (raw='{Raw}'). Defaulting to '{Container}'.",
                            profileName,
                            rawContainer,
                            fallbackContainer);
                        mappedContainer = fallbackContainer;
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Streaming profile '{ProfileName}' (class={ProfileClass}) uses container '{Container}'.",
                            profileName,
                            profileClass,
                            mappedContainer);
                    }

                    // Extract codec information from the transcode profile
                    var rawVideoCodec = GetJsonStringProp(profileEntry, "vcodec") ?? string.Empty;
                    var rawAudioCodec = GetJsonStringProp(profileEntry, "acodec") ?? string.Empty;
                    var proVideoCodec = GetJsonStringProp(profileEntry, "pro_vcodec") ?? string.Empty;
                    var proAudioCodec = GetJsonStringProp(profileEntry, "pro_acodec") ?? string.Empty;
                    var vBitrate = GetJsonIntProp(profileEntry, "vbitrate") ?? 0;
                    var aBitrate = GetJsonIntProp(profileEntry, "abitrate") ?? 0;
                    var resolution = GetJsonIntProp(profileEntry, "resolution") ?? 0;
                    var channels = GetJsonIntProp(profileEntry, "channels") ?? 0;

                    // Resolve codec names: prefer direct vcodec/acodec, fall back to profile ref name heuristics
                    var videoCodec = ResolveCodecFromProfile(rawVideoCodec, proVideoCodec, isVideo: true);
                    var audioCodec = ResolveCodecFromProfile(rawAudioCodec, proAudioCodec, isVideo: false);

                    _logger.LogInformation(
                        "Transcode profile '{ProfileName}': video={VideoCodec}, audio={AudioCodec}, vbitrate={VBitrate}k, abitrate={ABitrate}k, resolution={Resolution}.",
                        profileName,
                        videoCodec,
                        audioCodec,
                        vBitrate,
                        aBitrate,
                        resolution);

                    return new ProfileCacheEntry(DateTime.UtcNow, profileName, mappedContainer)
                    {
                        IsTranscodeProfile = true,
                        VideoCodec = videoCodec,
                        AudioCodec = audioCodec,
                        VideoBitrateKbps = vBitrate,
                        AudioBitrateKbps = aBitrate,
                        VideoHeight = resolution,
                        AudioChannels = channels,
                    };
                }

                // Non-transcode profiles: derive from profile class
                var classContainer = MapProfileClassToContainer(profileClass);
                _logger.LogInformation(
                    "Streaming profile '{ProfileName}' (class={ProfileClass}) → container '{Container}' (derived from class).",
                    profileName,
                    profileClass,
                    classContainer);
                return new ProfileCacheEntry(DateTime.UtcNow, profileName, classContainer);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to detect details for streaming profile '{ProfileName}'. Defaulting to '{Container}'.",
                profileName,
                fallbackContainer);
            return new ProfileCacheEntry(DateTime.UtcNow, profileName, fallbackContainer);
        }
    }

    /// <summary>
    /// Resolves a TVHeadend codec reference to an FFmpeg codec name.
    /// Tries the raw codec field first, then falls back to heuristics from the codec profile reference name.
    /// </summary>
    private static string ResolveCodecFromProfile(
        string rawCodec,
        string profileRef,
        bool isVideo)
    {
        // Try mapping the raw codec name directly
        if (!string.IsNullOrWhiteSpace(rawCodec))
        {
            var mapped = isVideo ? MapVideoCodecName(rawCodec) : MapAudioCodecName(rawCodec);
            if (!string.IsNullOrWhiteSpace(mapped))
            {
                return mapped;
            }
        }

        // Fall back to heuristics from the codec profile reference name
        if (!string.IsNullOrWhiteSpace(profileRef))
        {
            var lower = profileRef.ToLowerInvariant();
            if (isVideo)
            {
                if (lower.Contains("264", StringComparison.Ordinal) || lower.Contains("avc", StringComparison.Ordinal))
                {
                    return "h264";
                }

                if (lower.Contains("265", StringComparison.Ordinal) || lower.Contains("hevc", StringComparison.Ordinal))
                {
                    return "hevc";
                }

                if (lower.Contains("mpeg2", StringComparison.Ordinal))
                {
                    return "mpeg2video";
                }

                if (lower.Contains("vp9", StringComparison.Ordinal))
                {
                    return "vp9";
                }

                if (lower.Contains("vp8", StringComparison.Ordinal))
                {
                    return "vp8";
                }

                if (lower.Contains("av1", StringComparison.Ordinal))
                {
                    return "av1";
                }
            }
            else
            {
                if (lower.Contains("aac", StringComparison.Ordinal))
                {
                    return "aac";
                }

                if (lower.Contains("ac3", StringComparison.Ordinal) || lower.Contains("a52", StringComparison.Ordinal))
                {
                    return "ac3";
                }

                if (lower.Contains("eac3", StringComparison.Ordinal))
                {
                    return "eac3";
                }

                if (lower.Contains("opus", StringComparison.Ordinal))
                {
                    return "opus";
                }

                if (lower.Contains("mp3", StringComparison.Ordinal))
                {
                    return "mp3";
                }

                if (lower.Contains("mp2", StringComparison.Ordinal))
                {
                    return "mp2";
                }
            }
        }

        return string.Empty;
    }

    /// <summary>Maps TVHeadend/libav video codec names to FFmpeg codec names.</summary>
    private static string MapVideoCodecName(string raw)
    {
        return raw.ToLowerInvariant() switch
        {
            "" or "copy" or "do not use" => string.Empty,
            "libx264" or "h264" or "h264_vaapi" or "h264_nvenc" or "h264_qsv" => "h264",
            "libx265" or "hevc" or "hevc_vaapi" or "hevc_nvenc" or "hevc_qsv" => "hevc",
            "mpeg2video" or "mpeg2" => "mpeg2video",
            "libvpx" or "vp8" => "vp8",
            "libvpx-vp9" or "vp9" => "vp9",
            "av1" or "libaom-av1" or "libsvtav1" => "av1",
            _ => raw.ToLowerInvariant()
        };
    }

    /// <summary>Maps TVHeadend/libav audio codec names to FFmpeg codec names.</summary>
    private static string MapAudioCodecName(string raw)
    {
        return raw.ToLowerInvariant() switch
        {
            "" or "copy" or "do not use" => string.Empty,
            "libfdk_aac" or "aac" => "aac",
            "ac3" or "a52" => "ac3",
            "eac3" => "eac3",
            "libmp3lame" or "mp3" => "mp3",
            "mp2" or "libtwolame" => "mp2",
            "libvorbis" or "vorbis" => "vorbis",
            "libopus" or "opus" => "opus",
            _ => raw.ToLowerInvariant()
        };
    }

    /// <summary>
    /// Holds probe-quality stream data parsed from a single Jellyfin mediainfo cache file.
    /// </summary>
    private sealed record CachedProbeResult(
        List<MediaStream> MediaStreams,
        string? Container,
        int Bitrate);

    /// <summary>
    /// Cached profile details extracted from a TVHeadend streaming profile.
    /// Used both for container detection and as a fallback to build synthetic MediaStreams
    /// when no elementary stream data is available from TVHeadend.
    /// </summary>
    private sealed record ProfileCacheEntry(DateTime Timestamp, string ProfileName, string Container)
    {
        /// <summary>Gets a value indicating whether this is a transcode profile (fixed output codecs).</summary>
        public bool IsTranscodeProfile { get; init; }

        /// <summary>Gets the output video codec (FFmpeg name), e.g. "h264", "hevc". Empty for pass-through.</summary>
        public string VideoCodec { get; init; } = string.Empty;

        /// <summary>Gets the output audio codec (FFmpeg name), e.g. "aac", "ac3". Empty for pass-through.</summary>
        public string AudioCodec { get; init; } = string.Empty;

        /// <summary>Gets the video bitrate in kbps (0 if not set / copy).</summary>
        public int VideoBitrateKbps { get; init; }

        /// <summary>Gets the audio bitrate in kbps (0 if not set / copy).</summary>
        public int AudioBitrateKbps { get; init; }

        /// <summary>Gets the output video height (0 = source resolution).</summary>
        public int VideoHeight { get; init; }

        /// <summary>Gets the output audio channel count (0 = source channels).</summary>
        public int AudioChannels { get; init; }
    }
}
