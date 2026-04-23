using Jellyfin.Plugin.TvHeadendApi.Model.Statistics;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Statistics;

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
    /// Gets or sets the TVHeadend log entries table.
    /// </summary>
    public DbSet<TvhLogEntry> TvhLogEntries { get; set; }

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

        // TVHeadend log entries
        var logEntity = modelBuilder.Entity<TvhLogEntry>();
        logEntity.HasKey(l => l.Id);
        logEntity.HasIndex(l => l.TimestampUtc);
        logEntity.Property(l => l.Text).IsRequired().HasMaxLength(2048);
    }
}
