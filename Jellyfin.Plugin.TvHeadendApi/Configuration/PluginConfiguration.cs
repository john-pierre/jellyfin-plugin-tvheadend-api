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
        // Default connection settings
        this.Host = "192.168.0.28";
        this.Port = 9981;
        this.UseSSL = false;
        this.IgnoreCertificateErrors = false;
        this.Webroot = "/";

        // Authentication settings
        this.AllowAnonymousAccess = false;
        this.Username = "jellyfin";
        this.Password = "jellyfin";
        this.AuthToken = "PLSXqzk4gv0U6-yfu05c6LRihC.Y";

        // Streaming settings
        this.StreamingProfile = "pass";
        this.BufferMs = 500;
        this.FallbackMaxStreamingBitrate = 30000000;
        this.AnalyzeDurationMs = 500;
        this.SupportsTranscoding = true;
        this.SupportsProbing = false;
        this.SupportsDirectPlay = false;
        this.SupportsDirectStream = false;
        this.IsInfiniteStream = true;
        this.IgnoreDts = false;

        // Recording settings
        this.Priority = 5;
        this.PrePaddingSeconds = 5;
        this.PostPaddingSeconds = 5;
        this.RecordingProfile = "default";
    }

    /// <summary>
    /// Gets or sets the TVHeadend server hostname or IP address.
    /// Example: "192.168.0.28".
    /// </summary>
    public string Host { get; set; }

    /// <summary>
    /// Gets or sets the TVHeadend server port.
    /// Example: 9981.
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether SSL should be used for the connection.
    /// If set to true, the plugin will connect using HTTPS for HTTP requests
    /// and WSS for WebSocket connections, ensuring the communication is encrypted.
    /// Example: true.
    /// </summary>
    public bool UseSSL { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether SSL certificate errors should be ignored.
    /// If set to true, the plugin will bypass SSL certificate validation, allowing
    /// connections to servers with invalid or self-signed certificates.
    /// Use this option with caution, as it may expose the communication to security risks.
    /// Example: true.
    /// </summary>
    public bool IgnoreCertificateErrors { get; set; } = false;

    /// <summary>
    /// Gets or sets the web root path for the TVHeadend server.
    /// Example: "/".
    /// </summary>
    public string Webroot { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether anonymous access is allowed.
    /// If set to true, no authentication is required to connect to the TVHeadend server.
    /// Example: true.
    /// </summary>
    public bool AllowAnonymousAccess { get; set; }

    /// <summary>
    /// Gets or sets the username for authentication with the TVHeadend server.
    /// This is only used if <see cref="AllowAnonymousAccess"/> is set to false.
    /// Example: "admin".
    /// </summary>
    public string Username { get; set; }

    /// <summary>
    /// Gets or sets the password for authentication with the TVHeadend server.
    /// This is only used if <see cref="AllowAnonymousAccess"/> is set to false.
    /// Example: "password123".
    /// </summary>
    public string Password { get; set; }

    /// <summary>
    /// Gets or sets the auth token for authentication with the TVHeadend server.
    /// This is only used if <see cref="AllowAnonymousAccess"/> is set to false.
    /// Example: "password123".
    /// </summary>
    public string AuthToken { get; set; }

    /// <summary>
    /// Gets or sets the streaming profile.
    /// Example: "high-quality".
    /// </summary>
    public string StreamingProfile { get; set; }

    /// <summary>
    /// Gets or sets the buffer size for streaming in ms.
    /// This defines the size of the buffer used for streaming data.
    /// Example: 500 KB.
    /// </summary>
    public int BufferMs { get; set; }

    /// <summary>
    /// Gets or sets the fallback maximum streaming bitrate in kilobits per second (kbps).
    /// This value is used when no specific bitrate is defined for streaming.
    /// Example: 30000000 kbps (30 Mbps).
    /// </summary>
    public int FallbackMaxStreamingBitrate { get; set; }

    /// <summary>
    /// Gets or sets the analyze duration for streams in milliseconds.
    /// This defines the time spent analyzing streams before playback starts.
    /// Example: 500 ms.
    /// </summary>
    public int AnalyzeDurationMs { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether transcoding is supported.
    /// If true, streams can be transcoded to match client device capabilities.
    /// Example: true.
    /// </summary>
    public bool SupportsTranscoding { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether probing is supported.
    /// Probing detects stream properties before playback.
    /// Example: false.
    /// </summary>
    public bool SupportsProbing { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether direct play is supported.
    /// Direct play allows playback without any transcoding.
    /// Example: false.
    /// </summary>
    public bool SupportsDirectPlay { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether direct stream is supported.
    /// Direct stream allows playback with minimal modifications to the stream.
    /// Example: false.
    /// </summary>
    public bool SupportsDirectStream { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the stream is infinite (e.g., live streams).
    /// Example: true.
    /// </summary>
    public bool IsInfiniteStream { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether DTS (Decode Time Stamps) should be ignored.
    /// Ignoring DTS can resolve compatibility issues with some streams.
    /// Example: false.
    /// </summary>
    public bool IgnoreDts { get; set; }

    /// <summary>
    /// Gets or sets the priority of recordings.
    /// Lower values indicate higher priority.
    /// Example: 5 (normal priority).
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Gets or sets the pre-padding time for recordings in seconds.
    /// Defines how many seconds before the scheduled start time the recording begins.
    /// Example: 5 seconds.
    /// </summary>
    public int PrePaddingSeconds { get; set; }

    /// <summary>
    /// Gets or sets the post-padding time for recordings in seconds.
    /// Defines how many seconds after the scheduled end time the recording continues.
    /// Example: 5 seconds.
    /// </summary>
    public int PostPaddingSeconds { get; set; }

    /// <summary>
    /// Gets or sets the recording profile to use for DVR.
    /// Example: "high-quality".
    /// </summary>
    public string RecordingProfile { get; set; }
}
