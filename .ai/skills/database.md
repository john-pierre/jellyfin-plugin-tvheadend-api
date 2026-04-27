# Database Skill

SQLite persistence layer for jellyfin-plugin-tvheadend-api.

## Architecture

The database layer uses SQLite via Entity Framework Core with 3 distinct `DbContext` types:

| Context | Location | Purpose |
|---------|----------|---------|
| `RelayTokenDbContext` | `Service/Relay/` | Relay token storage and validation |
| `RelayMetricsContext` | `Service/Relay/` | Relay request metrics persistence |
| PluginLog context | `Service/Logging/` | Plugin log entry persistence |

All contexts are registered in `ServiceRegistrator.cs` and configured by `DatabaseProvider`.

## Schema conventions

- Column names: `lowercase_with_underscore` (e.g., `channel_id`, `created_at`).
- Table names: `lowercase_with_underscore`.
- Primary keys: auto-increment integers or string identifiers depending on domain.

## Write coordination

**All database writes must go through `DatabaseWriteCoordinator`** (`Service/Database/DatabaseWriteCoordinator.cs`).

- Serializes concurrent writes to prevent SQLite locking issues.
- Accepts a `Func<Task>` write operation and a `CancellationToken`.
- Never write to SQLite directly from service code — always use the coordinator.

## Migrations

`DatabaseMigrationService` (`Service/Database/DatabaseMigrationService.cs`):

- Version-gated: each migration has a version number and only runs if the database is below that version.
- Runs at plugin startup via `DatabaseHealthService`.
- Migrations are forward-only. No rollback support — use `DatabaseRecoveryService` for corruption.

## Cleanup

`DatabaseCleanupService` (`Service/Database/DatabaseCleanupService.cs`):

- Retention-based: removes records older than the configured retention period.
- Runs periodically via `DatabaseCleanupHostedService` (registered as `IHostedService`).
- Retention periods are configured in `PluginConfiguration` (see `StatisticsRetentionPeriod`).

## Health

`DatabaseHealthService` (`Service/Database/DatabaseHealthService.cs`):

- Initialization: creates database files and runs migrations on startup.
- Integrity check: runs SQLite `PRAGMA integrity_check`.
- Reports health status via `DatabaseHealthSnapshot` and `DatabaseHealthStatus`.

## Recovery

`DatabaseRecoveryService` (`Service/Database/DatabaseRecoveryService.cs`):

- Triggered when corruption is detected.
- Creates a backup of the corrupted database file.
- Rebuilds from scratch (schema recreation + migration).
- Logs the recovery process.

## Error classification

`DatabaseErrorClassifier` (`Service/Database/DatabaseErrorClassifier.cs`):

- Classifies SQLite exceptions into actionable categories (corruption, locking, disk full, etc.).
- Used by health and recovery services to determine the appropriate response.

## Connection management

`DatabaseConnectionFactory` (`Service/Database/DatabaseConnectionFactory.cs`):

- Creates and configures SQLite connections with appropriate pragmas.
- Sets WAL mode, busy timeout, and other SQLite-specific options.

## Rules

- Never bypass `DatabaseWriteCoordinator` for writes.
- Always use parameterized queries — no string concatenation for SQL.
- Always forward `CancellationToken` through database operations.
- Schema changes require a new migration in `DatabaseMigrationService`.
- Test database operations with in-memory SQLite or temporary files.
