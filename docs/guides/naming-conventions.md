# Naming and Structure Conventions

This document defines the naming standards for the Jellyfin TVHeadend API Plugin to maintain consistent organization and reduce refactoring needs.

## Folder and File Naming

### Core Rule: Singular Names Only
- **All folder names MUST be singular** (not plural)
- Examples:
  - ✓ `Service/` (not `Services/`)
  - ✓ `Model/` (not `Models/`)
  - ✓ `Configuration/` (not `Configurations/`)
  - ✓ `Property/` (not `Properties/`)
- **Rationale**: Singular names are the standard convention for directory structure. They represent the category/domain (everything in `Model/` is model-related), not a collection of items.
- **Documented exception**: `Service/Metrics/` (plural). "Metrics" is the established .NET term (`System.Diagnostics.Metrics`) and the plural form was already entrenched across the codebase; the former singular `Service/Metric/` folder was merged INTO `Service/Metrics/` so instrumentation and session telemetry live in one namespace instead of two near-identical ones.

### Markdown File Names
- **The root `README.md` is the only README in UPPERCASE.**
- All other markdown documentation files use **lowercase with hyphens** (e.g., `readme.md`, `overview.md`).
- **No redundant parent-folder prefix** — if the file is in `architecture/`, do NOT prefix the file with `architecture-`. The folder provides context.
  - ✓ `docs/architecture/decisions.md` (not `architecture-decisions.md`)
  - ✓ `docs/architecture/overview.md` (not `architecture-overview.md`)
- **Use `-` (hyphen) instead of `_` (underscore)** in all file names where a separator is needed.
  - ✓ `overview.md` (not `overview_decisions.md`)
  - ✓ `test-strategy.md` (not `test_strategy.md`)
  - ✓ `docker-compose.yaml` (not `docker_compose.yaml`)
- **Rationale**: Hyphens are URL-friendly, easier to read, and the de-facto standard in open-source documentation.

## File and Class Naming

### Core Rule
- **Each code file contains exactly one top-level type and the file name MUST exactly match it**
- Example: `TokenValidator.cs` contains the `TokenValidator` class
- Supporting DTOs, providers and value objects belong in their own files.
- Exception: None. This rule is absolute.
- **Enforced by the build**, not just documented: `SA1402` (file may only contain a single type)
  and `SA1649` (file name must match the first type name) are active in `Jellyfin.ruleset`, and
  `TreatWarningsAsErrors` turns a violation into a build failure.
  `Jellyfin.Plugin.TvHeadendApi.Tests/FileNamingConventionTests.cs` is the backstop.
- Types **nested inside** another type are not affected — the rule is about top-level declarations.

### Class Naming Style
- Use **PascalCase** for all class names
- Examples:
  - `TokenValidator` (utility for token validation)
  - `ProfileResolver` (service for profile resolution)
  - `AuthTokenValidator` ❌ (redundant, confusing - "Auth" prefix is implicit in Service.Auth namespace)
  - `TokenValidator` ✓ (clear, namespace provides context)

## Namespace Hierarchy

### Folder Structure MUST Match Namespace

