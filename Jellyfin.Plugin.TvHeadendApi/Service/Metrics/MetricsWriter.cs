// Metrics writer — low-overhead persistence of completed sessions and events to SQLite.

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Metrics;
using Jellyfin.Plugin.TvHeadendApi.Service.Database;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Metrics;

/// <summary>
/// Low-overhead persistence layer for streaming telemetry data.
/// Batches writes and avoids blocking the stream copy hot path.
/// Uses fire-and-forget ThreadPool work items for non-critical writes.
/// Critical writes (session start/end) are immediate.
/// </summary>
internal class MetricsWriter
{
    private readonly DatabaseHealthService _dbHealthService;
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly DatabaseWriteCoordinator _writeCoordinator;
    private readonly ILogger<MetricsWriter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetricsWriter"/> class for testing.
    /// </summary>
    protected MetricsWriter()
    {
        _dbHealthService = null!;
        _connectionFactory = null!;
        _writeCoordinator = null!;
        _logger = null!;
    }

    public MetricsWriter(
        DatabaseHealthService dbHealthService,
        DatabaseConnectionFactory connectionFactory,
        DatabaseWriteCoordinator writeCoordinator,
        ILogger<MetricsWriter> logger)
    {
        _dbHealthService = dbHealthService ?? throw new ArgumentNullException(nameof(dbHealthService));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _writeCoordinator = writeCoordinator ?? throw new ArgumentNullException(nameof(writeCoordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Persists a completed stream session. Fire-and-forget — never blocks the relay.
    /// </summary>
    /// <param name="session">The completed session to persist.</param>
    public virtual void WriteCompletedSession(CompletedStreamSession session)
    {
        if (!_dbHealthService.IsAvailable || session == null)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(_ => PersistCompletedSessionSync(session));
    }

    /// <summary>
    /// Persists a relay event. Fire-and-forget — never blocks the relay.
    /// </summary>
    /// <param name="relayEvent">The relay event to persist.</param>
    public virtual void WriteEvent(RelayEvent relayEvent)
    {
        if (!_dbHealthService.IsAvailable || relayEvent == null)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(_ => PersistEventSync(relayEvent));
    }

    /// <summary>
    /// Persists an active session snapshot (periodic flush for crash recovery).
    /// </summary>
    /// <param name="session">The active session to snapshot.</param>
    public virtual void WriteActiveSessionSnapshot(ActiveStreamSession session)
    {
        if (!_dbHealthService.IsAvailable || session == null)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(_ => PersistActiveSessionSync(session));
    }

    private void PersistCompletedSessionSync(CompletedStreamSession session)
    {
        using var writeLock = _writeCoordinator.AcquireWrite();
        try
        {
            using var connection = _connectionFactory.CreateConnection();
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO completed_stream_sessions (
                    session_id, started_at_utc, ended_at_utc, channel_id, channel_name,
                    client_name, client_ip_hash, request_method, total_duration_ms, session_duration_ms,
                    total_bytes, rolling_bitrate, average_bitrate, peak_bitrate,
                    startup_latency_ms, upstream_connect_latency_ms, upstream_headers_latency_ms,
                    upstream_first_byte_latency_ms, downstream_first_byte_latency_ms, p95_write_latency_ms,
                    upstream_status, downstream_status, range_requested, range_supported,
                    content_range_present, accept_ranges_present, ended_by, failure_reason,
                    final_outcome, normal_disconnect, user_agent, remote_endpoint_hash
                ) VALUES (
                    @sid, @startedAt, @endedAt, @channelId, @channelName,
                    @clientName, @clientIpHash, @requestMethod, @totalDurationMs, @sessionDurationMs,
                    @totalBytes, @rollingBitrate, @avgBitrate, @peakBitrate,
                    @startupLatencyMs, @upConnectMs, @upHeadersMs,
                    @upFirstByteMs, @downFirstByteMs, @p95WriteMs,
                    @upStatus, @downStatus, @rangeReq, @rangeSupp,
                    @contentRange, @acceptRanges, @endedBy, @failureReason,
                    @finalOutcome, @normalDisconnect, @userAgent, @remoteHash
                )
                """;

            cmd.Parameters.AddWithValue("@sid", session.SessionId);
            cmd.Parameters.AddWithValue("@startedAt", session.StartedAtUtc.ToString("o"));
            cmd.Parameters.AddWithValue("@endedAt", session.EndedAtUtc.ToString("o"));
            cmd.Parameters.AddWithValue("@channelId", session.ChannelId);
            cmd.Parameters.AddWithValue("@channelName", session.ChannelName);
            cmd.Parameters.AddWithValue("@clientName", session.ClientName);
            cmd.Parameters.AddWithValue("@clientIpHash", session.ClientIpHash);
            cmd.Parameters.AddWithValue("@requestMethod", session.RequestMethod);
            cmd.Parameters.AddWithValue("@totalDurationMs", session.TotalDurationMs);
            cmd.Parameters.AddWithValue("@sessionDurationMs", session.SessionDurationMs);
            cmd.Parameters.AddWithValue("@totalBytes", session.TotalBytes);
            cmd.Parameters.AddWithValue("@rollingBitrate", session.RollingBitrate);
            cmd.Parameters.AddWithValue("@avgBitrate", session.AverageBitrate);
            cmd.Parameters.AddWithValue("@peakBitrate", session.PeakBitrate);
            cmd.Parameters.AddWithValue("@startupLatencyMs", session.StartupLatencyMs);
            cmd.Parameters.AddWithValue("@upConnectMs", (object?)session.UpstreamConnectLatencyMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@upHeadersMs", (object?)session.UpstreamHeadersLatencyMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@upFirstByteMs", (object?)session.UpstreamFirstByteLatencyMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@downFirstByteMs", (object?)session.DownstreamFirstByteLatencyMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p95WriteMs", (object?)session.P95WriteLatencyMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@upStatus", session.UpstreamStatus);
            cmd.Parameters.AddWithValue("@downStatus", session.DownstreamStatus);
            cmd.Parameters.AddWithValue("@rangeReq", session.RangeRequested ? 1 : 0);
            cmd.Parameters.AddWithValue("@rangeSupp", session.RangeSupported ? 1 : 0);
            cmd.Parameters.AddWithValue("@contentRange", session.ContentRangePresent ? 1 : 0);
            cmd.Parameters.AddWithValue("@acceptRanges", session.AcceptRangesPresent ? 1 : 0);
            cmd.Parameters.AddWithValue("@endedBy", session.EndedBy.ToString());
            cmd.Parameters.AddWithValue("@failureReason", (object?)session.FailureReason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@finalOutcome", session.FinalOutcome.ToString());
            cmd.Parameters.AddWithValue("@normalDisconnect", session.NormalDisconnect ? 1 : 0);
            cmd.Parameters.AddWithValue("@userAgent", session.UserAgent);
            cmd.Parameters.AddWithValue("@remoteHash", session.RemoteEndpointHash);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist completed session {SessionId}", session.SessionId);
        }
    }

    private void PersistEventSync(RelayEvent relayEvent)
    {
        using var writeLock = _writeCoordinator.AcquireWrite();
        try
        {
            using var connection = _connectionFactory.CreateConnection();
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO relay_events (
                    timestamp_utc, session_id, event_type, severity, message,
                    upstream_status, downstream_status, bytes_sent_snapshot
                ) VALUES (
                    @ts, @sid, @type, @severity, @message, @upStatus, @downStatus, @bytes
                )
                """;
            cmd.Parameters.AddWithValue("@ts", relayEvent.TimestampUtc.ToString("o"));
            cmd.Parameters.AddWithValue("@sid", relayEvent.SessionId);
            cmd.Parameters.AddWithValue("@type", relayEvent.EventType);
            cmd.Parameters.AddWithValue("@severity", relayEvent.Severity);
            cmd.Parameters.AddWithValue("@message", relayEvent.Message);
            cmd.Parameters.AddWithValue("@upStatus", (object?)relayEvent.UpstreamStatus ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@downStatus", (object?)relayEvent.DownstreamStatus ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@bytes", (object?)relayEvent.BytesSentSnapshot ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist relay event for session {SessionId}", relayEvent.SessionId);
        }
    }

    private void PersistActiveSessionSync(ActiveStreamSession session)
    {
        using var writeLock = _writeCoordinator.AcquireWrite();
        try
        {
            using var connection = _connectionFactory.CreateConnection();
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO active_stream_sessions (
                    session_id, started_at_utc, last_update_utc, channel_id, channel_name,
                    client_name, client_ip_hash, request_method, bytes_sent, rolling_bitrate,
                    average_bitrate, peak_bitrate, startup_latency_ms, upstream_status,
                    downstream_status, range_requested, range_supported, content_range_present,
                    accept_ranges_present, stream_state, user_agent, remote_endpoint_hash
                ) VALUES (
                    @sid, @startedAt, @lastUpdate, @channelId, @channelName,
                    @clientName, @clientIpHash, @requestMethod, @bytes, @rollingBitrate,
                    @avgBitrate, @peakBitrate, @startupLatencyMs, @upStatus,
                    @downStatus, @rangeReq, @rangeSupp, @contentRange,
                    @acceptRanges, @streamState, @userAgent, @remoteHash
                )
                """;
            cmd.Parameters.AddWithValue("@sid", session.SessionId);
            cmd.Parameters.AddWithValue("@startedAt", session.StartedAtUtc.ToString("o"));
            cmd.Parameters.AddWithValue("@lastUpdate", session.LastUpdateUtc.ToString("o"));
            cmd.Parameters.AddWithValue("@channelId", session.ChannelId);
            cmd.Parameters.AddWithValue("@channelName", session.ChannelName);
            cmd.Parameters.AddWithValue("@clientName", session.ClientName);
            cmd.Parameters.AddWithValue("@clientIpHash", session.ClientIpHash);
            cmd.Parameters.AddWithValue("@requestMethod", session.RequestMethod);
            cmd.Parameters.AddWithValue("@bytes", session.BytesSent);
            cmd.Parameters.AddWithValue("@rollingBitrate", session.RollingBitrate);
            cmd.Parameters.AddWithValue("@avgBitrate", session.AverageBitrate);
            cmd.Parameters.AddWithValue("@peakBitrate", session.PeakBitrate);
            cmd.Parameters.AddWithValue("@startupLatencyMs", session.StartupLatencyMs);
            cmd.Parameters.AddWithValue("@upStatus", session.UpstreamStatus);
            cmd.Parameters.AddWithValue("@downStatus", session.DownstreamStatus);
            cmd.Parameters.AddWithValue("@rangeReq", session.RangeRequested ? 1 : 0);
            cmd.Parameters.AddWithValue("@rangeSupp", session.RangeSupported ? 1 : 0);
            cmd.Parameters.AddWithValue("@contentRange", session.ContentRangePresent ? 1 : 0);
            cmd.Parameters.AddWithValue("@acceptRanges", session.AcceptRangesPresent ? 1 : 0);
            cmd.Parameters.AddWithValue("@streamState", session.StreamState.ToString());
            cmd.Parameters.AddWithValue("@userAgent", session.UserAgent);
            cmd.Parameters.AddWithValue("@remoteHash", session.RemoteEndpointHash);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist active session snapshot {SessionId}", session.SessionId);
        }
    }
}
