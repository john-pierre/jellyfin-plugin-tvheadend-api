using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Statistics;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Statistics;

/// <summary>
/// Tracks live TV viewing sessions by listening to Jellyfin playback events.
/// Persists data to a JSON file in the plugin data directory.
/// </summary>
internal sealed class StatisticsService : IStatisticsService, IHostedService, IDisposable
{
    private const string FileName = "viewing-statistics.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ILogger<StatisticsService> _logger;
    private readonly ISessionManager _sessionManager;
    private readonly List<ViewingSession> _sessions = new();
    private readonly ConcurrentDictionary<string, ViewingSession> _activeSessions = new();
    private readonly object _lock = new();
    private Timer? _saveTimer;

    public StatisticsService(
        ILogger<StatisticsService> logger,
        ISessionManager sessionManager)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
    }

    /// <inheritdoc />
    public IReadOnlyList<ViewingSession> AllSessions
    {
        get
        {
            lock (_lock)
            {
                return _sessions.ToList().AsReadOnly();
            }
        }
    }

    /// <inheritdoc />
    public ViewingStatisticsResult GetStatistics(int days)
    {
        lock (_lock)
        {
            var cutoff = days > 0 ? DateTime.UtcNow.AddDays(-days) : DateTime.MinValue;
            var filtered = _sessions
                .Where(s => s.StartTimeUtc >= cutoff)
                .OrderByDescending(s => s.StartTimeUtc)
                .ToList();

            return new ViewingStatisticsResult
            {
                Sessions = filtered,
                TotalCount = filtered.Count,
                ActiveCount = _activeSessions.Count,
            };
        }
    }

    /// <inheritdoc />
    public void ClearStatistics()
    {
        lock (_lock)
        {
            _sessions.Clear();
            _activeSessions.Clear();
        }

        SaveToDisk();
        _logger.LogInformation("Viewing statistics cleared.");
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        LoadFromDisk();
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;

        // Periodic save — interval from config
        var saveMinutes = Plugin.Instance?.Configuration is Configuration.PluginConfiguration c
            ? (c.StatisticsSaveIntervalMinutes > 0 ? c.StatisticsSaveIntervalMinutes : 5)
            : 5;
        _saveTimer = new Timer(_ => SaveToDisk(), null, TimeSpan.FromMinutes(saveMinutes), TimeSpan.FromMinutes(saveMinutes));

        _logger.LogInformation("StatisticsService started — tracking live TV viewing sessions.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);

        // Close any active sessions
        foreach (var kvp in _activeSessions)
        {
            kvp.Value.EndTimeUtc = DateTime.UtcNow;
            lock (_lock)
            {
                _sessions.Add(kvp.Value);
            }
        }

        _activeSessions.Clear();
        SaveToDisk();

        _logger.LogInformation("StatisticsService stopped.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _saveTimer?.Dispose();
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        // Only track live TV channels
        if (e.Item is not LiveTvChannel)
        {
            return;
        }

        var sessionId = e.PlaySessionId ?? Guid.NewGuid().ToString("N");
        var userName = e.Users?.FirstOrDefault()?.Username ?? "Unknown";
        var playMethod = e.Session?.PlayState?.PlayMethod?.ToString() ?? "Unknown";

        var session = new ViewingSession
        {
            UserName = userName,
            DeviceName = e.DeviceName ?? "Unknown",
            ClientName = e.ClientName ?? "Unknown",
            ChannelName = e.Item.Name ?? "Unknown",
            ChannelId = e.Item.Id.ToString("N"),
            PlayMethod = playMethod,
            StartTimeUtc = DateTime.UtcNow,
            PlaySessionId = sessionId,
        };

        _activeSessions[sessionId] = session;
        _logger.LogDebug(
            "Live TV playback started: User={User}, Device={Device}, Channel={Channel}, PlayMethod={PlayMethod}",
            session.UserName,
            session.DeviceName,
            session.ChannelName,
            session.PlayMethod);
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Item is not LiveTvChannel)
        {
            return;
        }

        var sessionId = e.PlaySessionId ?? string.Empty;
        if (!_activeSessions.TryRemove(sessionId, out var session))
        {
            return;
        }

        session.EndTimeUtc = DateTime.UtcNow;

        // Update play method from the stop event (may have changed during playback)
        var currentPlayMethod = e.Session?.PlayState?.PlayMethod?.ToString();
        if (!string.IsNullOrEmpty(currentPlayMethod))
        {
            session.PlayMethod = currentPlayMethod;
        }

        lock (_lock)
        {
            _sessions.Add(session);
            PruneOldSessions();
        }

        _logger.LogDebug(
            "Live TV playback stopped: User={User}, Device={Device}, Channel={Channel}, Duration={Duration:F1}min",
            session.UserName,
            session.DeviceName,
            session.ChannelName,
            session.DurationMinutes);

        SaveToDisk();
    }

    private void PruneOldSessions()
    {
        var retentionDays = Plugin.Instance?.Configuration is Configuration.PluginConfiguration cfg
            ? cfg.StatisticsRetentionDays
            : 30;

        var effectiveDays = retentionDays > 0 ? retentionDays : 30;
        var cutoff = DateTime.UtcNow.AddDays(-effectiveDays);
        _sessions.RemoveAll(s => s.StartTimeUtc < cutoff);
    }

    private string? GetFilePath()
    {
        var dataPath = Plugin.Instance?.DataFolderPath;
        return string.IsNullOrWhiteSpace(dataPath) ? null : Path.Combine(dataPath, FileName);
    }

    private void LoadFromDisk()
    {
        try
        {
            var filePath = GetFilePath();
            if (filePath == null || !File.Exists(filePath))
            {
                return;
            }

            var json = File.ReadAllText(filePath);
            var loaded = JsonSerializer.Deserialize<List<ViewingSession>>(json, JsonOptions);
            if (loaded != null)
            {
                lock (_lock)
                {
                    _sessions.AddRange(loaded);
                    PruneOldSessions();
                }

                _logger.LogInformation("Loaded {Count} viewing sessions from disk.", loaded.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load viewing statistics from disk.");
        }
    }

    private void SaveToDisk()
    {
        try
        {
            var filePath = GetFilePath();
            if (filePath == null)
            {
                return;
            }

            List<ViewingSession> snapshot;
            lock (_lock)
            {
                snapshot = _sessions.ToList();
            }

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save viewing statistics to disk.");
        }
    }
}
