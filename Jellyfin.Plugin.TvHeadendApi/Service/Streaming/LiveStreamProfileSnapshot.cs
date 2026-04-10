namespace Jellyfin.Plugin.TvHeadendApi.Service.Streaming;

/// <summary>
/// Snapshot of the configured TVHeadend streaming profile resolved for live playback.
/// </summary>
public sealed record LiveStreamProfileSnapshot(
    string ProfileName,
    string ProfileUuid,
    string ProfileClass,
    string Container,
    string VideoCodecReference,
    string AudioCodecReference,
    string VideoCodec,
    string AudioCodec,
    bool? Deinterlace);
