// Schema tests for the stream-telemetry consolidation migrations (010 and 011).

using System;
using System.IO;
using Jellyfin.Plugin.TvHeadendApi.Service.Database;
using Jellyfin.Plugin.TvHeadendApi.Service.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Database;

/// <summary>
/// Verifies migration 010 (consolidate stream telemetry into <c>relay_request_metric</c>,
/// drop the parallel session tables) and migration 011 (relay token telemetry columns).
/// </summary>
public sealed class TelemetryMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly DatabaseMigrationService _migrationService;

    public TelemetryMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tvh_mig_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var pathProvider = new DataFolderPathProvider(() => _tempDir);
        var provider = new DatabaseProvider(pathProvider);
        _connectionFactory = new DatabaseConnectionFactory(provider);
        _migrationService = new DatabaseMigrationService(_connectionFactory, NullLogger<DatabaseMigrationService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void Migration010_DropsParallelStreamTelemetryTables()
    {
        using var conn = _connectionFactory.CreateConnection();
        _migrationService.RunMigrations(conn);

        Assert.False(DatabaseMigrationService.TableExists(conn, "active_stream_sessions"));
        Assert.False(DatabaseMigrationService.TableExists(conn, "completed_stream_sessions"));
        Assert.False(DatabaseMigrationService.TableExists(conn, "relay_events"));
    }

    [Fact]
    public void Migration010_ExtendsRelayRequestMetric_WithSessionAndZappingColumns()
    {
        using var conn = _connectionFactory.CreateConnection();
        _migrationService.RunMigrations(conn);

        foreach (var column in new[]
                 {
                     "session_id", "channel_name", "client_name", "user_agent",
                     "stream_final_outcome", "normal_disconnect", "peak_bitrate",
                     "effective_profile", "resolution_source", "mediainfo_cache_status", "stream_setup_ms",
                 })
        {
            Assert.True(
                DatabaseMigrationService.ColumnExists(conn, "relay_request_metric", column),
                $"relay_request_metric must have column '{column}'");
        }
    }

    [Fact]
    public void Migration010_RemovesDeadColumns()
    {
        using var conn = _connectionFactory.CreateConnection();
        _migrationService.RunMigrations(conn);

        Assert.False(
            DatabaseMigrationService.ColumnExists(conn, "relay_request_metric", "upstream_connect_duration_ms"),
            "never-populated upstream_connect_duration_ms must be dropped");
        Assert.False(
            DatabaseMigrationService.ColumnExists(conn, "relay_request_metric", "session_duration_ms"),
            "session_duration_ms was always identical to total_duration_ms and must be dropped");
    }

    [Fact]
    public void Migration011_AddsRelayTokenTelemetryColumns()
    {
        using var conn = _connectionFactory.CreateConnection();
        _migrationService.RunMigrations(conn);

        Assert.True(DatabaseMigrationService.ColumnExists(conn, "relay_token", "resolution_source"));
        Assert.True(DatabaseMigrationService.ColumnExists(conn, "relay_token", "mediainfo_cache_status"));
        Assert.True(DatabaseMigrationService.ColumnExists(conn, "relay_token", "stream_setup_ms"));
    }

    [Fact]
    public void Migrations_ReplaySequentially_FromLegacyVersion9Schema()
    {
        using var conn = _connectionFactory.CreateConnection();

        // Apply everything (001–011) on a fresh database — the version-9 tables are created by
        // migration 009 and dropped again by 010, proving the sequential replay works.
        var applied = _migrationService.RunMigrations(conn);

        Assert.True(applied >= 11, $"expected at least 11 migrations, got {applied}");
        Assert.Equal(DatabaseMigrationService.Migrations.Count, _migrationService.GetCurrentVersion(conn));
    }
}
