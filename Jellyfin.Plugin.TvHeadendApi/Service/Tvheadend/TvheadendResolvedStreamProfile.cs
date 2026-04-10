using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Tvheadend;

internal sealed record TvheadendResolvedStreamProfile(
    string Key,
    string Name,
    string ProfileClass,
    string Container,
    string RawContainer,
    string ProVideoCodec,
    string ProAudioCodec,
    string ResolvedVideoCodec,
    string ResolvedAudioCodec,
    IReadOnlyList<string> SrcVideoCodecs,
    IReadOnlyList<string> SrcAudioCodecs,
    bool? ProfileDeinterlace,
    bool? VideoCodecDeinterlace);
