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
        this.SupportsTranscoding = false;
        this.SupportsProbing = false;
        this.IgnoreDts = false;
        this.BufferMs = 0;
        this.AnalyzeDurationMs = 0;
        this.EnableMediaInfoCache = true;

        // Recording settings
        this.EnableTvhDvr = true;
        this.Priority = 5;
        this.PrePaddingSeconds = 5;
        this.PostPaddingSeconds = 5;
        this.RecordingProfile = "default";
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
    /// Gets or sets an optional authentication token appended as <c>?auth=</c> to image/parameter URLs.
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
    /// Disabling this speeds up channel switching when the format is known.
    /// <para>
    /// <strong>Jellyfin core behaviour when enabled:</strong>
    /// Jellyfin's <c>AddMediaInfoWithProbe</c> always waits at least 3 000 ms before probing
    /// (<c>Math.Max(3000, AnalyzeDurationMs)</c>) and then overrides <see cref="AnalyzeDurationMs"/>
    /// to 3 000 ms for all live streams, regardless of the value set by this plugin.
    /// Probe results are cached on disk under <c>&lt;data&gt;/cache/mediainfo/&lt;md5&gt;.json</c>,
    /// keyed by the stream's LiveStreamId. The first probe takes 3+ seconds; subsequent opens
    /// of the same channel use the disk cache and complete in milliseconds.
    /// </para>
    /// <para>
    /// <strong>Recommended: <c>false</c>.</strong> The plugin queries TVHeadend for stream details
    /// automatically, which avoids probing entirely and provides near-instant channel switching.
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
    /// Jellyfin multiplies this value by 1 000 and passes it to ffmpeg as
    /// <c>-analyzeduration {value × 1000}</c> (i.e. in microseconds).
    /// Example: 200 ms → <c>-analyzeduration 200000</c> (200 000 µs).
    /// This plugin value takes precedence over Jellyfin's global FFmpeg analyzeduration setting.
    /// When set to 0 and stream details are available from TVHeadend, the plugin defaults to 200 ms.
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
    /// Gets or sets a value indicating whether the plugin should read Jellyfin's mediainfo
    /// probe cache files (<c>&lt;data&gt;/cache/mediainfo/*.json</c>) and use the detailed
    /// stream data from previous FFmpeg probes when building <c>MediaSourceInfo</c>.
    /// <para>
    /// When enabled, if a cache file exists for a channel (created by a prior probe run),
    /// the plugin uses the probe-quality <c>MediaStreams</c> (with ColorRange, ColorSpace,
    /// Profile, Level, PixelFormat, BitDepth, RealFrameRate, DisplayTitle, etc.)
    /// instead of the synthetically constructed streams from TVHeadend's service API.
    /// This provides the best of both worlds: probe-quality metadata without the 3+ second
    /// probe delay on every channel open.
    /// </para>
    /// </summary>
    public bool EnableMediaInfoCache { get; set; }

    // ── Recording (DVR) ────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets a value indicating whether TVHeadend-side DVR is enabled.
    /// </summary>
    public bool EnableTvhDvr { get; set; }

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
}
