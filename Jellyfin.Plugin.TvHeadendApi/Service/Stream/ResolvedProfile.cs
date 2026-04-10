using System.Collections.Generic;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Stream;

internal sealed record ResolvedProfile(
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
