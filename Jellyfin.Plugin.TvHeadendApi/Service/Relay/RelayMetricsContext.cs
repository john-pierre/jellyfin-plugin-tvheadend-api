// Entity Framework Core DbContext for relay request metrics persistence.

using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Relay;

/// <summary>
/// EF Core DbContext for relay metrics stored in the plugin SQLite database.
/// All table and column names use lowercase_with_underscore convention.
/// Schema creation is owned by <see cref="Database.DatabaseMigrationService"/>.
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
        entity.ToTable("relay_request_metric");
        entity.HasKey(e => e.Id);

        // Column mappings — lowercase_with_underscore
        entity.Property(e => e.Id).HasColumnName("id");
        entity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
        entity.Property(e => e.RelayType).HasColumnName("relay_type").IsRequired().HasMaxLength(16);
        entity.Property(e => e.MediaKind).HasColumnName("media_kind").IsRequired().HasMaxLength(32);
        entity.Property(e => e.ImageSourceType).HasColumnName("image_source_type").HasMaxLength(32);
        entity.Property(e => e.ChannelId).HasColumnName("channel_id").HasMaxLength(256);
        entity.Property(e => e.SessionId).HasColumnName("session_id").HasMaxLength(32);
        entity.Property(e => e.ChannelName).HasColumnName("channel_name").HasMaxLength(256);
        entity.Property(e => e.ClientName).HasColumnName("client_name").HasMaxLength(64);
        entity.Property(e => e.UserAgent).HasColumnName("user_agent").HasMaxLength(256);
        entity.Property(e => e.TotalDurationMs).HasColumnName("total_duration_ms");
        entity.Property(e => e.UpstreamHeadersDurationMs).HasColumnName("upstream_headers_duration_ms");
        entity.Property(e => e.FirstByteFromUpstreamDurationMs).HasColumnName("first_byte_from_upstream_duration_ms");
        entity.Property(e => e.FirstByteToClientDurationMs).HasColumnName("first_byte_to_client_duration_ms");
        entity.Property(e => e.StartupLatencyMs).HasColumnName("startup_latency_ms");
        entity.Property(e => e.BytesSent).HasColumnName("bytes_sent");
        entity.Property(e => e.AverageBytesPerSecond).HasColumnName("average_bytes_per_second");
        entity.Property(e => e.PeakBitrate).HasColumnName("peak_bitrate");
        entity.Property(e => e.UpstreamStatusCode).HasColumnName("upstream_status_code");
        entity.Property(e => e.ClientStatusCode).HasColumnName("client_status_code");
        entity.Property(e => e.FinalOutcome).HasColumnName("final_outcome").IsRequired().HasMaxLength(16);
        entity.Property(e => e.FailureReason).HasColumnName("failure_reason").IsRequired().HasMaxLength(32);
        entity.Property(e => e.ClientCancelled).HasColumnName("client_cancelled");
        entity.Property(e => e.UpstreamTimedOut).HasColumnName("upstream_timed_out");
        entity.Property(e => e.StreamFinalOutcome).HasColumnName("stream_final_outcome").HasMaxLength(32);
        entity.Property(e => e.NormalDisconnect).HasColumnName("normal_disconnect");
        entity.Property(e => e.CacheStatus).HasColumnName("cache_status").IsRequired().HasMaxLength(24);
        entity.Property(e => e.CacheLookupDurationMs).HasColumnName("cache_lookup_duration_ms");
        entity.Property(e => e.HadEtag).HasColumnName("had_etag");
        entity.Property(e => e.HadLastModified).HasColumnName("had_last_modified");
        entity.Property(e => e.WasNotModified304).HasColumnName("was_not_modified_304");
        entity.Property(e => e.WasRangeRequest).HasColumnName("was_range_request");
        entity.Property(e => e.HasContentLength).HasColumnName("has_content_length");
        entity.Property(e => e.ContentLength).HasColumnName("content_length");
        entity.Property(e => e.ContentType).HasColumnName("content_type").HasMaxLength(128);
        entity.Property(e => e.RequestMethod).HasColumnName("request_method").IsRequired().HasMaxLength(8);
        entity.Property(e => e.EndedBy).HasColumnName("ended_by").HasMaxLength(32);
        entity.Property(e => e.StartupFailedWithin5Seconds).HasColumnName("startup_failed_within_5_seconds");
        entity.Property(e => e.ParallelActiveStreamCountAtStart).HasColumnName("parallel_active_stream_count_at_start");
        entity.Property(e => e.EffectiveProfile).HasColumnName("effective_profile").HasMaxLength(128);
        entity.Property(e => e.ResolutionSource).HasColumnName("resolution_source").HasMaxLength(32);
        entity.Property(e => e.MediaInfoCacheStatus).HasColumnName("mediainfo_cache_status").HasMaxLength(16);
        entity.Property(e => e.StreamSetupMs).HasColumnName("stream_setup_ms");

        // Indexes
        entity.HasIndex(e => e.CreatedAtUtc).HasDatabaseName("ix_relay_request_metric_created_at_utc");
        entity.HasIndex(e => e.RelayType).HasDatabaseName("ix_relay_request_metric_relay_type");
        entity.HasIndex(e => e.MediaKind).HasDatabaseName("ix_relay_request_metric_media_kind");
        entity.HasIndex(e => e.FailureReason).HasDatabaseName("ix_relay_request_metric_failure_reason");
        entity.HasIndex(e => e.FinalOutcome).HasDatabaseName("ix_relay_request_metric_final_outcome");
        entity.HasIndex(e => e.ChannelId).HasDatabaseName("ix_relay_request_metric_channel_id");
        entity.HasIndex(e => new { e.CreatedAtUtc, e.RelayType }).HasDatabaseName("ix_relay_request_metric_created_type");
        entity.HasIndex(e => new { e.CreatedAtUtc, e.FailureReason }).HasDatabaseName("ix_relay_request_metric_created_failure");
    }
}
