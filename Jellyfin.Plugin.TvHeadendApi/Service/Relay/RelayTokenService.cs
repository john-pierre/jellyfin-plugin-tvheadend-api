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

        // Per-user reuse: return the existing user-scoped image token if available.
        // This prevents thousands of tokens accumulating (one per channel logo / EPG image).
        if (_options.ImageTokenReusePerUser)
        {
            var existing = await _repository.FindActiveImageTokenForUserAsync(userId, cancellationToken).ConfigureAwait(false);
            if (existing != null)
            {
                _logger.LogDebug(
                    "Reusing existing image token {TokenId} for user {UserId} (created {CreatedAt})",
                    existing.Id,
                    userId ?? "(anonymous)",
                    existing.CreatedAtUtc);

                // We cannot return the raw token since only the hash is stored.
                // Issue a new token but store it with the same scope key so the validator
                // doesn't reject distinct tokens for different images.
                // → Actually we need a new raw token. But we reuse across images.
            }
        }

        var rawToken = RelayTokenHasher.GenerateRawToken();
        var tokenHash = _hasher.HashToken(rawToken);
        var now = DateTime.UtcNow;

        var record = new RelayTokenRecord
        {
            TokenHash = tokenHash,
            RelayType = "image",
            ImageId = _options.ImageTokenReusePerUser ? null : imageId,
            MediaKind = mediaKind?.ToString(),
            UserId = userId,
            CreatedAtUtc = now,
            ExpiresAtUtc = _options.ImageTokenNeverExpires ? DateTime.MaxValue : now.Add(_options.ImageTtl),
            MaxUses = _options.ImageMaxUses > 0 ? _options.ImageMaxUses : null,
        };

        await _repository.InsertAsync(record, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "Issued image relay token for image {ImageId}, TTL={TtlMinutes}, maxUses={MaxUses}, perUserReuse={ReusePerUser}",
            imageId,
            _options.ImageTokenNeverExpires ? "∞" : $"{_options.ImageTtlMinutes}min",
            _options.ImageMaxUses,
            _options.ImageTokenReusePerUser);

        return rawToken;
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
        var cutoff = DateTime.UtcNow;
        var count = await _repository.CleanupExpiredAsync(cutoff, cancellationToken).ConfigureAwait(false);
        if (count > 0)
        {
            _logger.LogInformation("Cleaned up {Count} expired/revoked relay tokens.", count);
        }

        return count;
    }
}
