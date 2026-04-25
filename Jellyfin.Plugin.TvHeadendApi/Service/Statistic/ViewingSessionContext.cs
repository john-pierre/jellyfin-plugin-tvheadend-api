using Jellyfin.Plugin.TvHeadendApi.Model.Statistic;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Statistic;

/// <summary>
/// Entity Framework Core DbContext for viewing statistics persistence.
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
    /// Gets or sets the TVHeadend log entries table (legacy).
    /// </summary>
    public DbSet<TvhLogEntry> TvhLogEntries { get; set; }

    /// <summary>
    /// Gets or sets the unified plugin log entries table.
    /// </summary>
    public DbSet<PluginLogEntry> PluginLogEntries { get; set; }

    /// <summary>
    /// Configures the model for the context.
    /// </summary>
    /// <param name="modelBuilder">Model builder.</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var sessionEntity = modelBuilder.Entity<ViewingSession>();

        // Composite unique constraint: ensures only one active session per user+device+client+channel+playSessionId
        sessionEntity.HasKey(s => s.Id);
        sessionEntity.HasIndex(s => new { s.UserName, s.DeviceName, s.ClientName, s.ChannelId, s.PlaySessionId })
            .IsUnique()
            .HasDatabaseName("IX_ViewingSession_Composite");

        // Additional indexes for querying
        sessionEntity.HasIndex(s => s.UserName);
        sessionEntity.HasIndex(s => s.DeviceName);
        sessionEntity.HasIndex(s => s.StartTimeUtc);
        sessionEntity.HasIndex(s => s.EndTimeUtc);

        // Configure properties
        sessionEntity.Property(s => s.UserName).IsRequired().HasMaxLength(256);
        sessionEntity.Property(s => s.DeviceName).IsRequired().HasMaxLength(256);
        sessionEntity.Property(s => s.ClientName).IsRequired().HasMaxLength(256);
        sessionEntity.Property(s => s.ChannelName).IsRequired().HasMaxLength(512);
        sessionEntity.Property(s => s.ChannelId).IsRequired().HasMaxLength(256);
        sessionEntity.Property(s => s.PlaySessionId).IsRequired().HasMaxLength(256);
        sessionEntity.Property(s => s.PlayMethod).IsRequired().HasMaxLength(64);

        // Health transition history
        var healthEntity = modelBuilder.Entity<HealthTransition>();
        healthEntity.HasKey(h => h.Id);
        healthEntity.HasIndex(h => h.TimestampUtc);
        healthEntity.Property(h => h.FromStatus).IsRequired().HasMaxLength(64);
        healthEntity.Property(h => h.ToStatus).IsRequired().HasMaxLength(64);
        healthEntity.Property(h => h.FailureReason).HasMaxLength(64);

        // TVHeadend log entries (legacy)
        var logEntity = modelBuilder.Entity<TvhLogEntry>();
        logEntity.HasKey(l => l.Id);
        logEntity.HasIndex(l => l.TimestampUtc);
        logEntity.Property(l => l.Text).IsRequired().HasMaxLength(2048);

        // Unified plugin log entries
        var pluginLogEntity = modelBuilder.Entity<PluginLogEntry>();
        pluginLogEntity.ToTable("plugin_log_entries");
        pluginLogEntity.HasKey(e => e.Id);

        // Indexes for efficient dashboard queries
        pluginLogEntity.HasIndex(e => e.CreatedAtUtc).HasDatabaseName("IX_plugin_log_created_at");
        pluginLogEntity.HasIndex(e => e.Source).HasDatabaseName("IX_plugin_log_source");
        pluginLogEntity.HasIndex(e => e.Level).HasDatabaseName("IX_plugin_log_level");
        pluginLogEntity.HasIndex(e => e.LogType).HasDatabaseName("IX_plugin_log_type");
        pluginLogEntity.HasIndex(e => e.Category).HasDatabaseName("IX_plugin_log_category");
        pluginLogEntity.HasIndex(e => new { e.Source, e.CreatedAtUtc }).HasDatabaseName("IX_plugin_log_source_created");
        pluginLogEntity.HasIndex(e => new { e.Level, e.CreatedAtUtc }).HasDatabaseName("IX_plugin_log_level_created");
        pluginLogEntity.HasIndex(e => e.RawLineHash).HasDatabaseName("IX_plugin_log_raw_hash");

        pluginLogEntity.Property(e => e.Source).IsRequired().HasMaxLength(16);
        pluginLogEntity.Property(e => e.LogType).IsRequired().HasMaxLength(32);
        pluginLogEntity.Property(e => e.Level).IsRequired().HasMaxLength(16);
        pluginLogEntity.Property(e => e.Category).HasMaxLength(256);
        pluginLogEntity.Property(e => e.Message).IsRequired().HasMaxLength(4096);
        pluginLogEntity.Property(e => e.Exception).HasMaxLength(8192);
        pluginLogEntity.Property(e => e.EventId).HasMaxLength(64);
        pluginLogEntity.Property(e => e.CorrelationId).HasMaxLength(64);
        pluginLogEntity.Property(e => e.ChannelId).HasMaxLength(256);
        pluginLogEntity.Property(e => e.RawSource).HasMaxLength(4096);
        pluginLogEntity.Property(e => e.RawLineHash).HasMaxLength(64);
    }
}
