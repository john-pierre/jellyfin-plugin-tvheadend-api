using System;
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
    private HttpClient _httpClient;
    private bool _disposed;
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

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
    /// Gets the base URL of the TVHeadEnd server as configured in the plugin settings.
    /// This property retrieves the host name or IP address of the server, which is used as the
    /// starting point for API calls and user-facing links.
    /// </summary>
    public string HomePageUrl => "https://tvheadend.org";

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

    /// <summary>
    /// Constructs a complete API URL for the TVHeadEnd service.
    /// This method supports different authentication methods (Basic Auth in URL, Header, or URL parameter).
    /// If anonymous access is allowed, no authentication is used regardless of the specified method.
    /// </summary>
    /// <param name="endpoint">The API endpoint relative to the TVHeadEnd base URL. Example: "stream/channel/1234".</param>
    /// <param name="authMethod">The authentication method to use: "url", "header", or "parameter". Default is "header".</param>
    /// <returns>A fully constructed URL including the web root and authentication token or credentials, if applicable.</returns>
    public string ConstructUrl(string endpoint, string authMethod = "header")
    {
        // Dynamically fetch the current configuration
        var config = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("Plugin configuration is not available.");

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
                baseUrl = string.IsNullOrWhiteSpace(config.AuthToken) ? baseUrl : $"{baseUrl}?auth={config.AuthToken}";
                break;

            case "header":
            default:
                // Use default HTTP header-based authentication
                var handler = new HttpClientHandler
                {
                    CheckCertificateRevocationList = true
                };

                if (config.UseSSL && config.IgnoreCertificateErrors)
                {
                    _logger.LogWarning("Ignoring SSL certificate errors. This is not recommended for production environments.");
                    handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
                }

                _httpClient = new HttpClient(handler)
                {
                    BaseAddress = new Uri($"{(config.UseSSL ? "https" : "http")}://{config.Host}:{config.Port}/")
                };

                var headerCredentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{config.Username}:{config.Password}"));
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", headerCredentials);
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
            // Build the URL for the API endpoint
            var url = ConstructUrl("api/channel/grid");

            // Log the URL being used for the API call (sensitive data is masked)
            _logger.LogInformation("Fetching channels from TVHeadEnd at {Url}...", MaskSensitiveData(url));

            // Make an HTTP GET request to the TVHeadEnd server and retrieve the response as a string
            var response = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);

            // Deserialize the JSON response into a ChannelApiResponse object
            var result = JsonSerializer.Deserialize<TvhApiChannelGridResponse>(response, JsonOptions);

            // Check if the response contains valid channel entries
            if (result?.Entries != null && result.Entries.Any())
            {
                // Map the TVHeadEnd channels to Jellyfin's ChannelInfo format
                var channels = result.Entries.Select(channel => new ChannelInfo
                {
                    Id = channel.Uuid, // Unique identifier for the channel
                    Name = channel.Name, // Display name of the channel
                    Number = channel.Number.ToString(CultureInfo.InvariantCulture), // Logical number of the channel
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
        try
        {
            // Define the relative path for the API endpoint
            var url = ConstructUrl("api/dvr/entry/grid_upcoming");

            // Log the request for debugging purposes
            _logger.LogInformation("Fetching timers from TVHeadEnd at {Url}...", MaskSensitiveData(url));

            // Send an HTTP GET request to the TVHeadEnd API
            var response = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);

            // Deserialize the JSON response into a list of recordings from TVHeadEnd
            var result = JsonSerializer.Deserialize<TvhApiDvrEntryGridResponse>(response, JsonOptions);

            // Check if the response contains valid timer entries
            if (result?.Entries != null && result.Entries.Any())
            {
                // Map TVHeadEnd recordings to Jellyfin's TimerInfo format
                var timers = result.Entries.Select(recording => new TimerInfo
                {
                    Id = recording.Uuid,
                    Name = recording.DispTitle,
                    Overview = recording.DispDescription,
                    ChannelId = recording.Channel,
                    StartDate = DateTimeOffset.FromUnixTimeSeconds(recording.Start).UtcDateTime,
                    EndDate = DateTimeOffset.FromUnixTimeSeconds(recording.Stop).UtcDateTime,
                    Priority = recording.Priority,
                    RecordingPath = recording.Filename,
                    OfficialRating = recording.RatingLabel,
                    CommunityRating = null, // No direct mapping available
                    Genres = recording.Genre?
                        .Select(genreId =>
                            EtsiGenreMapping.TryGetValue(genreId, out var genreDescription)
                                ? genreDescription
                                : $"Unknown ({genreId})")
                        .ToArray() ?? Array.Empty<string>(),
                    Tags = new[]
                    {
                        recording.NoReRecord ? "NoReRecord" : null,
                        recording.NoResched ? "NoResched" : null,
                        recording.Enabled ? "Enabled" : null
                    }.Where(tag => !string.IsNullOrEmpty(tag)).ToArray(),
                    IsRepeat = recording.Duplicate > 0,
                    SeriesTimerId = recording.AutoRec,
                    ShowId = recording.Parent,
                    OriginalAirDate = recording.FirstAired > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(recording.FirstAired).UtcDateTime
                        : null as DateTime?,
                    EpisodeTitle = recording.DispSubtitle,
                    Status = recording.SchedStatus switch
                    {
                        "scheduled" => RecordingStatus.New,
                        "recording" => RecordingStatus.InProgress,
                        "completed" => RecordingStatus.Completed,
                        "completedError" => RecordingStatus.Error,
                        "cancelled" => RecordingStatus.Cancelled,
                        "conflictedOk" => RecordingStatus.ConflictedOk,
                        "conflictedNotOk" => RecordingStatus.ConflictedNotOk,
                        _ => RecordingStatus.Error
                    }
                }).ToList();

                // Log the number of timers successfully retrieved and mapped
                _logger.LogInformation("Successfully retrieved {Count} timers from TVHeadEnd.", timers.Count);

                return timers;
            }

            // If no timers were found, log and return an empty list
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
        try
        {
            // Define the relative path for the API endpoint
            var url = ConstructUrl("api/dvr/autorec/grid");

            // Log the request for debugging purposes
            _logger.LogInformation("Fetching series timers from TVHeadEnd at {Url}...", MaskSensitiveData(url));

            // Send an HTTP GET request to the TVHeadEnd API
            var response = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);

            // Deserialize the JSON response into a list of series timers from TVHeadEnd
            var result = JsonSerializer.Deserialize<TvhApiDvrAutoRecGridResponse>(response, JsonOptions);

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
            var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

            // Handle unsuccessful responses
            if (!response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogError("Failed to fetch EPG data for channel ID: {ChannelId}. HTTP Status: {StatusCode}. Response: {Response}.", channelId, response.StatusCode, responseContent);
                return Enumerable.Empty<ProgramInfo>();
            }

            // Deserialize the JSON response into a list of EPG events from TVHeadEnd
            var result = JsonSerializer.Deserialize<TvhApiEpgEventsGridResponse>(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), JsonOptions);

            // Filter and map the results
            var filteredPrograms = result?.Entries?
                .Where(entry =>
                {
                    var programStart = DateTimeOffset.FromUnixTimeSeconds(entry.Start).UtcDateTime;
                    var programEnd = DateTimeOffset.FromUnixTimeSeconds(entry.Stop).UtcDateTime;
                    return entry.ChannelUuid == channelId && programEnd <= endDateUtc;
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
    /// Fetches the streaming URL for a specific channel from TVHeadEnd.
    /// Constructs the URL based on the channel ID and returns it as a <see cref="MediaSourceInfo"/> object.
    /// </summary>
    /// <param name="channelId">The unique identifier of the channel to stream.</param>
    /// <param name="streamId">The stream identifier, not used in this implementation but required by the interface.</param>
    /// <param name="cancellationToken">A token to cancel the operation if necessary.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a <see cref="MediaSourceInfo"/> object with the streaming information.</returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="channelId"/> is null or empty.</exception>
    /// <exception cref="Exception">Thrown if there is an error while generating the stream URL.</exception>
    public Task<MediaSourceInfo> GetChannelStream(string channelId, string streamId, CancellationToken cancellationToken)
    {
        try
        {
            // Validate the channelId parameter
            if (string.IsNullOrWhiteSpace(channelId))
            {
                throw new ArgumentException("Channel ID cannot be null or empty.", nameof(channelId));
            }

            // Dynamically fetch the current configuration
            var config = Plugin.Instance?.Configuration
                ?? throw new InvalidOperationException("Plugin configuration is not available.");

            // Construct the streaming URL using ConstructUrl
            var streamUrl = ConstructUrl($"stream/channel/{channelId}?profile={config.StreamingProfile}", "url");

            // Log the streaming URL without sensitive information
            _logger.LogInformation("Generated stream URL {Url} for channel ID {ChannelId}", MaskSensitiveData(streamUrl), channelId);

            // Construct the MediaSourceInfo object using all configuration parameters
            var mediaSourceInfo = new MediaSourceInfo
            {
                Id = channelId,
                Path = streamUrl,
                Protocol = MediaProtocol.Http,
                IsRemote = true,
                SupportsDirectPlay = config.SupportsDirectPlay,
                SupportsDirectStream = config.SupportsDirectStream,
                SupportsTranscoding = config.SupportsTranscoding,
                AnalyzeDurationMs = config.AnalyzeDurationMs,
                FallbackMaxStreamingBitrate = config.FallbackMaxStreamingBitrate,
                UseMostCompatibleTranscodingProfile = true,
                RequiresOpening = true,
                RequiresClosing = true
            };

            // Return the constructed MediaSourceInfo object
            return Task.FromResult(mediaSourceInfo);
        }
        catch (Exception ex)
        {
            // Log any errors that occur while generating the stream URL
            _logger.LogError(ex, "Error occurred while generating stream URL for channel ID {ChannelId}.", channelId);
            throw; // Re-throw the exception to ensure the caller is aware of the failure
        }
    }

    /// <summary>
    /// Fetches the available media sources for streaming a specific channel from TVHeadEnd.
    /// This method constructs the media source information for a given channel ID and returns it as a list of <see cref="MediaSourceInfo"/> objects.
    /// </summary>
    /// <param name="channelId">The unique identifier of the channel to stream.</param>
    /// <param name="cancellationToken">A token to cancel the operation if necessary.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The task result contains a list of <see cref="MediaSourceInfo"/> objects representing the available media sources.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown if <paramref name="channelId"/> is null or empty.</exception>
    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        try
        {
            // Validate the channelId parameter to ensure it is not null or empty
            if (string.IsNullOrWhiteSpace(channelId))
            {
                throw new ArgumentException("Channel ID cannot be null or empty.", nameof(channelId));
            }

            // Dynamically fetch the current configuration
            var config = Plugin.Instance?.Configuration
                ?? throw new InvalidOperationException("Plugin configuration is not available.");

            // Build the streaming URL using ConstructUrl
            var streamUrl = ConstructUrl($"stream/channel/{channelId}?profile={config.StreamingProfile}", "url");

            // Log the streaming URL for debugging purposes
            _logger.LogInformation("Generated media source URL {Url} for channel ID {ChannelId}: {StreamUrl}", MaskSensitiveData(streamUrl), channelId, streamUrl);

            // Construct the MediaSourceInfo object to describe the media source
            var mediaSourceInfo = new MediaSourceInfo
            {
                Id = channelId,
                Path = streamUrl,
                Protocol = MediaProtocol.Http,
                IsRemote = true,
                SupportsDirectPlay = config.SupportsDirectPlay,
                SupportsDirectStream = config.SupportsDirectStream,
                SupportsTranscoding = config.SupportsTranscoding,
                AnalyzeDurationMs = config.AnalyzeDurationMs,
                SupportsProbing = true,
                IsInfiniteStream = true,
                IgnoreDts = true,
                RequiresOpening = true,
                RequiresClosing = true,
                ReadAtNativeFramerate = false,
                BufferMs = config.BufferMs,
                FallbackMaxStreamingBitrate = config.FallbackMaxStreamingBitrate,
                UseMostCompatibleTranscodingProfile = true,
                MediaStreams = new List<MediaStream>
                {
                    new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        // Set the index to -1 because we don't know the exact index of the video stream within the container
                        Index = -1,
                        // Set to true if unknown to enable deinterlacing
                        IsInterlaced = true,
                        RealFrameRate = 50.0F
                    },
                    new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        // Set the index to -1 because we don't know the exact index of the audio stream within the container
                        Index = -1
                    }
                }
            };

            mediaSourceInfo.InferTotalBitrate(true);

            // Return the media source information as a list containing a single source
            return Task.FromResult(new List<MediaSourceInfo> { mediaSourceInfo });
        }
        catch (Exception ex)
        {
            // Log any errors that occur while constructing the media source information
            _logger.LogError(ex, "Error occurred while generating media sources for channel ID {ChannelId}.", channelId);
            throw; // Re-throw the exception to ensure the caller is aware of the failure
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
            var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

            // Ensure the response indicates success (HTTP status code 200-299)
            if (!response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogError("Failed to fetch content types. HTTP Status: {StatusCode}. Response: {Response}.", response.StatusCode, responseContent);
                return new Dictionary<int, string>();
            }

            // Deserialize the JSON response
            var result = JsonSerializer.Deserialize<TvhApiEpgContentTypeListResponse>(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), JsonOptions);

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
            var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

            // Ensure the response indicates success (HTTP status code 200-299)
            if (!response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogError("Failed to fetch channel tags. HTTP Status: {StatusCode}. Response: {Response}.", response.StatusCode, responseContent);
                return new Dictionary<string, string>();
            }

            // Deserialize the JSON response
            var result = JsonSerializer.Deserialize<TvhApiChannelTagResponse>(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), JsonOptions);

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

            // Fetch the recording profiles from TVHeadEnd
            var response = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);

            // Deserialize the response into TvhApiDvrConfigGridResponse
            var result = JsonSerializer.Deserialize<TvhApiDvrConfigGridResponse>(response, JsonOptions);

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
}
