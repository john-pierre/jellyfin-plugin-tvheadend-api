// EF Core entity representing a relay token stored in the relay_tokens SQLite table.

using System;

namespace Jellyfin.Plugin.TvHeadendApi.Model.Relay;

/// <summary>
/// Persistent record for a relay token. Only the HMAC hash of the raw token is stored — never the raw token itself.
/// </summary>
public sealed class RelayTokenRecord
{
    /// <summary>Gets or sets the auto-increment primary key.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the HMAC-SHA256 hash of the raw token (hex-encoded). Never the raw token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Gets or sets the relay type (<c>stream</c> or <c>image</c>).</summary>
    public string RelayType { get; set; } = string.Empty;

    /// <summary>Gets or sets the TVHeadend channel UUID (stream tokens).</summary>
    public string? ChannelId { get; set; }

    /// <summary>Gets or sets the image resource identifier (image tokens).</summary>
    public string? ImageId { get; set; }

    /// <summary>Gets or sets the media kind (logo, epg_image, thumbnail).</summary>
    public string? MediaKind { get; set; }

    /// <summary>Gets or sets the Jellyfin user ID this token was issued for.</summary>
    public string? UserId { get; set; }

    /// <summary>Gets or sets the device or client identifier.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Gets or sets the playback mode.</summary>
    public string? PlaybackMode { get; set; }

    /// <summary>Gets or sets the selected streaming profile.</summary>
    public string? SelectedProfile { get; set; }

    /// <summary>Gets or sets the UTC timestamp when the token was created.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Gets or sets the UTC timestamp when the token expires.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Gets or sets the UTC timestamp when the token was first used.</summary>
    public DateTime? FirstUsedAtUtc { get; set; }

    /// <summary>Gets or sets the UTC timestamp when the token was last used.</summary>
    public DateTime? LastUsedAtUtc { get; set; }

    /// <summary>Gets or sets the number of times this token has been used.</summary>
    public int UseCount { get; set; }

    /// <summary>Gets or sets the maximum number of times this token may be used. Null or 0 = unlimited.</summary>
    public int? MaxUses { get; set; }

    /// <summary>Gets or sets a value indicating whether this token has been revoked.</summary>
    public bool Revoked { get; set; }

    /// <summary>Gets or sets the UTC timestamp when the token was revoked.</summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>Gets or sets the reason the token was revoked.</summary>
    public string? RevokedReason { get; set; }

    /// <summary>Gets or sets the last validation result (e.g. <c>valid</c>, <c>rejected</c>).</summary>
    public string? LastValidationResult { get; set; }

    /// <summary>Gets or sets the last validation failure reason if rejected.</summary>
    public string? LastValidationFailureReason { get; set; }
}
