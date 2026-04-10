using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Profile;

internal sealed record ProfileDetails(
    string Key,
    string Name,
    string ProfileClass,
    string Container,
    string RawContainer,
    string ProVideoCodec,
    string ProAudioCodec,
    IReadOnlyList<string> SrcVideoCodecs,
    IReadOnlyList<string> SrcAudioCodecs,
    bool? Deinterlace);
