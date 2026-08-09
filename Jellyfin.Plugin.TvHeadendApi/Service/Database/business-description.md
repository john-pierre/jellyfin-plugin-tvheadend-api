# Database Service

Central SQLite infrastructure for the plugin: path management, connection factory, schema migrations, health monitoring, corruption recovery, data retention cleanup, and write serialization.

## Detailed Description

### Path & Connection Layer

`DatabaseProvider` centralizes the database file path (`tvheadend_plugin.db` in the plugin data folder), connection string construction, and `DbContextOptions<T>` creation for EF Core consumers. Pooling is disabled because writes are serialized and concurrency is low.

`DatabaseConnectionFactory` creates opened `SqliteConnection` instances with consistent PRAGMA settings: WAL journal mode, 5s busy timeout, foreign keys enabled, NORMAL synchronous. Provides both read-write and read-only connection methods.

`DatabaseWriteCoordinator` serializes all write operations across services using a `SemaphoreSlim(1,1)`. Exposes sync (`AcquireWrite`) and async (`AcquireWriteAsync`) acquisition, returning a disposable lock release. Prevents SQLite write contention from concurrent services.

### Schema Management

`DatabaseMigrationService` owns all DDL. Migrations are defined as ordered `MigrationDefinition` records (version, name, SQL statements) and executed idempotently within transactions. Current schema: 8 migrations covering `schema_version`, `viewing_session`, `health_transition`, `tvheadend_log_entry`, `plugin_log_entry`, `relay_request_metric`, `relay_token`, and `database_health_event`. Provides helper methods: `TableExists`, `ColumnExists`, `IndexExists`, `AddColumnIfMissing`, `CreateIndexIfMissing`.

### Health Monitoring

`DatabaseHealthService` is the central singleton that initializes the database at startup (integrity check → migration → table validation), tracks availability, and exposes health snapshots for the dashboard. Reports `DatabaseHealthStatus`: Unknown → Healthy ↔ Degraded → Corrupt → Recovering → Recovered/Unavailable. `RecordError` downgrades status; `RecordSuccess` promotes from Degraded back to Healthy. `GetSnapshot` returns a comprehensive `DatabaseHealthSnapshot` with file sizes, row counts per table, schema version, error history, and warning messages.

`DatabaseHealthStatus` is an enum: Unknown, Healthy, Degraded, Unavailable, Corrupt, Recovering, Recovered.

`DatabaseHealthSnapshot` is an immutable record with 25+ properties covering database file info, schema state, per-table row counts, error/recovery history, and human-readable warnings.

### Recovery

`DatabaseRecoveryService` handles corruption detection (`PRAGMA integrity_check`) and recovery. On corruption: clears connection pools, moves corrupt files (db, wal, shm) to timestamped backups, recreates the database, and runs all migrations from scratch. Tracks recovery count and timestamp.

### Cleanup

`DatabaseCleanupService` runs configurable retention policies across all tables: viewing sessions, health transitions, TVHeadend logs, plugin logs, relay metrics, expired tokens, and health events. All deletes go through `DatabaseWriteCoordinator`.

`DatabaseCleanupHostedService` is a hosted service that triggers `DatabaseCleanupService` every 24 hours (first run after 10 minutes), reading retention periods from plugin configuration.

### Error Classification

`DatabaseErrorClassifier` maps SQLite exceptions to categories: Corruption (error codes 11, 26), Locked (5, 6), IoError (10, 13, 14), SqliteError, or Unknown. Used by health tracking and recovery decisions.

## Domain Context

- **Use Case:** Reliable SQLite persistence for all plugin operational data (logs, metrics, tokens, viewing sessions, health transitions)
- **Module Type:** Infrastructure Service
- **Key Domain Entities:** DatabaseProvider, DatabaseConnectionFactory, DatabaseWriteCoordinator, DatabaseMigrationService, DatabaseHealthService, DatabaseRecoveryService, DatabaseCleanupService, DatabaseHealthSnapshot, DatabaseHealthStatus, MigrationDefinition

## Internal Dependencies

- **`Storage`** — `DataFolderPathProvider` for the plugin data folder path
- **`Configuration`** — `ConfigurationProvider` for retention period settings
