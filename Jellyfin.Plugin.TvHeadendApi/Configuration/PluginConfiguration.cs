using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TvHeadendApi.Configuration;

/// <summary>
/// Represents the plugin configuration for the TVHeadend plugin.
/// This class defines all configurable settings required for connecting to the TVHeadend server
/// and controlling its behavior within Jellyfin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// Sets default values for all configuration properties.
    /// </summary>
    public PluginConfiguration()
    {
        // Connection settings
        this.Host = "127.0.0.1";
        this.Port = 9981;
        this.UseSSL = false;
        this.IgnoreCertificateErrors = false;
        this.Webroot = "/";

        // Authentication settings
        this.AllowAnonymousAccess = false;
        this.Username = string.Empty;
        this.Password = string.Empty;
        this.AuthToken = string.Empty;

        // Streaming settings
        this.StreamingProfile = "pass";
        this.FallbackMaxStreamingBitrate = 3000000;
        this.IsInfiniteStream = true;

        // Playback behaviour
        this.SupportsDirectPlay = true;
        this.SupportsDirectStream = true;
        this.SupportsTranscoding = true;
        this.SupportsProbing = true;
        this.IgnoreDts = true;
        this.BufferMs = 0;
        this.AnalyzeDurationMs = 200;
        this.EnableMediaInfoCacheWrite = true;
        this.EnableMediaInfoCacheValidation = true;
        this.EnableJellyfinMetadataEnrichment = false;

        // Recording settings
        this.Priority = 5;
        this.PrePaddingSeconds = 5;
        this.PostPaddingSeconds = 5;
        this.RecordingProfile = string.Empty;

        // Statistics
        this.StatisticsRetentionPeriod = StatisticsRetentionPeriod.ThirtyDays;

        // Relay
        this.RelayEnabled = true;
        this.RelayHostOverride = string.Empty;

        // Relay token security
        this.EnableRelayTokenSecurity = true;
        this.StreamTokenTtlSeconds = 120;
        this.StreamTokenMaxUses = 5;
        this.ImageTokenTtlMinutes = 30;
        this.ImageTokenMaxUses = 0;
        this.EnableTokenReuse = true;
        this.StrictScopeValidation = true;
        this.CleanupExpiredTokensIntervalMinutes = 60;
        this.TokenValidationClockSkewSeconds = 5;

        // Resilience
        this.HealthTimeoutSeconds = 3;
        this.ImageTimeoutSeconds = 5;
        this.MetadataTimeoutSeconds = 10;
        this.StreamStartupTimeoutSeconds = 8;
        this.BackgroundRefreshTimeoutSeconds = 15;
        this.CircuitBreakerThreshold = 5;
        this.CircuitBreakerDurationSeconds = 30;
        this.CometReconnectBaseDelaySeconds = 2;
        this.CometReconnectMaxDelaySeconds = 120;

        // Streaming profile selection
        this.StreamingProfileSettings = new StreamingProfileSettings();

        // Expert
        this.AuthTokenMaxAttempts = 5;
        this.ProfileCacheTtlMinutes = 5;
    }

    // ── Connection ─────────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the TVHeadend server hostname or IP address.
    /// </summary>
    public string Host { get; set; }

    /// <summary>
    /// Gets or sets the TVHeadend server HTTP API port.
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether HTTPS should be used.
    /// </summary>
    public bool UseSSL { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether SSL certificate errors should be ignored.
    /// Only enable this when using self-signed certificates.
    /// </summary>
    public bool IgnoreCertificateErrors { get; set; }

    /// <summary>
    /// Gets or sets the web root path when TVHeadend is behind a reverse proxy sub-path.
    /// </summary>
    public string Webroot { get; set; }

    // ── Authentication ─────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets a value indicating whether anonymous (unauthenticated) access is allowed.
    /// </summary>
    public bool AllowAnonymousAccess { get; set; }

    /// <summary>
    /// Gets or sets the username for HTTP Basic Auth.
    /// </summary>
    public string Username { get; set; }

    /// <summary>
    /// Gets or sets the password for HTTP Basic Auth.
    /// </summary>
    public string Password { get; set; }

    /// <summary>
    /// Gets or sets a required authentication token appended as <c>?auth=</c> to stream and image URLs.
    /// This token is essential for direct playback from clients.
    /// <para>
    /// <strong>Required:</strong> A TVHeadend admin account is needed to generate this token.
    /// Generate it using the plugin's "Generate Auth Token" button in the settings UI
    /// (which uses the configured TVHeadend credentials), or manually create one in TVHeadend's
    /// admin panel (Configuration → Users → API Tokens or similar).
    /// </para>
    /// When set, this token is automatically appended to all stream URLs and image requests,
    /// allowing clients to play channels without transmitting raw passwords.
    /// </summary>
    public string AuthToken { get; set; }

    // ── Streaming ──────────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the TVHeadend streaming profile name used for live TV playback.
    /// This must match an existing profile in your TVHeadend configuration.
    /// Examples: "pass", "matroska", "webtv-h264-aac-mpegts".
    /// </summary>
    public string StreamingProfile { get; set; }

    /// <summary>
    /// Gets or sets the fallback maximum streaming bitrate in bits per second.
    /// Used when no specific bitrate information is available.
    /// </summary>
    public int FallbackMaxStreamingBitrate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the stream is infinite (live TV = true).
    /// </summary>
    public bool IsInfiniteStream { get; set; }

    // ── Playback Behaviour ─────────────────────────────────────────────

    /// <summary>
    /// Gets or sets a value indicating whether Jellyfin may play the stream directly
    /// without any server-side processing.
    /// </summary>
    public bool SupportsDirectPlay { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Jellyfin may remux the stream
    /// (change container without re-encoding).
    /// </summary>
    public bool SupportsDirectStream { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Jellyfin is allowed to transcode the stream.
    /// </summary>
    public bool SupportsTranscoding { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Jellyfin should probe the stream
    /// to detect codec, resolution and other properties before playback.
    /// <para>
    /// <strong>Recommended: <c>true</c></strong> when using the "jellyfin" transcode profile
    /// together with <see cref="EnableMediaInfoCacheWrite"/>. The plugin pre-creates cache files
    /// so Jellyfin finds probe data instantly without actually running FFmpeg — resulting in
    /// channel switching under 3 seconds even on the very first tune.
    /// </para>
    /// <para>
    /// <strong>Jellyfin core behaviour when enabled:</strong>
    /// Jellyfin's <c>AddMediaInfoWithProbe</c> checks the on-disk cache
    /// (<c>&lt;data&gt;/cache/mediainfo/&lt;md5&gt;.json</c>) first. If a cache file exists
    /// (written by the plugin or by a previous FFmpeg probe), it is used immediately.
    /// Only when no cache file exists does Jellyfin fall back to an actual FFmpeg probe
    /// (which adds 3+ seconds).
    /// </para>
    /// </summary>
    public bool SupportsProbing { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Decode Time Stamps (DTS) should be ignored.
    /// Can fix playback issues with certain MPEG-TS streams.
    /// </summary>
    public bool IgnoreDts { get; set; }

    /// <summary>
    /// Gets or sets the stream buffer size in milliseconds.
    /// Set to 0 to let Jellyfin use its default. Lower values reduce latency.
    /// </summary>
    public int BufferMs { get; set; }

    /// <summary>
    /// Gets or sets the FFmpeg analyze duration in milliseconds.
    /// Default: 200 ms for fast live TV startup.
    /// Jellyfin multiplies this value by 1 000 and passes it to ffmpeg as
    /// <c>-analyzeduration {value × 1000}</c> (i.e. in microseconds).
    /// Example: 200 ms → <c>-analyzeduration 200000</c> (200 000 µs).
    /// This plugin value takes precedence over Jellyfin's global FFmpeg analyzeduration setting.
    /// When set to 0 and stream details are available from TVHeadend, the plugin falls back to 200 ms.
    /// When set to 0 and no stream details are available, Jellyfin's global FFmpeg config is used as fallback.
    /// <para>
    /// <strong>Important:</strong> When <see cref="SupportsProbing"/> is <c>true</c>, Jellyfin's core
    /// (<c>MediaSourceManager.AddMediaInfoWithProbe</c>) unconditionally overrides this value to
    /// <c>3000</c> ms for live streams. The plugin value only takes effect reliably when probing is
    /// disabled and the plugin supplies stream details from TVHeadend.
    /// </para>
    /// Note: <c>-probesize</c> is controlled by Jellyfin's global FFmpeg settings and cannot be
    /// overridden by this plugin.
    /// </summary>
    public int AnalyzeDurationMs { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the plugin should pre-create Jellyfin mediainfo
    /// cache files when no cache entry exists for a channel.
    /// <para>
    /// When enabled, the plugin queries the selected TVHeadend streaming profile for its actual
    /// codec and container settings and writes a matching cache file so that Jellyfin can skip
    /// FFmpeg probing. This makes even the very first tune to a channel fast.
    /// </para>
    /// </summary>
    public bool EnableMediaInfoCacheWrite { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the plugin should validate existing mediainfo
    /// cache files against the currently selected TVHeadend streaming profile.
    /// <para>
    /// When enabled, the plugin compares the cached codec/container metadata with the active
    /// profile on every channel access. If the profile has changed (e.g. switched from "pass"
    /// to "jellyfin"), the outdated cache file is deleted and — if
    /// <see cref="EnableMediaInfoCacheWrite"/> is also enabled — replaced with a correct one.
    /// </para>
    /// </summary>
    public bool EnableMediaInfoCacheValidation { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Jellyfin metadata plugins should be given
    /// enrichment hints for Live TV EPG programs.
    /// <para>
    /// When enabled, the plugin forwards provider-id hints (if detectable from TVHeadend CRID/URI fields)
    /// so Jellyfin can attempt to enrich EPG items with additional artwork and descriptions.
    /// </para>
    /// </summary>
    public bool EnableJellyfinMetadataEnrichment { get; set; }

    // ── Recording (DVR) ────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the recording priority (lower = higher priority).
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Gets or sets the seconds to start recording before the scheduled time.
    /// </summary>
    public int PrePaddingSeconds { get; set; }

    /// <summary>
    /// Gets or sets the seconds to continue recording after the scheduled end.
    /// </summary>
    public int PostPaddingSeconds { get; set; }

    /// <summary>
    /// Gets or sets the DVR configuration profile name in TVHeadend.
    /// </summary>
    public string RecordingProfile { get; set; }

    // ── Relay ──────────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets a value indicating whether the relay service is enabled.
    /// When enabled, images and streams are proxied through Jellyfin instead of exposing TVHeadend URLs directly.
    /// Default: true.
    /// </summary>
    public bool RelayEnabled { get; set; }

    /// <summary>
    /// Gets or sets a custom Jellyfin host URL override for relay URL generation.
    /// When empty, the plugin auto-detects the Jellyfin URL via <c>IServerApplicationHost</c>.
    /// Example: <c>http://192.168.1.10:8096</c> or <c>https://jellyfin.example.com</c>.
    /// </summary>
    public string RelayHostOverride { get; set; }

    // ── Relay Token Security ────────────────────────────────────────────

    /// <summary>
    /// Gets or sets a value indicating whether relay token security is enabled.
    /// When enabled, public relay endpoints require a plugin-issued scoped token.
    /// Default: true.
    /// </summary>
    public bool EnableRelayTokenSecurity { get; set; }

    /// <summary>
    /// Gets or sets the time-to-live in seconds for stream relay tokens.
    /// Controls how long a token may be used to start a new stream relay request.
    /// Active streams are not interrupted when the token expires.
    /// Default: 120.
    /// </summary>
    public int StreamTokenTtlSeconds { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of times a stream relay token may be used.
    /// Supports Jellyfin/FFmpeg probing, retries, and the final playback request.
    /// A value of 5 is recommended for compatibility. Set to 0 for unlimited.
    /// Default: 5.
    /// </summary>
    public int StreamTokenMaxUses { get; set; }

    /// <summary>
    /// Gets or sets the time-to-live in minutes for image relay tokens.
    /// Image tokens typically need a longer TTL than stream tokens to support
    /// caching-friendly behavior and prevent channel logos from breaking.
    /// Default: 30.
    /// </summary>
    public int ImageTokenTtlMinutes { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of times an image relay token may be used.
    /// Set to 0 for unlimited uses. Default: 0 (unlimited).
    /// </summary>
    public int ImageTokenMaxUses { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether token reuse is enabled.
    /// When enabled, the same token can be used multiple times within its TTL and max-uses limits.
    /// Default: true.
    /// </summary>
    public bool EnableTokenReuse { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether strict scope validation is enabled.
    /// When enabled, a stream token for channel A cannot be used for channel B,
    /// and an image token cannot be used as a stream token.
    /// Default: true.
    /// </summary>
    public bool StrictScopeValidation { get; set; }

    /// <summary>
    /// Gets or sets the interval in minutes for cleaning up expired and revoked relay tokens.
    /// Prevents the SQLite database from growing unbounded.
    /// Default: 60.
    /// </summary>
    public int CleanupExpiredTokensIntervalMinutes { get; set; }

    /// <summary>
    /// Gets or sets the clock skew tolerance in seconds for token expiration checks.
    /// Accounts for minor clock differences between token issuance and validation.
    /// Default: 5.
    /// </summary>
    public int TokenValidationClockSkewSeconds { get; set; }

    // ── Statistics ─────────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets how long viewing statistics are retained before being pruned.
    /// Default: 30 days.
    /// </summary>
    public StatisticsRetentionPeriod StatisticsRetentionPeriod { get; set; }

    // ── Resilience ────────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the timeout in seconds for health check / ping operations. Default: 3.
    /// </summary>
    public int HealthTimeoutSeconds { get; set; }

    /// <summary>
    /// Gets or sets the timeout in seconds for image/logo fetch operations. Default: 5.
    /// </summary>
    public int ImageTimeoutSeconds { get; set; }

    /// <summary>
    /// Gets or sets the timeout in seconds for metadata operations (channels, EPG, profiles). Default: 10.
    /// </summary>
    public int MetadataTimeoutSeconds { get; set; }

    /// <summary>
    /// Gets or sets the timeout in seconds for stream startup (headers). Default: 8.
    /// </summary>
    public int StreamStartupTimeoutSeconds { get; set; }

    /// <summary>
    /// Gets or sets the timeout in seconds for background refresh tasks. Default: 15.
    /// </summary>
    public int BackgroundRefreshTimeoutSeconds { get; set; }

    /// <summary>
    /// Gets or sets the number of consecutive failures before the circuit breaker opens. Default: 5.
    /// </summary>
    public int CircuitBreakerThreshold { get; set; }

    /// <summary>
    /// Gets or sets the duration in seconds the circuit breaker stays open before half-open. Default: 30.
    /// </summary>
    public int CircuitBreakerDurationSeconds { get; set; }

    /// <summary>
    /// Gets or sets the base delay in seconds for CometService WebSocket reconnect backoff. Default: 2.
    /// </summary>
    public int CometReconnectBaseDelaySeconds { get; set; }

    /// <summary>
    /// Gets or sets the maximum delay in seconds for CometService WebSocket reconnect backoff. Default: 120.
    /// </summary>
    public int CometReconnectMaxDelaySeconds { get; set; }

    // ── Expert ────────────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the maximum number of attempts when generating an auth token. Default: 5.
    /// </summary>
    public int AuthTokenMaxAttempts { get; set; }

    /// <summary>
    /// Gets or sets the TTL in minutes for the cached streaming profile metadata. Default: 5.
    /// </summary>
    public int ProfileCacheTtlMinutes { get; set; }

    // ── Streaming Profile Selection ─────────────────────────────────────

    /// <summary>
    /// Gets or sets the streaming profile selection settings.
    /// Controls hierarchical profile resolution with global defaults, per-channel overrides,
    /// per-channel-group overrides, and client/user rules.
    /// </summary>
    public StreamingProfileSettings StreamingProfileSettings { get; set; }
}