```
Jellyfin.Plugin.TvHeadendApi/
├── Service/
│   ├── Auth/                          → Jellyfin.Plugin.TvHeadendApi.Service.Auth
│   │   ├── TokenValidator.cs          → public class TokenValidator { }
│   ├── Profile/                       → Jellyfin.Plugin.TvHeadendApi.Service.Profile
│   │   ├── ProfileResolver.cs         → public class ProfileResolver { }
│   │   ├── DefaultProfileService.cs   → public class DefaultProfileService { }
│   │   ├── ProfileContainerResolver.cs→ public class ProfileContainerResolver { }
│   │   ├── ProfileMappingHelper.cs    → public class ProfileMappingHelper { }
│   ├── Stream/                        → Jellyfin.Plugin.TvHeadendApi.Service.Stream
│   │   ├── MediaSourceService.cs      → public class MediaSourceService { }
│   │   ├── LifecycleService.cs        → public class LifecycleService { }
│   ├── Diagnostic/                    → Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic
│   │   ├── DiagnosticService.cs       → public class DiagnosticService { }
│   │   ├── EncodingOptionsReader.cs   → public class EncodingOptionsReader { }
│   ├── Backend/                        → Jellyfin.Plugin.TvHeadendApi.Service.Backend
│   │   ├── IdNodeValueHelper.cs       → public class IdNodeValueHelper { }
│   │   ├── ApiClient.cs               → public class ApiClient { }
│   │   ├── UrlBuilder.cs              → public class UrlBuilder { }
│   │   ├── GridFetcher.cs             → public class GridFetcher { }
│   ├── Resilience/                     → Jellyfin.Plugin.TvHeadendApi.Service.Resilience
│   │   ├── ResiliencePolicies.cs      → public class ResiliencePolicies { }
│   ├── Metrics/                        → Jellyfin.Plugin.TvHeadendApi.Service.Metrics (documented plural exception)
│   │   ├── MetricService.cs           → internal static class MetricService { }
│   │   ├── SessionTracker.cs          → public sealed class SessionTracker { }
│   ├── Configuration/                  → Jellyfin.Plugin.TvHeadendApi.Service.Configuration
│   │   ├── ConfigurationProvider.cs   → public class ConfigurationProvider { }
│   │   ├── ConfigurationSaver.cs      → public class ConfigurationSaver { }
│   ├── Storage/                        → Jellyfin.Plugin.TvHeadendApi.Service.Storage
│   │   ├── CachePathProvider.cs       → public class CachePathProvider { }
│   │   ├── DataFolderPathProvider.cs  → public class DataFolderPathProvider { }
│   ├── Common/                         → Jellyfin.Plugin.TvHeadendApi.Service.Common
│   │   ├── JsonDefaults.cs            → public class JsonDefaults { }
├── Api/                               → Jellyfin.Plugin.TvHeadendApi.Api
│   ├── PluginController.cs            → public class PluginController { }
├── Configuration/                     → Jellyfin.Plugin.TvHeadendApi.Configuration
│   ├── PluginConfiguration.cs         → public class PluginConfiguration { }
├── Model/                             → Jellyfin.Plugin.TvHeadendApi.Model
│   ├── ProfileListResponse.cs         → public class ProfileListResponse { }
├── Property/                          → (AssemblyInfo.cs and other metadata)
```

### Namespace Declaration
Every C# file must declare its namespace matching its folder path:
```csharp
// In file: Service/Auth/TokenValidator.cs
namespace Jellyfin.Plugin.TvHeadendApi.Service.Auth;

internal static class TokenValidator { }
```

## Service Naming and Responsibility

### Service Layer Concerns
Services are organized by **domain responsibility**, not by implementation detail:

| Service Folder | Responsibility | Examples |
|---|---|---|
| **Service.Auth** | Token validation and authentication | `TokenValidator` - validates TVHeadend user tokens |
| **Service.Profile** | TVHeadend profile configuration and provisioning | `ProfileResolver`, `DefaultProfileService`, `ProfileDetails` |
| **Service.Stream** | Stream URL construction and stream lifecycle | `MediaSourceService`, stream URL building |
| **Service.Diagnostic** | Plugin diagnostics and health checks | `DiagnosticService` |
| **Service.Backend** | Low-level HTTP and URL infrastructure | `ApiClient`, `UrlBuilder`, `GridFetcher`, `IdNodeValueHelper` |
| **Service.Resilience** | Retry and circuit breaker policies | `ResiliencePolicies`, `FailureClassifier` |
| **Service.Metrics** | Metrics instrumentation + in-memory stream session telemetry | `MetricService`, `SessionTracker`, `MetricsAggregator` |
| **Service.Configuration** | Plugin configuration access and mutation | `ConfigurationProvider`, `ConfigurationSaver` |
| **Service.Storage** | Plugin path resolution | `CachePathProvider`, `DataFolderPathProvider` |
| **Service.Common** | Shared utilities | `JsonDefaults` |

### Service Type Suffixes
- **Service**: Full lifecycle service (e.g., `DiagnosticService`, `ProvisioningService`, `MediaSourceService`)
- **Resolver**: Returns computed/resolved values (e.g., `ProfileResolver`, `ProfileContainerResolver`)
- **Validator**: Validates input according to rules (e.g., `TokenValidator`)
- **Helper**: Static utility methods or utility classes (e.g., `ProfileMappingHelper`, `IdNodeValueHelper`)
- **Provider**: Supplies/resolves dependencies or configuration (e.g., `ConfigurationProvider`, `CachePathProvider`)
- **Saver**: Persists/saves data (e.g., `ConfigurationSaver`)
- **Reader**: Reads or extracts data (e.g., `EncodingOptionsReader`)
- **Handler**: HTTP/network middleware (e.g., `DigestAuthHandler`, `ResilienceHandler`)
- **Fetcher**: Retrieves/fetches data from sources (e.g., `GridFetcher`)
- **Context**: Database context or request context (e.g., `ViewingSessionContext`)
- **Manager**: Resource management (reserved for future use)

