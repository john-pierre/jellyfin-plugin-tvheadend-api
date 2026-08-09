# Storage Service

Lambda-based path providers for plugin file system locations.

## Detailed Description

`CachePathProvider` wraps a `Func<string?>` delegate that resolves the plugin's cache directory path at call time. Used by services that need to read or write cached files (e.g. mediainfo cache) without depending on `Plugin.Instance`.

`DataFolderPathProvider` wraps a `Func<string?>` delegate that resolves the plugin's data folder path at call time. Used by `DatabaseProvider` and other services that need the persistent data directory. Returns `null` when the plugin instance is unavailable.

Both follow the same indirection pattern as `ConfigurationProvider` — delegates registered in DI point to `Plugin.Instance` properties, decoupling services from the static singleton.

## Domain Context

- **Use Case:** Testable file path resolution without static plugin instance coupling
- **Module Type:** Infrastructure
- **Key Domain Entities:** CachePathProvider, DataFolderPathProvider
