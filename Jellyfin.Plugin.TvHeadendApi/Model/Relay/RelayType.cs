// Relay type classification for metrics tracking.

namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// Classifies the type of relay request.
/// </summary>
public enum RelayType
{
    /// <summary>Image relay (logo, EPG image, thumbnail).</summary>
    Image,

    /// <summary>Live TV stream relay.</summary>
    Stream,
}
