namespace Jellyfin.Plugin.TvHeadendApi.Api;

/// <summary>
/// Response model for profile-related controller actions.
/// </summary>
public class ProfileDetectionResult
{
    /// <summary>Gets or sets a value indicating whether detection was successful.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets a human-readable status message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the profile name in TVHeadend.</summary>
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the TVHeadend profile class (e.g. "profile-transcode").</summary>
    public string ProfileClass { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this is a transcode profile.</summary>
    public bool IsTranscodeProfile { get; set; }

    /// <summary>Gets or sets the detected container format.</summary>
    public string Container { get; set; } = string.Empty;

    /// <summary>Gets or sets the detected video codec.</summary>
    public string VideoCodec { get; set; } = string.Empty;

    /// <summary>Gets or sets the detected audio codec.</summary>
    public string AudioCodec { get; set; } = string.Empty;

    /// <summary>Gets or sets the detected video height (resolution). 0 = not specified.</summary>
    public int VideoHeight { get; set; }

    /// <summary>Gets or sets the detected video bitrate in bps. 0 = not specified.</summary>
    public int VideoBitrate { get; set; }

    /// <summary>Gets or sets the detected audio bitrate in bps. 0 = not specified.</summary>
    public int AudioBitrate { get; set; }

    /// <summary>Gets or sets the detected number of audio channels. 0 = not specified.</summary>
    public int AudioChannels { get; set; }

    /// <summary>Gets or sets a value indicating whether the output video is interlaced.</summary>
    public bool VideoIsInterlaced { get; set; }

    /// <summary>Gets or sets the name of the created/detected video codec profile in TVHeadend.</summary>
    public string VideoCodecProfile { get; set; } = string.Empty;

    /// <summary>Gets or sets the name of the created/detected audio codec profile in TVHeadend.</summary>
    public string AudioCodecProfile { get; set; } = string.Empty;
}
