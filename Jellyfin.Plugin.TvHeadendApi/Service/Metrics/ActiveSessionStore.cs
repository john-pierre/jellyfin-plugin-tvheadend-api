// Thread-safe in-memory store for active streaming sessions with real-time visibility.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TvHeadendApi.Model.Metrics;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Metrics;

/// <summary>
/// Thread-safe concurrent store for active streaming sessions.
/// Provides real-time visibility into all ongoing streams.
/// Sessions are added on stream start and removed on finalization.
/// </summary>
public sealed class ActiveSessionStore
{
    private readonly ConcurrentDictionary<string, ActiveStreamSession> _sessions = new(StringComparer.Ordinal);

    /// <summary>Gets the current number of active sessions.</summary>
    public int Count => _sessions.Count;

    /// <summary>Registers a new active session.</summary>
    /// <param name="session">The session to register.</param>
    public void Add(ActiveStreamSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _sessions[session.SessionId] = session;
    }

    /// <summary>Retrieves an active session by ID.</summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <returns>The session if found; otherwise null.</returns>
    public ActiveStreamSession? Get(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    /// <summary>Removes a session from the active store.</summary>
    /// <param name="sessionId">The session identifier to remove.</param>
    /// <returns>The removed session if found; otherwise null.</returns>
    public ActiveStreamSession? Remove(string sessionId)
    {
        _sessions.TryRemove(sessionId, out var session);
        return session;
    }

    /// <summary>Returns a snapshot of all currently active sessions.</summary>
    /// <returns>A list of all active sessions (copy, safe to enumerate).</returns>
    public List<ActiveStreamSession> GetAll()
    {
        return _sessions.Values.ToList();
    }

    /// <summary>Returns sessions grouped by client name.</summary>
    /// <returns>Dictionary of client names to session counts.</returns>
    public Dictionary<string, int> GetClientDistribution()
    {
        return _sessions.Values
            .GroupBy(s => string.IsNullOrEmpty(s.ClientName) ? "Unknown" : s.ClientName)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>Returns distinct active channel names.</summary>
    /// <returns>List of unique channel names currently being streamed.</returns>
    public List<string> GetActiveChannels()
    {
        return _sessions.Values
            .Select(s => string.IsNullOrEmpty(s.ChannelName) ? s.ChannelId : s.ChannelName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct()
            .ToList();
    }

    /// <summary>Returns total bandwidth across all active sessions in bits/second.</summary>
    /// <returns>Sum of rolling bitrates across all sessions.</returns>
    public double GetTotalBandwidth()
    {
        return _sessions.Values.Sum(s => s.RollingBitrate);
    }

    /// <summary>Returns count of sessions in a specific lifecycle state.</summary>
    /// <param name="state">The lifecycle state to filter by.</param>
    /// <returns>Number of sessions in the specified state.</returns>
    public int CountByState(StreamLifecycleState state)
    {
        return _sessions.Values.Count(s => s.StreamState == state);
    }
}
