# Project Overview

## What This Is

Jellyfin plugin that integrates TVHeadend as a Live TV provider via TVHeadend's HTTP/JSON API only (no HTSP protocol).

- **Language:** C# 12, .NET 8.0
- **Plugin SDK:** Jellyfin Plugin SDK (`MediaBrowser` APIs)
- **Persistence:** SQLite via Entity Framework Core (`Microsoft.EntityFrameworkCore.Sqlite`)
- **Serialization:** `System.Text.Json` with shared `JsonDefaults.Api` options
- **Nullable reference types:** enabled, `TreatWarningsAsErrors` enabled
- **License:** GPL-3.0

## Entry Points

| File | Role |
|------|------|
| `Plugin.cs` | Plugin lifecycle, configuration page registration, static instance |
| `ServiceRegistrator.cs` | DI container wiring (all services registered as singletons) |
| `OrchestratorService.cs` | Implements `ILiveTvService` — thin delegation facade, no business logic |

## Architecture in One Sentence

`OrchestratorService` delegates to ~20 focused domain services which call a shared `ApiClient` backend that speaks HTTP/JSON to TVHeadend.

## Key Service Domains

| Domain | Purpose |
|--------|---------|
| Guide | Channels, EPG programs, content types, channel tags |
| Dvr | Single timers, series timers, recordings |
| Stream | Stream URL construction, media source info, lifecycle |
| Relay | Proxies TVHeadend streams through Jellyfin (hides credentials, token security) |
| Auth | Token generation/validation, digest authentication |
| Health | Aggregated plugin health state |
| Database | SQLite lifecycle, migrations, cleanup, recovery |
| Logging | Plugin log query, parsing, sanitization |
| Metric | `System.Diagnostics.Metrics` instruments for API calls, durations, cache hits/misses |
| StreamingProfile | Hierarchical rule-based profile selection and discovery |
| Dashboard | Admin status panel data aggregation |
| Diagnostic | Compatibility checks, configuration analysis |
| Profile | Profile resolution, container mapping, default profile creation |
| Statistic | Viewing session tracking with SQLite persistence |
| Comet | Buffers TVHeadend Comet log and disk-space updates |
| Resilience | Retry with exponential back-off and circuit breaker |
| Configuration | Plugin config access (`ConfigurationProvider`) and mutation (`ConfigurationSaver`) |
| Storage | Plugin path resolution (cache, data folder) |
| Backend | Low-level HTTP (`ApiClient`), URL building (`UrlBuilder`), grid pagination (`GridFetcher`) |
| Common | Shared utilities (`JsonDefaults`) |

## Build and Test

```bash
# Build (must pass with 0 warnings, 0 errors)
dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release

# Unit tests (excludes live/integration)
dotnet test Jellyfin.Plugin.TvHeadendApi.Tests/Jellyfin.Plugin.TvHeadendApi.Tests.csproj -c Release --no-build --filter "Category!=LiveIntegration"

# Live integration tests (requires Docker test stack)
dotnet test --filter "Category=LiveIntegration"
```

## External Dependencies

- Jellyfin Plugin SDK (`MediaBrowser.*`)
- `Microsoft.EntityFrameworkCore.Sqlite`
- StyleCop Analyzers, Serilog Analyzers, Multithreading Analyzers (build-time only)

No other runtime dependencies.