## Import Organization

### Import Order
1. System namespaces (`using System;`)
2. System.* namespaces (`using System.Linq;`)
3. Plugin namespaces (`using Jellyfin.Plugin.TvHeadendApi...;`)
4. Jellyfin framework (`using MediaBrowser...;`)
5. Microsoft.Extensions (`using Microsoft.Extensions...;`)

### Alphabetical Within Groups
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Configuration;
using Jellyfin.Plugin.TvHeadendApi.Service.Auth;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Profile;
using Jellyfin.Plugin.TvHeadendApi.Service.Stream;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;
```

## Examples of Naming Decisions

### ✓ Correct: `TokenValidator`
- **Why**: Short, clear, namespace provides context that it's for authentication
- **Location**: `Service/Auth/TokenValidator.cs`
- **Namespace**: `Jellyfin.Plugin.TvHeadendApi.Service.Auth`
- **Usage**: `TokenValidator.IsValidTokenFormat(token)`

### ✗ Incorrect: `AuthTokenValidator`
- **Why**: Redundant prefix; "Auth" is already in the namespace
- **Problem**: Creates cognitive load; verbose without adding clarity

### ✓ Correct: `ProfileResolver`
- **Why**: Indicates it resolves/computes profile data
- **Location**: `Service/Profile/ProfileResolver.cs`
- **Namespace**: `Jellyfin.Plugin.TvHeadendApi.Service.Profile`
- **Usage**: `_resolver.ResolveProfileAsync()`

### ✓ Correct: `MediaSourceService`
- **Why**: Clear that it manages media source operations for streams
- **Location**: `Service/Stream/MediaSourceService.cs`
- **Namespace**: `Jellyfin.Plugin.TvHeadendApi.Service.Stream`

### ✗ Incorrect: Placing `TokenValidator` in `Service.Profile`
- **Why**: Authentication tokens are not profile configuration
- **Correct Location**: `Service/Auth/`
- **Rationale**: Separates concerns; "Validation" and "Profile" are different domains

### ✓ Correct: `ConfigurationProvider`
- **Why**: Supplies/provides the plugin configuration without direct coupling
- **Location**: `Service/Configuration/ConfigurationProvider.cs`
- **Namespace**: `Jellyfin.Plugin.TvHeadendApi.Service.Configuration`
- **Usage**: Constructor injection; provides lazily-resolved configuration

### ✓ Correct: `DigestAuthHandler`
- **Why**: HTTP middleware handler for digest authentication
- **Location**: `Service/Auth/DigestAuthHandler.cs`
- **Namespace**: `Jellyfin.Plugin.TvHeadendApi.Service.Backend`
- **Usage**: Registered in `DelegatingHandler` chain for HTTP client

### ✓ Correct: `EncodingOptionsReader`
- **Why**: Reads/extracts encoding options from server configuration
- **Location**: `Service/Diagnostic/EncodingOptionsReader.cs`
- **Namespace**: `Jellyfin.Plugin.TvHeadendApi.Service.Diagnostic`
- **Usage**: `_reader.ReadFfmpegSettings(...)`

## Refactoring Checklist

When adding or moving files and folders:

- [ ] All folder names are **singular** (e.g., `Service/`, not `Services/`; `Model/`, not `Models/`) — sole documented exception: `Service/Metrics/`
- [ ] File name matches class name exactly (e.g., `TokenValidator.cs`)
- [ ] Namespace path matches folder structure
- [ ] Class uses appropriate suffix: `-Service`, `-Resolver`, `-Validator`, `-Helper`, `-Provider`, `-Saver`, `-Reader`, `-Handler`, `-Fetcher`, `-Context`
- [ ] No redundant prefixes in class names (context from namespace is sufficient)
- [ ] All imports are updated in consuming files
- [ ] Imports are organized per the order above
- [ ] Test file imports match source code structure
- [ ] No leftover old files or broken imports

## Related Documentation

- See `AGENTS.md` for agent workflow and repository context
- See `docs/architecture/overview.md` for architectural layers
- See `docs/architecture/module-responsibilities.md` for module boundaries
- See `README.md` for project overview

## SQL Schema Naming

SQLite tables and columns use `lowercase_with_underscore`, not PascalCase — e.g. `relay_token`,
`expires_at_utc`, `plugin_log_entry`. EF Core entity properties stay PascalCase and are mapped.

Raw-SQL comparisons against `DateTime` columns must format the value with
`Service/Database/SqliteDateTimeFormat.cs`; the round-trip specifier `"o"` produces a different
separator than the TEXT EF Core writes and silently mis-compares.
