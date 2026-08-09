// Tests for the MetricsAggregator reading from the consolidated relay_request_metric table.

using System;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Database;
using Jellyfin.Plugin.TvHeadendApi.Service.Metrics;
using Jellyfin.Plugin.TvHeadendApi.Service.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Metrics;

/// <summary>
/// Verifies that Metrics/History and Metrics/Session read from the consolidated
/// <c>relay_request_metric</c> table, that AVG and P95 startup latency use the SAME
/// population (&gt; 0), and that channel/client names aggregate correctly.
/// </summary>
public sealed class MetricsAggregatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly MetricsAggregator _aggregator;

    public MetricsAggregatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tvh_agg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var pathProvider = new DataFolderPathProvider(() => _tempDir);
        var provider = new DatabaseProvider(pathProvider);
        _connectionFactory = new DatabaseConnectionFactory(provider);
        var migration = new DatabaseMigrationService(_connectionFactory, NullLogger<DatabaseMigrationService>.Instance);
        var recovery = new DatabaseRecoveryService(provider, migration, _connectionFactory, NullLogger<DatabaseRecoveryService>.Instance);
        var dbHealth = new DatabaseHealthService(provider, _connectionFactory, migration, recovery, NullLogger<DatabaseHealthService>.Instance);
        dbHealth.Initialize();

        _aggregator = new MetricsAggregator(dbHealth, _connectionFactory, NullLogger<MetricsAggregator>.Instance);
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
    public void GetHistoryMetrics_AggregatesStreamRows_FromRelayRequestMetric()
    {
        InsertStreamRow(sessionId: "s1", channelName: "Das Erste", clientName: "Infuse", startupMs: 400, outcome: "NormalDisconnect", normalDisconnect: true, endedBy: "ClientDisconnectAfterFirstByte");
        InsertStreamRow(sessionId: "s2", channelName: "Das Erste", clientName: "VLC", startupMs: 800, outcome: "Completed", normalDisconnect: true, endedBy: "UpstreamEof");
        InsertStreamRow(sessionId: "s3", channelName: "ZDF", clientName: "Infuse", startupMs: 0, outcome: "StartupFailed", normalDisconnect: false, endedBy: "StartupCancelledBeforeFirstByte");

        var history = _aggregator.GetHistoryMetrics();

        Assert.Equal(3, history.StreamsToday);
        Assert.Equal(1, history.CompletedStreamsToday);
        Assert.Equal(2, history.NormalDisconnectsToday);
        Assert.Equal(2, history.TopChannels["Das Erste"]);
        Assert.Equal(1, history.TopChannels["ZDF"]);
        Assert.Equal(2, history.TopClients["Infuse"]);
        Assert.Equal(1, history.FailuresByReason["StartupCancelledBeforeFirstByte"]);
        Assert.Equal(1, history.FailuresByCategory["StartupFailed"]);
    }

    [Fact]
    public void GetHistoryMetrics_AvgAndP95_UseTheSamePopulation()
    {
        // One row with startup 0 (never delivered a byte) must be excluded from BOTH
        // the AVG and the P95 — previously only the P95 filtered it out.
        InsertStreamRow(sessionId: "p1", channelName: "A", clientName: "C", startupMs: 1000, outcome: "NormalDisconnect", normalDisconnect: true, endedBy: "ClientDisconnectAfterFirstByte");
        InsertStreamRow(sessionId: "p2", channelName: "A", clientName: "C", startupMs: 2000, outcome: "NormalDisconnect", normalDisconnect: true, endedBy: "ClientDisconnectAfterFirstByte");
        InsertStreamRow(sessionId: "p3", channelName: "A", clientName: "C", startupMs: 0, outcome: "StartupFailed", normalDisconnect: false, endedBy: "StartupCancelledBeforeFirstByte");

        var history = _aggregator.GetHistoryMetrics();

        Assert.Equal(1500, history.AvgStartupLatencyMs, precision: 0);
        Assert.Equal(2000, history.P95StartupLatencyMs, precision: 0);
    }

    [Fact]
    public void GetTodayFailureCounts_ClassifiesByStreamFinalOutcome()
    {
        InsertStreamRow(sessionId: "f1", channelName: "A", clientName: "C", startupMs: 0, outcome: "StartupFailed", normalDisconnect: false, endedBy: "StartupCancelledBeforeFirstByte");
        InsertStreamRow(sessionId: "f2", channelName: "A", clientName: "C", startupMs: 0, outcome: "UpstreamFailed", normalDisconnect: false, endedBy: "UpstreamTimeout");
        InsertStreamRow(sessionId: "f3", channelName: "A", clientName: "C", startupMs: 300, outcome: "DownstreamFailed", normalDisconnect: false, endedBy: "DownstreamWriteError");
        InsertStreamRow(sessionId: "f4", channelName: "A", clientName: "C", startupMs: 300, outcome: "NormalDisconnect", normalDisconnect: true, endedBy: "ClientDisconnectAfterFirstByte");

        var (startup, upstream, downstream) = _aggregator.GetTodayFailureCounts();

        Assert.Equal(1, startup);
        Assert.Equal(1, upstream);
        Assert.Equal(1, downstream);
    }

    [Fact]
    public void GetSessionDetail_ReadsSessionRow_WithZappingTelemetry()
    {
        InsertStreamRow(
            sessionId: "detail-1",
            channelName: "Das Erste",
            clientName: "Infuse",
            startupMs: 429,
            outcome: "NormalDisconnect",
            normalDisconnect: true,
            endedBy: "ClientDisconnectAfterFirstByte",
            effectiveProfile: "jellyfin",
            cacheStatus: "hit",
            setupMs: 55.5);

        var detail = _aggregator.GetSessionDetail("detail-1");

        Assert.NotNull(detail);
        var session = detail!.Session!;
        Assert.Equal("detail-1", session.SessionId);
        Assert.Equal("Das Erste", session.ChannelName);
        Assert.Equal("Infuse", session.ClientName);
        Assert.Equal(429, session.StartupLatencyMs, precision: 0);
        Assert.Equal(StreamEndedBy.ClientDisconnectAfterFirstByte, session.EndedBy);
        Assert.Equal(StreamFinalOutcome.NormalDisconnect, session.FinalOutcome);
        Assert.True(session.NormalDisconnect);
        Assert.Equal("jellyfin", session.EffectiveProfile);
        Assert.Equal("hit", session.MediaInfoCacheStatus);
        Assert.Equal(55.5, session.StreamSetupMs);
        Assert.True(session.AverageBitrate > 0, "average bitrate must derive from average_bytes_per_second (bits/s)");
    }

    [Fact]
    public void GetSessionDetail_UnknownSession_ReturnsNull()
    {
        Assert.Null(_aggregator.GetSessionDetail("nope"));
    }

    private void InsertStreamRow(
        string sessionId,
        string channelName,
        string clientName,
        double startupMs,
        string outcome,
        bool normalDisconnect,
        string endedBy,
        string? effectiveProfile = null,
        string? cacheStatus = null,
        double? setupMs = null)
    {
        using var conn = _connectionFactory.CreateConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO relay_request_metric (
                created_at_utc, relay_type, media_kind, channel_id, session_id, channel_name,
                client_name, user_agent, total_duration_ms, startup_latency_ms, bytes_sent,
                average_bytes_per_second, client_status_code, final_outcome, failure_reason,
                client_cancelled, upstream_timed_out, stream_final_outcome, normal_disconnect,
                cache_status, had_etag, had_last_modified, was_not_modified_304, was_range_request,
                has_content_length, request_method, ended_by, startup_failed_within_5_seconds,
                effective_profile, mediainfo_cache_status, stream_setup_ms
            ) VALUES (
                @created, 'stream', 'LiveTvStream', 'ch-1', @sid, @channelName,
                @clientName, 'ua', 60000, @startup, 1048576,
                17476.0, 200, 'success', 'None',
                0, 0, @outcome, @normal,
                'NotApplicable', 0, 0, 0, 0,
                0, 'GET', @endedBy, 0,
                @profile, @cacheStatus, @setupMs
            )
            """;
        // Insert with EF Core's real SQLite DateTime shape (space separator) — the "o" format
        // ('T' separator) would make this test blind to lexicographic date-filter bugs.
        cmd.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@channelName", channelName);
        cmd.Parameters.AddWithValue("@clientName", clientName);
        cmd.Parameters.AddWithValue("@startup", startupMs);
        cmd.Parameters.AddWithValue("@outcome", outcome);
        cmd.Parameters.AddWithValue("@normal", normalDisconnect ? 1 : 0);
        cmd.Parameters.AddWithValue("@endedBy", endedBy);
        cmd.Parameters.AddWithValue("@profile", (object?)effectiveProfile ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cacheStatus", (object?)cacheStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@setupMs", (object?)setupMs ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
