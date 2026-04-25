// Entity Framework Core DbContext for relay token persistence.

using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// EF Core DbContext for relay tokens stored in the plugin SQLite database.
/// All table and column names use lowercase_with_underscore convention.
/// Schema creation is owned by <see cref="Database.DatabaseMigrationService"/>.
/// </summary>
internal sealed class RelayTokenDbContext : DbContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RelayTokenDbContext"/> class.
    /// </summary>
    /// <param name="options">Context options.</param>
    public RelayTokenDbContext(DbContextOptions<RelayTokenDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Gets or sets the relay tokens table.
    /// </summary>
    public DbSet<RelayTokenRecord> RelayTokens { get; set; }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var entity = modelBuilder.Entity<RelayTokenRecord>();
        entity.ToTable("relay_token");
        entity.HasKey(e => e.Id);

        // Column mappings — lowercase_with_underscore
        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.TokenHash).HasColumnName("token_hash").IsRequired().HasMaxLength(128);
        entity.Property(e => e.RelayType).HasColumnName("relay_type").IsRequired().HasMaxLength(16);
        entity.Property(e => e.ChannelId).HasColumnName("channel_id").HasMaxLength(256);
        entity.Property(e => e.ImageId).HasColumnName("image_id").HasMaxLength(512);
        entity.Property(e => e.MediaKind).HasColumnName("media_kind").HasMaxLength(32);
        entity.Property(e => e.UserId).HasColumnName("user_id").HasMaxLength(256);
        entity.Property(e => e.DeviceId).HasColumnName("device_id").HasMaxLength(256);
        entity.Property(e => e.PlaybackMode).HasColumnName("playback_mode").HasMaxLength(32);
        entity.Property(e => e.SelectedProfile).HasColumnName("selected_profile").HasMaxLength(128);
        entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
        entity.Property(e => e.ExpiresAtUtc).HasColumnName("expires_at_utc");
        entity.Property(e => e.FirstUsedAtUtc).HasColumnName("first_used_at_utc");
        entity.Property(e => e.LastUsedAtUtc).HasColumnName("last_used_at_utc");
        entity.Property(e => e.UseCount).HasColumnName("use_count");
        entity.Property(e => e.MaxUses).HasColumnName("max_uses");
        entity.Property(e => e.Revoked).HasColumnName("revoked");
        entity.Property(e => e.RevokedAtUtc).HasColumnName("revoked_at_utc");
        entity.Property(e => e.RevokedReason).HasColumnName("revoked_reason").HasMaxLength(256);
        entity.Property(e => e.LastValidationResult).HasColumnName("last_validation_result").HasMaxLength(32);
        entity.Property(e => e.LastValidationFailureReason).HasColumnName("last_validation_failure_reason").HasMaxLength(64);

        // Indexes
        entity.HasIndex(e => e.TokenHash).IsUnique().HasDatabaseName("ix_relay_token_token_hash");
        entity.HasIndex(e => e.ExpiresAtUtc).HasDatabaseName("ix_relay_token_expires_at_utc");
        entity.HasIndex(e => e.RelayType).HasDatabaseName("ix_relay_token_relay_type");
        entity.HasIndex(e => new { e.RelayType, e.ChannelId }).HasDatabaseName("ix_relay_token_type_channel");
        entity.HasIndex(e => new { e.RelayType, e.ImageId }).HasDatabaseName("ix_relay_token_type_image");
    }
}
