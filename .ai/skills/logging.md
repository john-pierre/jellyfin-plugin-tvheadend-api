# Logging Skill

Plugin-scoped logging for jellyfin-plugin-tvheadend-api.

## Overview

The plugin has its own logging subsystem that runs alongside Jellyfin's built-in logging. It provides plugin-specific log level control, log persistence to SQLite, and credential redaction. All logging code lives in `Service/Logging/`.

## Components

### PluginLoggerFactory

`PluginLoggerFactory` (`Service/Logging/PluginLoggerFactory.cs`) — implements `IPluginLoggerFactory`:

- Creates plugin-scoped `ILogger<T>` instances.
- Applies the plugin-specific log level override from `PluginConfiguration.PluginLogLevel`.
- Loggers created through this factory participate in both Jellyfin's logging pipeline and the plugin's own log persistence.

### PluginLogService

`PluginLogService` (`Service/Logging/PluginLogService.cs`):

- Registered as `IHostedService` — runs for the lifetime of the plugin.
- Maintains a bounded async queue of log entries.
- Persists log entries to SQLite asynchronously (does not block the calling code).
- Queryable via `IPluginLogQueryService` for the dashboard log viewer.

### LogSanitizer

`LogSanitizer` (`Service/Logging/LogSanitizer.cs`):

- Regex-based credential redaction applied to log messages before persistence.
- Redacts: passwords, authentication tokens, auth headers, URLs with embedded credentials.
- Applied automatically — services do not need to sanitize manually.

### LogParser

`LogParser` (`Service/Logging/LogParser.cs`):

- Parses TVHeadend's log output formats.
- Extracts structured fields: timestamp, severity, subsystem, message.
- Multiple format support (syslog-style, TVHeadend internal).

## Log level configuration

- `PluginConfiguration.PluginLogLevel` — override log level for plugin loggers.
- Defined in `Configuration/PluginLogLevel.cs` (enum).
- Allows the plugin to log at Debug level while Jellyfin runs at Information.

## Rules

### Never log secrets

These must never appear in log output:
- TVHeadend passwords or credentials.
- Authentication tokens (both plugin auth tokens and relay HMAC tokens).
- Authorization headers.
- URLs containing embedded credentials.

`LogSanitizer` is the safety net, but services should avoid passing secrets to loggers in the first place.

### Logger usage conventions

```csharp
// Correct: static message template
_logger.LogInformation("Channel {ChannelId} resolved to profile {ProfileName}", channelId, profileName);

// Wrong: string interpolation (defeats structured logging and analyzers will flag it)
_logger.LogInformation($"Channel {channelId} resolved to profile {profileName}");
```

- Use `ILogger<T>` injected via constructor.
- Use static message templates with named placeholders.
- No string interpolation in logger calls.
- Appropriate levels: `Debug` for flow tracing, `Information` for state changes, `Warning` for recoverable issues, `Error` for failures with stack traces.

### Log persistence

- Log entries are queued, not written synchronously.
- The bounded queue prevents memory exhaustion under heavy logging.
- Persistence uses `DatabaseWriteCoordinator` for SQLite writes.
- Retention-based cleanup removes old entries (configurable via `PluginConfiguration`).
