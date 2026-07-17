// Metrics aggregator — reads stream telemetry from the consolidated relay_request_metric table.

using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.TvHeadendApi.Model.Metrics;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Database;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Metrics;

/// <summary>
/// Reads stream telemetry rows from the consolidated <c>relay_request_metric</c> table and
/// aggregates them for the dashboard APIs. Read-only — persistence is owned by the relay
/// metrics pipeline.
/// </summary>
internal sealed class MetricsAggregator
{
    private readonly DatabaseHealthService _dbHealthService;
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly ILogger<MetricsAggregator> _logger;

    public MetricsAggregator(
        DatabaseHealthService dbHealthService,
        DatabaseConnectionFactory connectionFactory,
        ILogger<MetricsAggregator> logger)
    {
        _dbHealthService = dbHealthService ?? throw new ArgumentNullException(nameof(dbHealthService));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Builds a history metrics response for today's stream requests.
    /// </summary>
    /// <returns>Historical metrics response with aggregated data.</returns>
    public HistoryMetricsResponse GetHistoryMetrics()
    {
        var response = new HistoryMetricsResponse();
        if (!_dbHealthService.IsAvailable)
        {
            return response;
        }

        try
        {
            using var connection = _connectionFactory.CreateConnection();
            connection.Open();

            // EF Core's SQLite provider stores DateTime as "yyyy-MM-dd HH:mm:ss.FFFFFFF" (space
            // separator). TEXT comparison is lexicographic, so the bound parameter MUST use the
            // same shape — ISO "o" format ('T' separator) silently matches nothing.
            var todayStart = DateTime.UtcNow.Date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            // Aggregate counts. AVG(startup_latency_ms) filters > 0 so its population matches
            // the P95 query below — previously the two disagreed (AVG included zeros).
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT
                        COUNT(*) as total,
                        SUM(CASE WHEN stream_final_outcome = 'Completed' THEN 1 ELSE 0 END) as completed,
                        SUM(CASE WHEN normal_disconnect = 1 THEN 1 ELSE 0 END) as normal_disconnects,
                        AVG(CASE WHEN startup_latency_ms > 0 THEN startup_latency_ms ELSE NULL END) as avg_startup,
                        SUM(bytes_sent) as total_bytes,
                        AVG(total_duration_ms) as avg_duration
                    FROM relay_request_metric
                    WHERE relay_type = 'stream' AND created_at_utc >= @todayStart
                    """;
                cmd.Parameters.AddWithValue("@todayStart", todayStart);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    response.StreamsToday = reader.GetInt32(0);
                    response.CompletedStreamsToday = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                    response.NormalDisconnectsToday = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                    response.AvgStartupLatencyMs = reader.IsDBNull(3) ? 0 : reader.GetDouble(3);
                    response.TotalBytesToday = reader.IsDBNull(4) ? 0 : reader.GetInt64(4);
                    response.AverageWatchDurationMs = reader.IsDBNull(5) ? 0 : reader.GetDouble(5);
                }
            }

            // P95 startup latency — same population filter (> 0) as the AVG above.
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT startup_latency_ms FROM relay_request_metric
                    WHERE relay_type = 'stream' AND created_at_utc >= @todayStart AND startup_latency_ms > 0
                    ORDER BY startup_latency_ms
                    """;
                cmd.Parameters.AddWithValue("@todayStart", todayStart);
                var latencies = new List<double>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    latencies.Add(reader.GetDouble(0));
                }

                if (latencies.Count > 0)
                {
                    var p95Index = (int)Math.Ceiling(latencies.Count * 0.95) - 1;
                    response.P95StartupLatencyMs = latencies[Math.Max(0, p95Index)];
                }
            }

            // Top channels.
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT channel_name, COUNT(*) as cnt FROM relay_request_metric
                    WHERE relay_type = 'stream' AND created_at_utc >= @todayStart
                          AND channel_name IS NOT NULL AND channel_name != ''
                    GROUP BY channel_name ORDER BY cnt DESC LIMIT 10
                    """;
                cmd.Parameters.AddWithValue("@todayStart", todayStart);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    response.TopChannels[reader.GetString(0)] = reader.GetInt32(1);
                }
            }

            // Top clients.
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT client_name, COUNT(*) as cnt FROM relay_request_metric
                    WHERE relay_type = 'stream' AND created_at_utc >= @todayStart
                          AND client_name IS NOT NULL AND client_name != ''
                    GROUP BY client_name ORDER BY cnt DESC LIMIT 10
                    """;
                cmd.Parameters.AddWithValue("@todayStart", todayStart);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    response.TopClients[reader.GetString(0)] = reader.GetInt32(1);
                }
            }

            // Failures by reason (ended_by) — failures only, normal disconnects excluded.
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT ended_by, COUNT(*) as cnt FROM relay_request_metric
                    WHERE relay_type = 'stream' AND created_at_utc >= @todayStart
                          AND normal_disconnect = 0 AND ended_by IS NOT NULL
                    GROUP BY ended_by ORDER BY cnt DESC
                    """;
                cmd.Parameters.AddWithValue("@todayStart", todayStart);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    response.FailuresByReason[reader.GetString(0)] = reader.GetInt32(1);
                }
            }

            // Failures by category (startup/upstream/downstream classification).
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT stream_final_outcome, COUNT(*) as cnt FROM relay_request_metric
                    WHERE relay_type = 'stream' AND created_at_utc >= @todayStart
                          AND normal_disconnect = 0 AND stream_final_outcome IS NOT NULL
                    GROUP BY stream_final_outcome ORDER BY cnt DESC
                    """;
                cmd.Parameters.AddWithValue("@todayStart", todayStart);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    response.FailuresByCategory[reader.GetString(0)] = reader.GetInt32(1);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to aggregate history metrics");
        }

        return response;
    }

    /// <summary>
    /// Gets failure counts for today grouped by startup/upstream/downstream.
    /// </summary>
    /// <returns>Tuple of (startup, upstream, downstream) failure counts.</returns>
    public (int Startup, int Upstream, int Downstream) GetTodayFailureCounts()
    {
        if (!_dbHealthService.IsAvailable)
        {
            return (0, 0, 0);
        }

        try
        {
            using var connection = _connectionFactory.CreateConnection();
            connection.Open();
            // EF Core's SQLite provider stores DateTime as "yyyy-MM-dd HH:mm:ss.FFFFFFF" (space
            // separator). TEXT comparison is lexicographic, so the bound parameter MUST use the
            // same shape — ISO "o" format ('T' separator) silently matches nothing.
            var todayStart = DateTime.UtcNow.Date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT
                    SUM(CASE WHEN stream_final_outcome = 'StartupFailed' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN stream_final_outcome = 'UpstreamFailed' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN stream_final_outcome = 'DownstreamFailed' THEN 1 ELSE 0 END)
                FROM relay_request_metric
                WHERE relay_type = 'stream' AND created_at_utc >= @todayStart
                """;
            cmd.Parameters.AddWithValue("@todayStart", todayStart);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return (
                    reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                    reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                    reader.IsDBNull(2) ? 0 : reader.GetInt32(2));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query today's failure counts");
        }

        return (0, 0, 0);
    }

    /// <summary>
    /// Gets a completed session by its session ID from the consolidated metric table.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>Session detail, or null if not found.</returns>
    public SessionMetricsResponse? GetSessionDetail(string sessionId)
    {
        if (!_dbHealthService.IsAvailable || string.IsNullOrEmpty(sessionId))
        {
            return null;
        }

        try
        {
            using var connection = _connectionFactory.CreateConnection();
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM relay_request_metric WHERE session_id = @sid LIMIT 1";
            cmd.Parameters.AddWithValue("@sid", sessionId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new SessionMetricsResponse { Session = ReadCompletedSession(reader) };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get session detail for {SessionId}", sessionId);
            return null;
        }
    }

    private static CompletedStreamSession ReadCompletedSession(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        var startedAt = DateTime.Parse(
            reader["created_at_utc"]?.ToString() ?? DateTime.UtcNow.ToString("o"),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        var totalDurationMs = Convert.ToDouble(reader["total_duration_ms"], CultureInfo.InvariantCulture);
        var avgBytesPerSecond = ReadNullableDouble(reader, "average_bytes_per_second");
        var startupLatency = ReadNullableDouble(reader, "startup_latency_ms");

        return new CompletedStreamSession
        {
            SessionId = reader["session_id"]?.ToString() ?? string.Empty,
            StartedAtUtc = startedAt,
            EndedAtUtc = startedAt.AddMilliseconds(totalDurationMs),
            ChannelId = reader["channel_id"]?.ToString() ?? string.Empty,
            ChannelName = reader["channel_name"] == DBNull.Value ? string.Empty : reader["channel_name"]?.ToString() ?? string.Empty,
            ClientName = reader["client_name"] == DBNull.Value ? string.Empty : reader["client_name"]?.ToString() ?? string.Empty,
            RequestMethod = reader["request_method"]?.ToString() ?? "GET",
            TotalDurationMs = totalDurationMs,
            TotalBytes = Convert.ToInt64(reader["bytes_sent"], CultureInfo.InvariantCulture),
            AverageBitrate = (avgBytesPerSecond ?? 0) * 8.0,
            PeakBitrate = ReadNullableDouble(reader, "peak_bitrate"),
            StartupLatencyMs = startupLatency ?? 0,
            UpstreamHeadersLatencyMs = ReadNullableDouble(reader, "upstream_headers_duration_ms"),
            UpstreamFirstByteLatencyMs = ReadNullableDouble(reader, "first_byte_from_upstream_duration_ms"),
            DownstreamFirstByteLatencyMs = ReadNullableDouble(reader, "first_byte_to_client_duration_ms"),
            RangeRequested = Convert.ToInt32(reader["was_range_request"], CultureInfo.InvariantCulture) == 1,
            EndedBy = Enum.TryParse<StreamEndedBy>(reader["ended_by"]?.ToString(), out var eb) ? eb : StreamEndedBy.Unknown,
            FailureReason = reader["failure_reason"] == DBNull.Value ? null : reader["failure_reason"]?.ToString(),
            FinalOutcome = Enum.TryParse<StreamFinalOutcome>(reader["stream_final_outcome"]?.ToString(), out var fo) ? fo : StreamFinalOutcome.Failed,
            NormalDisconnect = reader["normal_disconnect"] != DBNull.Value
                && Convert.ToInt32(reader["normal_disconnect"], CultureInfo.InvariantCulture) == 1,
            UserAgent = reader["user_agent"] == DBNull.Value ? string.Empty : reader["user_agent"]?.ToString() ?? string.Empty,
            EffectiveProfile = reader["effective_profile"] == DBNull.Value ? null : reader["effective_profile"]?.ToString(),
            ResolutionSource = reader["resolution_source"] == DBNull.Value ? null : reader["resolution_source"]?.ToString(),
            MediaInfoCacheStatus = reader["mediainfo_cache_status"] == DBNull.Value ? null : reader["mediainfo_cache_status"]?.ToString(),
            StreamSetupMs = ReadNullableDouble(reader, "stream_setup_ms"),
        };
    }

    private static double? ReadNullableDouble(Microsoft.Data.Sqlite.SqliteDataReader reader, string column)
    {
        var value = reader[column];
        return value == DBNull.Value || value == null ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }
}
