using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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
    /// In-memory cache for channel elementary-stream details queried from TVHeadend.
    /// Key = channel UUID, Value = (timestamp, list of elementary streams).
    /// Entries older than <see cref="StreamDetailsCacheTtl"/> are refreshed on next access.
    /// </summary>
    private readonly ConcurrentDictionary<string, (DateTime Timestamp, List<TvhElementaryStream> Streams)> _streamDetailsCache = new();

    /// <summary>
    /// Time-to-live for cached stream details. After this period the plugin re-queries TVHeadend.
    /// </summary>
    private static readonly TimeSpan StreamDetailsCacheTtl = TimeSpan.FromMinutes(5);

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

            var handler = new HttpClientHandler
            {
                CheckCertificateRevocationList = true
            };

            if (config.UseSSL && config.IgnoreCertificateErrors)
            {
                _logger.LogWarning("Ignoring SSL certificate errors. This is not recommended for production environments.");
                handler.ServerCertificateCustomValidationCallback = static (_, _, _, _) => true;
            }

            var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri($"{(config.UseSSL ? "https" : "http")}://{config.Host}:{config.Port}/")
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
    /// Fetches the list of channels from the TVHeadEnd API and converts them into Jellyfin-compatible channel objects.
    /// This method queries the `/api/channel/grid` endpoint of TVHeadEnd, deserializes the response, and maps it to Jellyfin's <see cref="ChannelInfo"/> format.
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
    /// Cancels an existing recording timer on TVHeadEnd.
    /// This method sends a request to the TVHeadEnd API to cancel a scheduled recording identified by its timer ID.
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
    /// Cancels a scheduled series timer on TVHeadEnd.
    /// This method sends a request to the TVHeadEnd API to cancel all scheduled recordings for a series identified by its timer ID.
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
    /// Creates a new recording timer on TVHeadEnd for a specific program or channel.
    /// This method sends a JSON object to the TVHeadEnd API to schedule a new recording.
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
    /// Creates a new series timer on TVHeadEnd to automatically record all episodes of a series.
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
    /// Updates an existing recording timer on TVHeadEnd.
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
    /// Updates an existing series timer on TVHeadEnd.
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
    /// Fetches all active recording timers from TVHeadEnd.
    /// This method queries the TVHeadEnd API to retrieve the list of currently scheduled recordings.
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
    /// Fetches all active series timers from TVHeadEnd.
    /// This method queries the TVHeadEnd API to retrieve the list of currently scheduled series timers.
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
    /// This method queries the TVHeadEnd API to retrieve program listings for a specific channel
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

            // Send the GET request to the TVHeadEnd API
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
    /// Maps a TVHeadend elementary stream type string to its FFmpeg codec name.
    /// </summary>
    private static string MapTvhStreamTypeToFfmpeg(string tvhType)
    {
        return tvhType.ToUpperInvariant() switch
        {
            // Video
            "MPEG2VIDEO" => "mpeg2video",
            "H264" => "h264",
            "HEVC" => "hevc",
            "VP8" => "vp8",
            "VP9" => "vp9",
            "THEORA" => "theora",
            "AV1" => "av1",

            // Audio
            "AC3" or "A52" => "ac3",
            "AAC" or "MP4A" => "aac",
            "EAC3" => "eac3",
            "MPEG2AUDIO" => "mp2",
            "VORBIS" => "vorbis",
            "OPUS" => "opus",
            "AC-4" => "ac4",
            "MP3" or "LIBMP3LAME" => "mp3",

            // Subtitles
            "TELETEXT" => "teletext",
            "DVBSUB" => "dvb_subtitle",

            _ => tvhType.ToLowerInvariant()
        };
    }

    /// <summary>
    /// Classifies a TVHeadend stream type string into the Jellyfin <see cref="MediaStreamType"/>.
    /// </summary>
    private static MediaStreamType ClassifyTvhStreamType(string tvhType)
    {
        return tvhType.ToUpperInvariant() switch
        {
            "MPEG2VIDEO" or "H264" or "HEVC" or "VP8" or "VP9" or "THEORA" or "AV1" => MediaStreamType.Video,
            "AC3" or "A52" or "AAC" or "MP4A" or "EAC3" or "MPEG2AUDIO" or "VORBIS" or "OPUS" or "AC-4" or "MP3" or "LIBMP3LAME" => MediaStreamType.Audio,
            "TELETEXT" or "DVBSUB" => MediaStreamType.Subtitle,
            _ => MediaStreamType.Data
        };
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
    /// Queries the TVHeadend API to discover the elementary streams (video, audio, subtitle)
    /// for a given channel UUID. The result is cached for <see cref="StreamDetailsCacheTtl"/>.
    /// <para>
    /// Flow:
    /// 1. Load the channel via <c>api/idnode/load?uuid=channelId</c> to find its service UUIDs.
    /// 2. Load the first service via <c>api/idnode/load?uuid=serviceId</c> to read its <c>stream</c> array.
    /// 3. Parse each entry into a <see cref="TvhElementaryStream"/>.
    /// </para>
    /// </summary>
    /// <param name="channelId">The UUID of the TVHeadend channel.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of elementary streams, or an empty list on failure.</returns>
    private async Task<List<TvhElementaryStream>> GetChannelElementaryStreamsAsync(string channelId, CancellationToken cancellationToken)
    {
        // Check cache first
        if (_streamDetailsCache.TryGetValue(channelId, out var cached) && DateTime.UtcNow - cached.Timestamp < StreamDetailsCacheTtl)
        {
            _logger.LogDebug("Using cached stream details for channel {ChannelId} ({Count} streams).", channelId, cached.Streams.Count);
            return cached.Streams;
        }

        try
        {
            // Step 1: Load channel to find its service UUID(s)
            var channelUrl = ConstructUrl($"api/idnode/load?uuid={Uri.EscapeDataString(channelId)}");
            _logger.LogDebug("Loading channel node from TVHeadend for channel {ChannelId}.", channelId);

            using var channelResponse = await _httpClient.GetAsync(channelUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            channelResponse.EnsureSuccessStatusCode();
            var channelBody = await channelResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            string? serviceUuid = null;
            using (var channelDoc = JsonDocument.Parse(channelBody))
            {
                if (!channelDoc.RootElement.TryGetProperty("entries", out var channelEntries) || channelEntries.GetArrayLength() == 0)
                {
                    _logger.LogWarning("TVHeadend returned no data for channel UUID {ChannelId}.", channelId);
                    return new List<TvhElementaryStream>();
                }

                var channelEntry = channelEntries[0];
                if (channelEntry.TryGetProperty("services", out var servicesEl) &&
                    servicesEl.ValueKind == JsonValueKind.Array && servicesEl.GetArrayLength() > 0)
                {
                    serviceUuid = servicesEl[0].GetString();
                }
            }

            if (string.IsNullOrWhiteSpace(serviceUuid))
            {
                _logger.LogWarning("Channel {ChannelId} has no services associated.", channelId);
                return new List<TvhElementaryStream>();
            }

            // Step 2: Load the service to get its elementary stream descriptors
            var serviceUrl = ConstructUrl($"api/idnode/load?uuid={Uri.EscapeDataString(serviceUuid)}");
            _logger.LogDebug("Loading service node from TVHeadend for service {ServiceUuid} (channel {ChannelId}).", serviceUuid, channelId);

            using var serviceResponse = await _httpClient.GetAsync(serviceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            serviceResponse.EnsureSuccessStatusCode();
            var serviceBody = await serviceResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var streams = new List<TvhElementaryStream>();
            using (var serviceDoc = JsonDocument.Parse(serviceBody))
            {
                if (!serviceDoc.RootElement.TryGetProperty("entries", out var serviceEntries) || serviceEntries.GetArrayLength() == 0)
                {
                    _logger.LogWarning("TVHeadend returned no data for service UUID {ServiceUuid}.", serviceUuid);
                    return streams;
                }

                var serviceEntry = serviceEntries[0];

                // The "stream" property contains the elementary stream descriptors from the PMT
                if (serviceEntry.TryGetProperty("stream", out var streamsEl) && streamsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var sEl in streamsEl.EnumerateArray())
                    {
                        var tvhType = GetJsonStringProp(sEl, "type") ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(tvhType))
                        {
                            continue;
                        }

                        streams.Add(new TvhElementaryStream
                        {
                            Index = GetJsonIntProp(sEl, "index") ?? 0,
                            TvhType = tvhType,
                            FfmpegCodec = MapTvhStreamTypeToFfmpeg(tvhType),
                            JellyfinStreamType = ClassifyTvhStreamType(tvhType),
                            Width = GetJsonIntProp(sEl, "width") ?? 0,
                            Height = GetJsonIntProp(sEl, "height") ?? 0,
                            Duration = GetJsonIntProp(sEl, "duration") ?? 0,
                            AspectNum = GetJsonIntProp(sEl, "aspect_num") ?? 0,
                            AspectDen = GetJsonIntProp(sEl, "aspect_den") ?? 0,
                            Language = GetJsonStringProp(sEl, "language") ?? string.Empty,
                            AudioChannels = GetJsonIntProp(sEl, "channels") ?? 0,
                            SampleRate = GetJsonIntProp(sEl, "rate") ?? 0,
                            AudioType = GetJsonIntProp(sEl, "audio_type") ?? 0,
                        });
                    }
                }
            }

            // Update cache
            _streamDetailsCache[channelId] = (DateTime.UtcNow, streams);

            _logger.LogInformation(
                "Queried TVHeadend service {ServiceUuid} for channel {ChannelId}: {Total} elementary streams " +
                "({Video} video, {Audio} audio, {Sub} subtitle, {Data} data).",
                serviceUuid,
                channelId,
                streams.Count,
                streams.Count(s => s.JellyfinStreamType == MediaStreamType.Video),
                streams.Count(s => s.JellyfinStreamType == MediaStreamType.Audio),
                streams.Count(s => s.JellyfinStreamType == MediaStreamType.Subtitle),
                streams.Count(s => s.JellyfinStreamType == MediaStreamType.Data));

            return streams;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query TVHeadend for elementary streams of channel {ChannelId}. Will fall back to probing.", channelId);
            return new List<TvhElementaryStream>();
        }
    }

    /// <summary>
    /// Builds a <see cref="MediaSourceInfo"/> for a given channel.
    /// <para>
    /// The method queries TVHeadend's API (<c>api/idnode/load</c>) for the channel's service
    /// elementary streams and uses the discovered codec, resolution, bitrate, and audio details
    /// to build an extremely detailed <see cref="MediaSourceInfo"/> so that Jellyfin can start
    /// playback without stream probing.
    /// </para>
    /// <para>
    /// When the API query fails or no streams are found, the method falls back to enabling
    /// probing so Jellyfin can still discover the format on its own.
    /// </para>
    /// </summary>
    private async Task<MediaSourceInfo> BuildMediaSourceInfoAsync(string channelId, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var streamUrl = ConstructUrl($"stream/channel/{channelId}?profile={config.StreamingProfile}", "url");
        _logger.LogInformation("Generated stream URL {Url} for channel ID {ChannelId}", MaskSensitiveData(streamUrl), channelId);

        // ── Query TVHeadend for the actual elementary streams ────────────
        var elementaryStreams = await GetChannelElementaryStreamsAsync(channelId, cancellationToken).ConfigureAwait(false);
        var hasStreamDetails = elementaryStreams.Count > 0 && elementaryStreams.Any(s => s.JellyfinStreamType == MediaStreamType.Video || s.JellyfinStreamType == MediaStreamType.Audio);

        // ── Container ───────────────────────────────────────────────────
        // With profile "pass" the output is always MPEG-TS (raw DVB transport stream).
        // Transcode profiles may use matroska/mp4; honour config when FastChannelSwitching is on.
        var container = config.EnableFastChannelSwitching && !string.IsNullOrWhiteSpace(config.StreamContainer)
            ? config.StreamContainer
            : "mpegts";

        var effectiveSupportsTranscoding = config.EnableFastChannelSwitching ? false : config.SupportsTranscoding;

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
            SupportsTranscoding = effectiveSupportsTranscoding,
            IsInfiniteStream = config.IsInfiniteStream,
            IgnoreDts = config.IgnoreDts,
            FallbackMaxStreamingBitrate = config.FallbackMaxStreamingBitrate,

            UseMostCompatibleTranscodingProfile = effectiveSupportsTranscoding,

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

        // ── Build detailed MediaStreams from TVH service data ────────────
        if (hasStreamDetails)
        {
            // We have real stream information – disable probing.
            mediaSource.SupportsProbing = false;
            mediaSource.AnalyzeDurationMs = config.AnalyzeDurationMs > 0 ? config.AnalyzeDurationMs : 200;

            var mediaStreams = new List<MediaStream>();
            int jellyfinIndex = 0;
            bool firstVideo = true;
            bool firstAudio = true;
            int totalBitrate = 0;

            // ── Video streams ───────────────────────────────────────────
            foreach (var video in elementaryStreams.Where(s => s.JellyfinStreamType == MediaStreamType.Video))
            {
                var vs = new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Index = jellyfinIndex++,
                    Codec = video.FfmpegCodec,
                    IsDefault = firstVideo,
                };

                firstVideo = false;

                if (video.Width > 0)
                {
                    vs.Width = video.Width;
                }

                if (video.Height > 0)
                {
                    vs.Height = video.Height;
                }

                // Frame rate: TVH reports frame duration in 90 kHz PTS ticks
                if (video.Duration > 0)
                {
                    var fps = 90000.0f / video.Duration;
                    vs.RealFrameRate = fps;
                    vs.AverageFrameRate = fps;
                }

                // Aspect ratio
                if (video.AspectNum > 0 && video.AspectDen > 0)
                {
                    vs.AspectRatio = $"{video.AspectNum}:{video.AspectDen}";
                }
                else if (video.Width > 0 && video.Height > 0)
                {
                    vs.AspectRatio = $"{video.Width}:{video.Height}";
                }

                // Interlaced detection: common DVB resolutions that are typically interlaced
                vs.IsInterlaced = video.Height switch
                {
                    576 => true,  // 576i (SD PAL)
                    480 => true,  // 480i (SD NTSC)
                    1080 when video.FfmpegCodec is "mpeg2video" => true, // 1080i common for MPEG-2
                    _ => false
                };

                // Codec profile / level hints
                switch (video.FfmpegCodec)
                {
                    case "h264":
                        vs.Profile = video.Height >= 720 ? "High" : "Main";
                        vs.Level = video.Height >= 1080 ? 41 : video.Height >= 720 ? 40 : 31;
                        vs.PixelFormat = "yuv420p";
                        vs.BitDepth = 8;
                        break;
                    case "hevc":
                        vs.Profile = "Main";
                        vs.Level = video.Height >= 2160 ? 150 : video.Height >= 1080 ? 120 : 90;
                        vs.PixelFormat = "yuv420p";
                        vs.BitDepth = 8;
                        break;
                    case "mpeg2video":
                        vs.Profile = video.Height >= 720 ? "High" : "Main";
                        vs.Level = video.Height >= 1080 ? 4 : video.Height >= 720 ? 3 : 2;
                        vs.PixelFormat = "yuv420p";
                        vs.BitDepth = 8;
                        break;
                }

                // Bitrate estimation when not known
                var videoBitrate = video.Height switch
                {
                    >= 2160 => 25000000,  // 4K UHD: ~25 Mbps
                    >= 1080 => 15000000,  // 1080: ~15 Mbps
                    >= 720 => 8000000,    // 720: ~8 Mbps
                    >= 576 => 4000000,    // SD PAL: ~4 Mbps
                    >= 480 => 3000000,    // SD NTSC: ~3 Mbps
                    _ => config.FallbackMaxStreamingBitrate
                };
                vs.BitRate = videoBitrate;
                totalBitrate += videoBitrate;

                mediaStreams.Add(vs);
            }

            // ── Audio streams ───────────────────────────────────────────
            foreach (var audio in elementaryStreams.Where(s => s.JellyfinStreamType == MediaStreamType.Audio))
            {
                var audioStream = new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = jellyfinIndex++,
                    Codec = audio.FfmpegCodec,
                    IsDefault = firstAudio,
                    Language = string.IsNullOrWhiteSpace(audio.Language) ? null : audio.Language,
                };

                firstAudio = false;

                // Channel count
                if (audio.AudioChannels > 0)
                {
                    audioStream.Channels = audio.AudioChannels;
                }
                else
                {
                    // Default channel count by codec type
                    audioStream.Channels = audio.FfmpegCodec switch
                    {
                        "ac3" or "eac3" => 6, // Surround codecs default to 5.1
                        _ => 2 // Stereo default
                    };
                }

                audioStream.ChannelLayout = audioStream.Channels switch
                {
                    1 => "mono",
                    2 => "stereo",
                    6 => "5.1",
                    8 => "7.1",
                    _ => $"{audioStream.Channels}.0"
                };

                // Sample rate
                audioStream.SampleRate = audio.SampleRate > 0 ? audio.SampleRate : 48000;

                // Codec profile
                if (audio.FfmpegCodec == "aac")
                {
                    audioStream.Profile = "LC";
                }

                // Bitrate estimation by codec
                var audioBitrate = audio.FfmpegCodec switch
                {
                    "ac3" => 384000,
                    "eac3" => 640000,
                    "aac" => 128000,
                    "mp2" => 192000,
                    "mp3" => 128000,
                    "opus" => 128000,
                    "vorbis" => 128000,
                    _ => 128000
                };
                audioStream.BitRate = audioBitrate;
                totalBitrate += audioBitrate;

                // Audio type hint from DVB
                if (audio.AudioType == 3)
                {
                    audioStream.Title = "Audio Description";
                }

                mediaStreams.Add(audioStream);
            }

            // ── Subtitle streams ────────────────────────────────────────
            foreach (var sub in elementaryStreams.Where(s => s.JellyfinStreamType == MediaStreamType.Subtitle))
            {
                mediaStreams.Add(new MediaStream
                {
                    Type = MediaStreamType.Subtitle,
                    Index = jellyfinIndex++,
                    Codec = sub.FfmpegCodec,
                    Language = string.IsNullOrWhiteSpace(sub.Language) ? null : sub.Language,
                    IsDefault = false,
                    IsForced = false,
                    IsExternal = false,
                });
            }

            mediaSource.MediaStreams = mediaStreams;

            // Total bitrate
            if (totalBitrate > 0)
            {
                mediaSource.Bitrate = totalBitrate;
            }

            _logger.LogInformation(
                "Built detailed MediaSourceInfo for channel {ChannelId}: Container={Container}, " +
                "{VideoCount} video, {AudioCount} audio, {SubCount} subtitle streams, " +
                "total bitrate ≈ {Bitrate} bps. Probing disabled.",
                channelId,
                container,
                mediaStreams.Count(s => s.Type == MediaStreamType.Video),
                mediaStreams.Count(s => s.Type == MediaStreamType.Audio),
                mediaStreams.Count(s => s.Type == MediaStreamType.Subtitle),
                totalBitrate);
        }
        else
        {
            // No stream details available – fall back to probing or Fast Channel Switching config
            if (config.EnableFastChannelSwitching)
            {
                // Use the static hints from the plugin configuration
                mediaSource.SupportsProbing = false;
                mediaSource.AnalyzeDurationMs = config.AnalyzeDurationMs > 0 ? config.AnalyzeDurationMs : 200;
                mediaSource.MediaStreams = BuildStaticMediaStreams(config);
                _logger.LogInformation("No TVH service data available for channel {ChannelId}. Using Fast Channel Switching config hints.", channelId);
            }
            else
            {
                // Let Jellyfin probe the stream
                mediaSource.SupportsProbing = true;
                if (config.AnalyzeDurationMs > 0)
                {
                    mediaSource.AnalyzeDurationMs = config.AnalyzeDurationMs;
                }

                _logger.LogWarning("No stream details from TVHeadend for channel {ChannelId} and Fast Channel Switching is off. Jellyfin will probe the stream.", channelId);
            }
        }

        return mediaSource;
    }

    /// <summary>
    /// Builds <see cref="MediaStream"/> hints from static plugin configuration values.
    /// Used as fallback when TVHeadend API query fails and Fast Channel Switching is enabled.
    /// </summary>
    private static List<MediaStream> BuildStaticMediaStreams(PluginConfiguration config)
    {
        var mediaStreams = new List<MediaStream>();

        if (!string.IsNullOrWhiteSpace(config.VideoCodec))
        {
            var videoStream = new MediaStream
            {
                Type = MediaStreamType.Video,
                Index = 0,
                Codec = config.VideoCodec,
                IsInterlaced = config.VideoIsInterlaced,
                IsDefault = true,
            };

            if (config.VideoWidth > 0)
            {
                videoStream.Width = config.VideoWidth;
            }

            if (config.VideoHeight > 0)
            {
                videoStream.Height = config.VideoHeight;
            }

            if (config.VideoFramerate > 0)
            {
                videoStream.RealFrameRate = config.VideoFramerate;
                videoStream.AverageFrameRate = config.VideoFramerate;
            }

            if (config.VideoBitrate > 0)
            {
                videoStream.BitRate = config.VideoBitrate;
            }

            if (config.VideoWidth > 0 && config.VideoHeight > 0)
            {
                videoStream.AspectRatio = $"{config.VideoWidth}:{config.VideoHeight}";
            }

            mediaStreams.Add(videoStream);
        }

        if (!string.IsNullOrWhiteSpace(config.AudioCodec))
        {
            var audioStream = new MediaStream
            {
                Type = MediaStreamType.Audio,
                Index = string.IsNullOrWhiteSpace(config.VideoCodec) ? 0 : 1,
                Codec = config.AudioCodec,
                IsDefault = true,
            };

            if (config.AudioChannels > 0)
            {
                audioStream.Channels = config.AudioChannels;
                audioStream.ChannelLayout = config.AudioChannels switch
                {
                    1 => "mono",
                    2 => "stereo",
                    6 => "5.1",
                    8 => "7.1",
                    _ => $"{config.AudioChannels}.0"
                };
            }

            if (config.AudioSampleRate > 0)
            {
                audioStream.SampleRate = config.AudioSampleRate;
            }

            if (config.AudioBitrate > 0)
            {
                audioStream.BitRate = config.AudioBitrate;
            }

            mediaStreams.Add(audioStream);
        }

        return mediaStreams;
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
    /// Fetches the list of content types from the TVHeadEnd API.
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
                throw new InvalidOperationException("No recording profiles retrieved from TVHeadEnd.");
            }

            // Find the profile matching the provided name
            var matchingProfile = result.Entries.FirstOrDefault(profile =>
                string.Equals(profile.Name, profileName, StringComparison.OrdinalIgnoreCase));

            if (matchingProfile?.Uuid == null)
            {
                throw new InvalidOperationException($"No matching recording profile found for '{profileName}' in TVHeadEnd.");
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

    /// <summary>
    /// Represents a single elementary stream (video, audio, subtitle, data) within a TVHeadend service.
    /// Populated from the <c>stream</c> array returned by <c>api/idnode/load</c> for a service UUID.
    /// </summary>
    private sealed class TvhElementaryStream
    {
        /// <summary>Gets the PID / stream index.</summary>
        public int Index { get; init; }

        /// <summary>Gets the TVHeadend codec type string, e.g. "H264", "AC3", "DVBSUB".</summary>
        public string TvhType { get; init; } = string.Empty;

        /// <summary>Gets the FFmpeg-compatible codec name, e.g. "h264", "ac3", "dvb_subtitle".</summary>
        public string FfmpegCodec { get; init; } = string.Empty;

        /// <summary>Gets the Jellyfin stream type classification.</summary>
        public MediaStreamType JellyfinStreamType { get; init; }

        /// <summary>Gets the video width in pixels (0 if unknown or not video).</summary>
        public int Width { get; init; }

        /// <summary>Gets the video height in pixels (0 if unknown or not video).</summary>
        public int Height { get; init; }

        /// <summary>Gets the video frame duration in 90 kHz PTS ticks (0 if unknown). fps = 90000 / Duration.</summary>
        public int Duration { get; init; }

        /// <summary>Gets the aspect ratio numerator (e.g. 16).</summary>
        public int AspectNum { get; init; }

        /// <summary>Gets the aspect ratio denominator (e.g. 9).</summary>
        public int AspectDen { get; init; }

        /// <summary>Gets the ISO 639 language code, e.g. "deu", "eng".</summary>
        public string Language { get; init; } = string.Empty;

        /// <summary>Gets the number of audio channels (0 if unknown or not audio).</summary>
        public int AudioChannels { get; init; }

        /// <summary>Gets the audio sample rate in Hz (0 if unknown).</summary>
        public int SampleRate { get; init; }

        /// <summary>Gets the DVB audio type field (0 = undefined).</summary>
        public int AudioType { get; init; }
    }
}
