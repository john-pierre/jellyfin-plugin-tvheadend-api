# Coding Standards

## Language and Framework

- .NET 8.0, C# 12
- Nullable reference types: **enabled** — avoid `!` suppression unless necessary (add explanatory comment)
- `TreatWarningsAsErrors`: **enabled** — zero warnings allowed
- `AnalysisMode`: AllEnabledByDefault

## Naming Conventions

### Folders
- **Singular names**: `Service/Auth/`, `Model/Guide/`, `Configuration/` (NOT `Services/`, `Models/`)

### Files
- PascalCase, must exactly match primary type name
- Namespace must match folder path: `Service/Auth/TokenValidator.cs` -> `Jellyfin.Plugin.TvHeadendApi.Service.Auth`

### Types
- No redundant prefixes — namespace provides context: `TokenValidator` in `Service.Auth` (NOT `AuthTokenValidator`)
- Suffixes: `Service` (lifecycle), `Resolver` (computed values), `Validator` (input validation), `Helper` (static utility)

### Fields
- Private: `_camelCase` (underscore prefix)
- Constants: `PascalCase`
- Parameters/locals: `camelCase`

### TVHeadend Naming
- **Never** abbreviate "tvheadend" to "tvh" in code identifiers (only in comments referring to the product)

## Architecture Rules

- **Controller layer**: thin — delegates to services, no business logic
- **Service layer**: domain logic organized by responsibility
- **Backend layer**: generic HTTP infrastructure — no domain logic
- **Model layer**: pure data shapes, no logic beyond property declarations
- **Dependency direction**: Controller -> Service -> Backend -> HTTP (never reverse)
- **All services registered as singletons** via `ServiceRegistrator`

## Dependency Injection

- Interface-driven: every service has an interface
- **Internal classes, public interfaces**
- Never access `Plugin.Instance` directly in services — use injected `ConfigurationProvider` or other providers
- Constructor validation: `ArgumentNullException.ThrowIfNull(parameter)` for all injected dependencies

## Async Patterns

- `Async` suffix on all async methods
- `ConfigureAwait(false)` on all awaited calls
- Forward `CancellationToken` to all async calls (`CA2016` enforced as error)
- No blocking I/O in async paths

## Error Handling

- Catch specific exceptions, not `Exception` base
- Return null or empty collections for "not found" scenarios (don't throw)
- Use structured logging with static message templates (`CA2254` enforced)
- Argument validation: `ArgumentNullException.ThrowIfNull`, `ArgumentException.ThrowIfNullOrWhiteSpace`

## Documentation

- XML doc comments on all public members
- All code, comments, variables, documentation: **English**

## Import Organization

Order (alphabetical within each group):
1. `System` / `System.*`
2. `Jellyfin.Plugin.TvHeadendApi.*`
3. `MediaBrowser.*`
4. `Microsoft.Extensions.*`

## Analyzers (enforced)

| Analyzer | Scope |
|----------|-------|
| StyleCop Analyzers | Code style (with exceptions: SA1101, SA1200, SA1309, SA1600, SA1602, SA1633 disabled) |
| Serilog Analyzers | Log template correctness |
| Multithreading Analyzers | Thread safety |
| CA1305 | Always specify `IFormatProvider` |
| CA2016 | Always forward `CancellationToken` |
| CA2254 | No string interpolation in logger calls |

## Build Gate

The build must pass with **0 warnings and 0 errors**. Any analyzer violation fails the build.
