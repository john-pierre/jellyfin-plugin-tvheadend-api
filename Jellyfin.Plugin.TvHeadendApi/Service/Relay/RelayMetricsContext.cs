// Entity Framework Core DbContext for relay request metrics persistence.

using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// EF Core DbContext for relay metrics stored in the statistics SQLite database.
/// </summary>
internal sealed class RelayMetricsContext : DbContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RelayMetricsContext"/> class.
    /// </summary>
    /// <param name="options">Context options.</param>
    public RelayMetricsContext(DbContextOptions<RelayMetricsContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Gets or sets the relay request metrics table.
    /// </summary>
    public DbSet<RelayRequestMetric> RelayRequestMetrics { get; set; }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var entity = modelBuilder.Entity<RelayRequestMetric>();
        entity.ToTable("relay_request_metrics");
        entity.HasKey(e => e.Id);

        // Indexes for common dashboard queries
        entity.HasIndex(e => e.CreatedAtUtc).HasDatabaseName("IX_relay_metrics_created_at");
        entity.HasIndex(e => e.RelayType).HasDatabaseName("IX_relay_metrics_relay_type");
        entity.HasIndex(e => e.MediaKind).HasDatabaseName("IX_relay_metrics_media_kind");
        entity.HasIndex(e => e.FailureReason).HasDatabaseName("IX_relay_metrics_failure_reason");
        entity.HasIndex(e => e.FinalOutcome).HasDatabaseName("IX_relay_metrics_final_outcome");
        entity.HasIndex(e => e.ChannelId).HasDatabaseName("IX_relay_metrics_channel_id");
        entity.HasIndex(e => new { e.CreatedAtUtc, e.RelayType }).HasDatabaseName("IX_relay_metrics_created_type");
        entity.HasIndex(e => new { e.CreatedAtUtc, e.FailureReason }).HasDatabaseName("IX_relay_metrics_created_failure");

        // Column config
        entity.Property(e => e.RelayType).IsRequired().HasMaxLength(16);
        entity.Property(e => e.MediaKind).IsRequired().HasMaxLength(32);
        entity.Property(e => e.FinalOutcome).IsRequired().HasMaxLength(16);
        entity.Property(e => e.FailureReason).IsRequired().HasMaxLength(32);
        entity.Property(e => e.CacheStatus).IsRequired().HasMaxLength(24);
        entity.Property(e => e.RequestMethod).IsRequired().HasMaxLength(8);
        entity.Property(e => e.ImageSourceType).HasMaxLength(32);
        entity.Property(e => e.ChannelId).HasMaxLength(256);
        entity.Property(e => e.ContentType).HasMaxLength(128);
        entity.Property(e => e.EndedBy).HasMaxLength(32);
    }
}
