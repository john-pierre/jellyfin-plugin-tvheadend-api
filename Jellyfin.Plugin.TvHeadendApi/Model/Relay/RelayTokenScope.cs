// Immutable value object describing the scope of a relay token.

namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// Describes the scope constraints of a relay token.
/// A stream token is scoped to a specific channel; an image token is scoped to a specific image resource.
/// </summary>
public sealed class RelayTokenScope
{
    /// <summary>Gets the relay type this token is valid for.</summary>
    public RelayType RelayType { get; init; }

    /// <summary>Gets the TVHeadend channel UUID (stream tokens only).</summary>
    public string? ChannelId { get; init; }

    /// <summary>Gets the image resource identifier (image tokens only).</summary>
    public string? ImageId { get; init; }

    /// <summary>Gets the media kind constraint (image tokens only).</summary>
    public MediaKind? MediaKind { get; init; }

    /// <summary>Gets the optional Jellyfin user ID this token was issued for.</summary>
    public string? UserId { get; init; }

    /// <summary>Gets the optional device or client identifier.</summary>
    public string? DeviceId { get; init; }

    /// <summary>Gets the optional streaming profile name.</summary>
    public string? SelectedProfile { get; init; }

    /// <summary>Gets the optional playback mode.</summary>
    public string? PlaybackMode { get; init; }
}
