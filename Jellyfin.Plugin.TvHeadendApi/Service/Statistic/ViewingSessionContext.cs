using Jellyfin.Plugin.TvHeadendApi.Model.Statistic;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Statistic;

/// <summary>
/// Entity Framework Core DbContext for viewing statistics, health transitions,
/// TVHeadend log entries, and unified plugin logs.
/// All table and column names use lowercase_with_underscore convention.
/// Schema creation is owned by <see cref="Database.DatabaseMigrationService"/> —
/// this context only defines EF mappings for runtime queries and writes.
/// </summary>
internal sealed class ViewingSessionContext : DbContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ViewingSessionContext"/> class.
    /// </summary>
    /// <param name="options">Context options.</param>
    public ViewingSessionContext(DbContextOptions<ViewingSessionContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Gets or sets the viewing sessions table.
    /// </summary>
    public DbSet<ViewingSession> ViewingSessions { get; set; }

    /// <summary>
    /// Gets or sets the health transition history table.
    /// </summary>
    public DbSet<HealthTransition> HealthTransitions { get; set; }

    /// <summary>
    /// Gets or sets the TVHeadend log entries table.
    /// </summary>
    public DbSet<TvheadendLogEntry> TvheadendLogEntries { get; set; }

    /// <summary>
    /// Gets or sets the unified plugin log entries table.
    /// </summary>
    public DbSet<PluginLogEntry> PluginLogEntries { get; set; }

    /// <summary>
    /// Configures the model for the context using lowercase_with_underscore naming.
    /// </summary>
    /// <param name="modelBuilder">Model builder.</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Viewing Session ──────────────────────────────────────────
        var sessionEntity = modelBuilder.Entity<ViewingSession>();
        sessionEntity.ToTable("viewing_session");
        sessionEntity.HasKey(s => s.Id);
        sessionEntity.Property(s => s.Id).HasColumnName("id");
        sessionEntity.Property(s => s.UserName).HasColumnName("user_name").IsRequired().HasMaxLength(256);
        sessionEntity.Property(s => s.DeviceName).HasColumnName("device_name").IsRequired().HasMaxLength(256);
        sessionEntity.Property(s => s.ClientName).HasColumnName("client_name").IsRequired().HasMaxLength(256);
        sessionEntity.Property(s => s.ChannelName).HasColumnName("channel_name").IsRequired().HasMaxLength(512);
        sessionEntity.Property(s => s.ChannelId).HasColumnName("channel_id").IsRequired().HasMaxLength(256);
        sessionEntity.Property(s => s.PlayMethod).HasColumnName("play_method").IsRequired().HasMaxLength(64);
        sessionEntity.Property(s => s.PlaySessionId).HasColumnName("play_session_id").IsRequired().HasMaxLength(256);
        sessionEntity.Property(s => s.StartTimeUtc).HasColumnName("start_time_utc");
        sessionEntity.Property(s => s.EndTimeUtc).HasColumnName("end_time_utc");

        sessionEntity.HasIndex(s => new { s.UserName, s.DeviceName, s.ClientName, s.ChannelId, s.PlaySessionId })
            .IsUnique()
            .HasDatabaseName("ix_viewing_session_composite");
        sessionEntity.HasIndex(s => s.UserName).HasDatabaseName("ix_viewing_session_user_name");
        sessionEntity.HasIndex(s => s.DeviceName).HasDatabaseName("ix_viewing_session_device_name");
        sessionEntity.HasIndex(s => s.StartTimeUtc).HasDatabaseName("ix_viewing_session_start_time_utc");
        sessionEntity.HasIndex(s => s.EndTimeUtc).HasDatabaseName("ix_viewing_session_end_time_utc");

        // ── Health Transition ────────────────────────────────────────
        var healthEntity = modelBuilder.Entity<HealthTransition>();
        healthEntity.ToTable("health_transition");
        healthEntity.HasKey(h => h.Id);
        healthEntity.Property(h => h.Id).HasColumnName("id");
        healthEntity.Property(h => h.TimestampUtc).HasColumnName("timestamp_utc");
        healthEntity.Property(h => h.FromStatus).HasColumnName("from_status").IsRequired().HasMaxLength(64);
        healthEntity.Property(h => h.ToStatus).HasColumnName("to_status").IsRequired().HasMaxLength(64);
        healthEntity.Property(h => h.FailureReason).HasColumnName("failure_reason").HasMaxLength(64);
        healthEntity.Property(h => h.ResponseTimeMs).HasColumnName("response_time_ms");
        healthEntity.Property(h => h.ConsecutiveFailures).HasColumnName("consecutive_failures");
        healthEntity.HasIndex(h => h.TimestampUtc).HasDatabaseName("ix_health_transition_timestamp_utc");

        // ── TVHeadend Log Entries ────────────────────────────────────
        var logEntity = modelBuilder.Entity<TvheadendLogEntry>();
        logEntity.ToTable("tvheadend_log_entry");
        logEntity.HasKey(l => l.Id);
        logEntity.Property(l => l.Id).HasColumnName("id");
        logEntity.Property(l => l.TimestampUtc).HasColumnName("timestamp_utc");
        logEntity.Property(l => l.Text).HasColumnName("text").IsRequired().HasMaxLength(2048);
        logEntity.HasIndex(l => l.TimestampUtc).HasDatabaseName("ix_tvheadend_log_entry_timestamp_utc");

        // ── Plugin Log Entries ───────────────────────────────────────
        var pluginLogEntity = modelBuilder.Entity<PluginLogEntry>();
        pluginLogEntity.ToTable("plugin_log_entry");
        pluginLogEntity.HasKey(e => e.Id);
        pluginLogEntity.Property(e => e.Id).HasColumnName("id");
        pluginLogEntity.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc");
        pluginLogEntity.Property(e => e.Source).HasColumnName("source").IsRequired().HasMaxLength(16);
        pluginLogEntity.Property(e => e.LogType).HasColumnName("log_type").IsRequired().HasMaxLength(32);
        pluginLogEntity.Property(e => e.Level).HasColumnName("level").IsRequired().HasMaxLength(16);
        pluginLogEntity.Property(e => e.Category).HasColumnName("category").HasMaxLength(256);
        pluginLogEntity.Property(e => e.Message).HasColumnName("message").IsRequired().HasMaxLength(4096);
        pluginLogEntity.Property(e => e.Exception).HasColumnName("exception").HasMaxLength(8192);
        pluginLogEntity.Property(e => e.EventId).HasColumnName("event_id").HasMaxLength(64);
        pluginLogEntity.Property(e => e.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
        pluginLogEntity.Property(e => e.ChannelId).HasColumnName("channel_id").HasMaxLength(256);
        pluginLogEntity.Property(e => e.RawSource).HasColumnName("raw_source").HasMaxLength(4096);
        pluginLogEntity.Property(e => e.RawLineHash).HasColumnName("raw_line_hash").HasMaxLength(64);
        pluginLogEntity.Property(e => e.ImportedAtUtc).HasColumnName("imported_at_utc");

        pluginLogEntity.HasIndex(e => e.CreatedAtUtc).HasDatabaseName("ix_plugin_log_entry_created_at_utc");
        pluginLogEntity.HasIndex(e => e.Source).HasDatabaseName("ix_plugin_log_entry_source");
        pluginLogEntity.HasIndex(e => e.Level).HasDatabaseName("ix_plugin_log_entry_level");
        pluginLogEntity.HasIndex(e => e.LogType).HasDatabaseName("ix_plugin_log_entry_log_type");
        pluginLogEntity.HasIndex(e => e.Category).HasDatabaseName("ix_plugin_log_entry_category");
        pluginLogEntity.HasIndex(e => new { e.Source, e.CreatedAtUtc }).HasDatabaseName("ix_plugin_log_entry_source_created");
        pluginLogEntity.HasIndex(e => new { e.Level, e.CreatedAtUtc }).HasDatabaseName("ix_plugin_log_entry_level_created");
        pluginLogEntity.HasIndex(e => e.RawLineHash).HasDatabaseName("ix_plugin_log_entry_raw_line_hash");
    }
}
