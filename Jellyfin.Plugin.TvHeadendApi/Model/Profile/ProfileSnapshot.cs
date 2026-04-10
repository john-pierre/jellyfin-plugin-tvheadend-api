namespace Jellyfin.Plugin.TvHeadendApi.Model.Profile;

/// <summary>
/// Snapshot of the configured TVHeadend streaming profile resolved for live playback.
/// </summary>
public sealed record ProfileSnapshot(
    string ProfileName,
    string ProfileUuid,
    string ProfileClass,
    string Container,
    string VideoCodecReference,
    string AudioCodecReference,
    string VideoCodec,
    string AudioCodec,
    bool? Deinterlace);
