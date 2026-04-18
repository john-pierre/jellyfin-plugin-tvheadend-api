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
- **File name MUST exactly match the primary type name**
- Example: `TokenValidator.cs` contains the `TokenValidator` class
- Exception: None. This rule is absolute.

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
│   ├── Helper/                        → Jellyfin.Plugin.TvHeadendApi.Service.Helper
│   │   ├── IdNodeValueHelper.cs       → public class IdNodeValueHelper { }
│   │   ├── ApiClient.cs               → public class ApiClient { }
│   │   ├── UrlBuilder.cs              → public class UrlBuilder { }
│   │   ├── GridFetcher.cs             → public class GridFetcher { }
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
| **Service.Profile** | TVHeadend profile configuration and provisioning | `ProfileResolver`, `ProvisioningService`, `ProfileDetails` |
| **Service.Stream** | Stream URL construction and stream lifecycle | `MediaSourceService`, stream URL building |
| **Service.Diagnostic** | Plugin diagnostics and health checks | `DiagnosticService` |
| **Service.Helper** | Low-level HTTP and URL helpers | `ApiClient`, `UrlBuilder`, `ResiliencePolicies`, `PluginMetrics` |

### Service Type Suffixes
- **Service**: Full lifecycle service (e.g., `DiagnosticService`, `ProvisioningService`)
- **Resolver**: Returns computed/resolved values (e.g., `ProfileResolver`)
- **Validator**: Validates input according to rules (e.g., `TokenValidator`)
- **Helper**: Static utility methods (e.g., `ProfileMappingHelper`)
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
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;
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
- **Usage**: `TokenValidator.IsAlphanumeric(token)`

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

## Refactoring Checklist

When adding or moving files and folders:

- [ ] All folder names are **singular** (e.g., `Service/`, not `Services/`; `Model/`, not `Models/`)
- [ ] File name matches class name exactly (e.g., `TokenValidator.cs`)
- [ ] Namespace path matches folder structure
- [ ] Class uses appropriate suffix: `-Service`, `-Resolver`, `-Validator`, `-Helper`
- [ ] No redundant prefixes in class names (context from namespace is sufficient)
- [ ] All imports are updated in consuming files
- [ ] Imports are organized per the order above
- [ ] Test file imports match source code structure
- [ ] No leftover old files or broken imports

## Related Documentation

- See `AGENTS.md` for agent workflow and repository context
- See `docs/architecture/architecture-overview.md` for architectural layers
- See `docs/architecture/module-responsibilities.md` for module boundaries
- See `README.md` for project overview

