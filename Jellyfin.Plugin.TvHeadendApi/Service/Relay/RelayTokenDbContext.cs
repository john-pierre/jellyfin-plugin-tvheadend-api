// Entity Framework Core DbContext for relay token persistence.

using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// EF Core DbContext for relay tokens stored in the statistics SQLite database.
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
        entity.ToTable("relay_tokens");
        entity.HasKey(e => e.Id);

        // Unique index on token hash for fast lookups
        entity.HasIndex(e => e.TokenHash).IsUnique().HasDatabaseName("IX_relay_tokens_token_hash");
        entity.HasIndex(e => e.ExpiresAtUtc).HasDatabaseName("IX_relay_tokens_expires_at");
        entity.HasIndex(e => e.RelayType).HasDatabaseName("IX_relay_tokens_relay_type");
        entity.HasIndex(e => new { e.RelayType, e.ChannelId }).HasDatabaseName("IX_relay_tokens_type_channel");
        entity.HasIndex(e => new { e.RelayType, e.ImageId }).HasDatabaseName("IX_relay_tokens_type_image");

        // Column constraints
        entity.Property(e => e.TokenHash).IsRequired().HasMaxLength(128);
        entity.Property(e => e.RelayType).IsRequired().HasMaxLength(16);
        entity.Property(e => e.ChannelId).HasMaxLength(256);
        entity.Property(e => e.ImageId).HasMaxLength(512);
        entity.Property(e => e.MediaKind).HasMaxLength(32);
        entity.Property(e => e.UserId).HasMaxLength(256);
        entity.Property(e => e.DeviceId).HasMaxLength(256);
        entity.Property(e => e.PlaybackMode).HasMaxLength(32);
        entity.Property(e => e.SelectedProfile).HasMaxLength(128);
        entity.Property(e => e.RevokedReason).HasMaxLength(256);
        entity.Property(e => e.LastValidationResult).HasMaxLength(32);
        entity.Property(e => e.LastValidationFailureReason).HasMaxLength(64);
    }

    /// <summary>
    /// Creates the relay_tokens table and indexes using raw SQL if they do not already exist.
    /// Called during schema initialization to ensure the table exists even if EF migrations are not used.
    /// </summary>
    /// <param name="db">The database context.</param>
    internal static void ApplySchemaIfMissing(RelayTokenDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS "relay_tokens" (
                "Id"                            INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                "TokenHash"                     TEXT NOT NULL,
                "RelayType"                     TEXT NOT NULL,
                "ChannelId"                     TEXT NULL,
                "ImageId"                       TEXT NULL,
                "MediaKind"                     TEXT NULL,
                "UserId"                        TEXT NULL,
                "DeviceId"                      TEXT NULL,
                "PlaybackMode"                  TEXT NULL,
                "SelectedProfile"               TEXT NULL,
                "CreatedAtUtc"                  TEXT NOT NULL,
                "ExpiresAtUtc"                  TEXT NOT NULL,
                "FirstUsedAtUtc"                TEXT NULL,
                "LastUsedAtUtc"                 TEXT NULL,
                "UseCount"                      INTEGER NOT NULL DEFAULT 0,
                "MaxUses"                       INTEGER NULL,
                "Revoked"                       INTEGER NOT NULL DEFAULT 0,
                "RevokedAtUtc"                  TEXT NULL,
                "RevokedReason"                 TEXT NULL,
                "LastValidationResult"          TEXT NULL,
                "LastValidationFailureReason"   TEXT NULL
            )
            """);

        db.Database.ExecuteSqlRaw("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_relay_tokens_token_hash" ON "relay_tokens" ("TokenHash")""");
        db.Database.ExecuteSqlRaw("""CREATE INDEX IF NOT EXISTS "IX_relay_tokens_expires_at" ON "relay_tokens" ("ExpiresAtUtc")""");
        db.Database.ExecuteSqlRaw("""CREATE INDEX IF NOT EXISTS "IX_relay_tokens_relay_type" ON "relay_tokens" ("RelayType")""");
        db.Database.ExecuteSqlRaw("""CREATE INDEX IF NOT EXISTS "IX_relay_tokens_type_channel" ON "relay_tokens" ("RelayType", "ChannelId")""");
        db.Database.ExecuteSqlRaw("""CREATE INDEX IF NOT EXISTS "IX_relay_tokens_type_image" ON "relay_tokens" ("RelayType", "ImageId")""");
    }
}
