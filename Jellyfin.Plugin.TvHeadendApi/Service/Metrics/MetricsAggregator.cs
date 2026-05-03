// Metrics aggregator — reads completed sessions from SQLite for dashboard queries.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.TvHeadendApi.Model.Metrics;
using Jellyfin.Plugin.TvHeadendApi.Service.Database;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Metrics;

/// <summary>
/// Reads completed session data from SQLite and aggregates it for dashboard APIs.
/// Provides historical metrics queries with efficient SQL-level filtering.
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
    /// Builds a history metrics response for today's completed sessions.
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

            var todayStart = DateTime.UtcNow.Date.ToString("o", CultureInfo.InvariantCulture);

            // Aggregate counts.
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT
                        COUNT(*) as total,
                        SUM(CASE WHEN final_outcome = 'Completed' THEN 1 ELSE 0 END) as completed,
                        SUM(CASE WHEN normal_disconnect = 1 THEN 1 ELSE 0 END) as normal_disconnects,
                        AVG(startup_latency_ms) as avg_startup,
                        SUM(total_bytes) as total_bytes,
                        AVG(session_duration_ms) as avg_duration
                    FROM completed_stream_sessions
                    WHERE started_at_utc >= @todayStart
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

            // P95 startup latency.
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT startup_latency_ms FROM completed_stream_sessions
                    WHERE started_at_utc >= @todayStart AND startup_latency_ms > 0
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
                    SELECT channel_name, COUNT(*) as cnt FROM completed_stream_sessions
                    WHERE started_at_utc >= @todayStart AND channel_name != ''
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
                    SELECT client_name, COUNT(*) as cnt FROM completed_stream_sessions
                    WHERE started_at_utc >= @todayStart AND client_name != ''
                    GROUP BY client_name ORDER BY cnt DESC LIMIT 10
                    """;
                cmd.Parameters.AddWithValue("@todayStart", todayStart);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    response.TopClients[reader.GetString(0)] = reader.GetInt32(1);
                }
            }

            // Failures by reason.
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT failure_reason, COUNT(*) as cnt FROM completed_stream_sessions
                    WHERE started_at_utc >= @todayStart AND failure_reason IS NOT NULL
                    GROUP BY failure_reason ORDER BY cnt DESC
                    """;
                cmd.Parameters.AddWithValue("@todayStart", todayStart);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    response.FailuresByReason[reader.GetString(0)] = reader.GetInt32(1);
                }
            }

            // Failures by category (upstream/downstream).
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT final_outcome, COUNT(*) as cnt FROM completed_stream_sessions
                    WHERE started_at_utc >= @todayStart AND normal_disconnect = 0
                    GROUP BY final_outcome ORDER BY cnt DESC
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
    /// Gets failure counts for today grouped by upstream/downstream.
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
            var todayStart = DateTime.UtcNow.Date.ToString("o", CultureInfo.InvariantCulture);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT
                    SUM(CASE WHEN final_outcome = 'StartupFailed' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN final_outcome = 'UpstreamFailed' THEN 1 ELSE 0 END),
                    SUM(CASE WHEN final_outcome = 'DownstreamFailed' THEN 1 ELSE 0 END)
                FROM completed_stream_sessions
                WHERE started_at_utc >= @todayStart
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
    /// Gets a completed session by ID with its event timeline.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>Session detail with events, or null if not found.</returns>
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

            CompletedStreamSession? session = null;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT * FROM completed_stream_sessions WHERE session_id = @sid LIMIT 1";
                cmd.Parameters.AddWithValue("@sid", sessionId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    session = ReadCompletedSession(reader);
                }
            }

            if (session == null)
            {
                return null;
            }

            var events = new List<RelayEvent>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT * FROM relay_events WHERE session_id = @sid ORDER BY timestamp_utc";
                cmd.Parameters.AddWithValue("@sid", sessionId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    events.Add(ReadRelayEvent(reader));
                }
            }

            return new SessionMetricsResponse { Session = session, Events = events };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get session detail for {SessionId}", sessionId);
            return null;
        }
    }

    private static CompletedStreamSession ReadCompletedSession(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        return new CompletedStreamSession
        {
            SessionId = reader["session_id"]?.ToString() ?? string.Empty,
            StartedAtUtc = DateTime.Parse(reader["started_at_utc"]?.ToString() ?? DateTime.UtcNow.ToString("o"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            EndedAtUtc = DateTime.Parse(reader["ended_at_utc"]?.ToString() ?? DateTime.UtcNow.ToString("o"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ChannelId = reader["channel_id"]?.ToString() ?? string.Empty,
            ChannelName = reader["channel_name"]?.ToString() ?? string.Empty,
            ClientName = reader["client_name"]?.ToString() ?? string.Empty,
            ClientIpHash = reader["client_ip_hash"]?.ToString() ?? string.Empty,
            RequestMethod = reader["request_method"]?.ToString() ?? "GET",
            TotalDurationMs = Convert.ToDouble(reader["total_duration_ms"], CultureInfo.InvariantCulture),
            SessionDurationMs = Convert.ToDouble(reader["session_duration_ms"], CultureInfo.InvariantCulture),
            TotalBytes = Convert.ToInt64(reader["total_bytes"], CultureInfo.InvariantCulture),
            EndedBy = Enum.TryParse<StreamEndedBy>(reader["ended_by"]?.ToString(), out var eb) ? eb : StreamEndedBy.Unknown,
            FinalOutcome = Enum.TryParse<StreamFinalOutcome>(reader["final_outcome"]?.ToString(), out var fo) ? fo : StreamFinalOutcome.Failed,
            NormalDisconnect = Convert.ToInt32(reader["normal_disconnect"], CultureInfo.InvariantCulture) == 1,
            FailureReason = reader["failure_reason"] == DBNull.Value ? null : reader["failure_reason"]?.ToString(),
            UserAgent = reader["user_agent"]?.ToString() ?? string.Empty,
        };
    }

    private static RelayEvent ReadRelayEvent(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        return new RelayEvent
        {
            EventId = Convert.ToInt64(reader["event_id"], CultureInfo.InvariantCulture),
            TimestampUtc = DateTime.Parse(reader["timestamp_utc"]?.ToString() ?? DateTime.UtcNow.ToString("o"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            SessionId = reader["session_id"]?.ToString() ?? string.Empty,
            EventType = reader["event_type"]?.ToString() ?? string.Empty,
            Severity = reader["severity"]?.ToString() ?? "info",
            Message = reader["message"]?.ToString() ?? string.Empty,
            UpstreamStatus = reader["upstream_status"] == DBNull.Value ? null : reader["upstream_status"]?.ToString(),
            DownstreamStatus = reader["downstream_status"] == DBNull.Value ? null : reader["downstream_status"]?.ToString(),
            BytesSentSnapshot = reader["bytes_sent_snapshot"] == DBNull.Value ? null : Convert.ToInt64(reader["bytes_sent_snapshot"], CultureInfo.InvariantCulture),
        };
    }
}
