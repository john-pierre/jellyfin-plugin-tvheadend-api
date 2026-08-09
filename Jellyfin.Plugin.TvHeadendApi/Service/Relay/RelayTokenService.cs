// Relay token service — issues scoped tokens and manages their lifecycle.

using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// Issues scoped, short-lived relay tokens and coordinates cleanup.
/// Raw tokens are returned to the caller but never persisted — only HMAC hashes are stored.
/// </summary>
internal sealed class RelayTokenService : IRelayTokenService
{
    /// <summary>Scope prefix for the per-user, non-expiring reusable image relay token.</summary>
    private const string ImageTokenScopePrefix = "image-user:";

    /// <summary>Scope suffix used when no user id is available (anonymous requests).</summary>
    private const string AnonymousUserScope = "anonymous";

    private readonly ILogger<RelayTokenService> _logger;
    private readonly RelayTokenHasher _hasher;
    private readonly IRelayTokenRepository _repository;
    private readonly RelayTokenOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayTokenService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="hasher">Token hasher for HMAC computation.</param>
    /// <param name="repository">Token repository for persistence.</param>
    /// <param name="options">Effective token policy options.</param>
    public RelayTokenService(
        ILogger<RelayTokenService> logger,
        RelayTokenHasher hasher,
        IRelayTokenRepository repository,
        RelayTokenOptions options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task<string> IssueStreamTokenAsync(
        string channelId,
        string? userId,
        string? deviceId,
        string? profile,
        string? playbackMode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        var rawToken = RelayTokenHasher.GenerateRawToken();
        var tokenHash = _hasher.HashToken(rawToken);
        var now = DateTime.UtcNow;

        var record = new RelayTokenRecord
        {
            TokenHash = tokenHash,
            RelayType = "stream",
            ChannelId = channelId,
            UserId = userId,
            DeviceId = deviceId,
            SelectedProfile = profile,
            PlaybackMode = playbackMode,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(_options.StreamTtl),
            MaxUses = _options.StreamMaxUses > 0 ? _options.StreamMaxUses : null,
        };

        await _repository.InsertAsync(record, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "Issued stream relay token for channel {ChannelId}, TTL={TtlSeconds}s, maxUses={MaxUses}",
            channelId,
            _options.StreamTtlSeconds,
            _options.StreamMaxUses);

        return rawToken;
    }

    /// <inheritdoc />
    public async Task<string> IssueImageTokenAsync(
        string imageId,
        MediaKind? mediaKind,
        string? userId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageId);

        // When image tokens never expire (TTL 0 — the default), issue ONE stable token PER USER for
        // every image, rather than a row per image. Jellyfin persists image URLs permanently, so the
        // token must be stable; a never-expiring per-image token would grow the DB unbounded (it is
        // never cleaned up). One row per user keeps it bounded and allows per-user revocation.
        if (_options.ImageTokenNeverExpires)
        {
            return await EnsureReusableImageTokenAsync(userId, cancellationToken).ConfigureAwait(false);
        }

        // Expiring image tokens: issue a fresh per-image token. These are bounded by their TTL and
        // removed by the cleanup service, so they do not accumulate.
        var rawToken = RelayTokenHasher.GenerateRawToken();
        var now = DateTime.UtcNow;
        await _repository.InsertAsync(
            new RelayTokenRecord
            {
                TokenHash = _hasher.HashToken(rawToken),
                RelayType = "image",
                ImageId = imageId,
                MediaKind = mediaKind?.ToString(),
                UserId = userId,
                CreatedAtUtc = now,
                ExpiresAtUtc = now.Add(_options.ImageTtl),
                MaxUses = _options.ImageMaxUses > 0 ? _options.ImageMaxUses : null,
            },
            cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Issued expiring image relay token for image {ImageId} (TTL={TtlMinutes}min)", imageId, _options.ImageTtlMinutes);
        return rawToken;
    }

    /// <summary>
    /// Ensures the per-user, non-expiring reusable image token exists and returns its stable raw value.
    /// Idempotent and safe under concurrent first-issue: the unique index on <c>token_hash</c> keeps a
    /// single row per user, and a losing concurrent insert is harmless (the token is valid regardless).
    /// </summary>
    /// <param name="userId">The Jellyfin user id, or null for anonymous requests.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<string> EnsureReusableImageTokenAsync(string? userId, CancellationToken cancellationToken)
    {
        var rawToken = _hasher.DeriveDeterministicToken(ImageTokenScopePrefix + (userId ?? AnonymousUserScope));
        var tokenHash = _hasher.HashToken(rawToken);

        var existing = await _repository.FindByHashAsync(tokenHash, cancellationToken).ConfigureAwait(false);
        if (existing == null)
        {
            try
            {
                await _repository.InsertAsync(
                    new RelayTokenRecord
                    {
                        TokenHash = tokenHash,
                        RelayType = "image",
                        ImageId = null,           // null scope = valid for any image
                        MediaKind = null,
                        UserId = userId,          // one stable token per user
                        CreatedAtUtc = DateTime.UtcNow,
                        ExpiresAtUtc = DateTime.MaxValue,
                        MaxUses = null,           // unlimited
                    },
                    cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Created the reusable image relay token for user {UserId} (stable, non-expiring).", userId ?? "(anonymous)");
            }
            catch (Exception ex)
            {
                // Concurrent first-issue race (unique token_hash) or a transient DB error — the token
                // value is still valid and stable; a later request will retry persistence if needed.
                _logger.LogDebug(ex, "Reusable image token insert raced or failed; returning stable token anyway.");
            }
        }

        return rawToken;
    }

    /// <inheritdoc />
    public async Task AttachStreamTelemetryAsync(string rawToken, string? resolutionSource, string? mediaInfoCacheStatus, double? streamSetupMs, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);

        var tokenHash = _hasher.HashToken(rawToken);
        await _repository.UpdateStreamTelemetryAsync(tokenHash, resolutionSource, mediaInfoCacheStatus, streamSetupMs, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RevokeTokenAsync(string rawToken, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);

        var tokenHash = _hasher.HashToken(rawToken);
        var record = await _repository.FindByHashAsync(tokenHash, cancellationToken).ConfigureAwait(false);
        if (record == null)
        {
            _logger.LogDebug("Revocation requested for unknown token (hash prefix: {HashPrefix})", tokenHash[..8]);
            return;
        }

        await _repository.RevokeAsync(record.Id, reason, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Revoked relay token {TokenId} — reason: {Reason}", record.Id, reason);
    }

    /// <inheritdoc />
    public async Task<int> CleanupExpiredTokensAsync(CancellationToken cancellationToken)
    {
        // Expired tokens are worthless — validation always rejects them — so they are pruned
        // promptly. The short grace period (shared with the central cleanup) only covers clock
        // skew and inspecting very recent tokens while diagnosing an issue.
        var cutoff = DateTime.UtcNow - Database.DatabaseCleanupService.RelayTokenRetentionGrace;
        var count = await _repository.CleanupExpiredAsync(cutoff, cancellationToken).ConfigureAwait(false);
        if (count > 0)
        {
            _logger.LogInformation("Cleaned up {Count} expired/revoked relay tokens.", count);
        }

        return count;
    }
}
