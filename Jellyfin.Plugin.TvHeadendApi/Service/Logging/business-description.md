# Logging Service

Plugin-scoped logging with SQLite persistence, configurable log level override, and TVHeadend log import.

## Detailed Description

`PluginLogService` is a hosted service that persists plugin and TVHeadend log entries to SQLite via a bounded in-memory queue (2000 entries, non-blocking enqueue). A background writer task drains the queue in batches of 50. If DB persistence fails once, DB logging is disabled for the session to prevent cascading failures. Provides query capabilities for the dashboard with filtering by source, level, log type, and free-text search, plus configurable sort order.

Plugin log entries are enqueued via `EnqueuePluginLog` (called by `PluginLogPersistenceProvider`). TVHeadend log lines are enqueued via `EnqueueTvHeadendLog`, which delegates to `LogParser` for structured extraction before persistence.

`PluginLogPersistenceProvider` is an `ILoggerProvider` registered with Jellyfin's logging pipeline. It transparently intercepts every log entry from plugin-namespace categories (`Jellyfin.Plugin.TvHeadendApi*`) — regardless of how a given service obtained its logger — and enqueues qualifying entries to `PluginLogService` for SQLite persistence. The plugin-specific log level override is applied via `PluginLogLevelPolicy`: `JellyfinDefault` persists everything Jellyfin's framework delivers, while any concrete level raises the minimum and drops lower-level entries before persistence. Because the framework filters before this provider runs, the override can only filter down — it cannot capture entries more verbose than Jellyfin's global log level.

`LogParser` parses TVHeadend log lines into structured components (timestamp, level, category, message) supporting multiple TVHeadend log formats: `timestamp [LEVEL] message`, `timestamp LEVEL: message`, and `[LEVEL] message`. Extracts subsystem category (e.g. `mpegts`, `linuxdvb`) from the message prefix. Computes SHA-256 hash for deduplication.

`LogSanitizer` masks sensitive data in log messages before persistence: auth tokens, passwords, Authorization headers, and query-string secrets are replaced with `***REDACTED***` using compiled regex patterns.

## Domain Context

- **Use Case:** Dashboard log viewer with plugin-scoped log level control and credential-safe persistence
- **Module Type:** Service (Hosted)
- **Key Domain Entities:** PluginLogEntry, LogParseResult

## Internal Dependencies

- **`Configuration`** — `ConfigurationProvider` for log level override and feature flags (SQLite logging, sanitization)
- **`Database`** — `DatabaseHealthService` for availability checks before persistence
