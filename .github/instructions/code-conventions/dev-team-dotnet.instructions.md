# C# / .NET Coding Conventions — jellyfin-plugin-tvheadend-api

> Authoritative source for all C#/.NET coding standards in this project.

---

## 1 — Language & Framework

- **Target:** .NET 8.0, C# 12
- **Nullable:** enabled (`<Nullable>enable</Nullable>`) — avoid `!` suppression unless necessary with explanatory comment
- **TreatWarningsAsErrors:** enabled — zero warnings allowed
- **AnalysisMode:** AllEnabledByDefault

---

## 2 — Analyzers & Rules

Configured via `Jellyfin.Plugin.TvHeadendApi/Jellyfin.ruleset`:

### StyleCop — Disabled Rules
| Rule | Reason |
|------|--------|
| SA1101 | `this.` prefix not required |
| SA1200 | Using directives outside namespace allowed |
| SA1309 | Underscore-prefixed fields allowed (`_fieldName`) |
| SA1600, SA1602 | XML docs not required on all elements |
| SA1633 | File header not required |

### Errors (must fix)
| Rule | Enforcement |
|------|-------------|
| CA1305 | Always specify `IFormatProvider` |
| CA2016 | Always forward `CancellationToken` |
| CA2254 | Use static log message templates (no string interpolation in logger calls) |

---

## 3 — Naming Conventions

Full reference: `docs/guides/naming-conventions.md`

### Key Rules
- **Folders:** Singular (`Service/`, `Model/`, `Configuration/`)
- **Files:** PascalCase, must exactly match primary type name
- **Namespace:** Must match folder path (`Service/Auth/TokenValidator.cs` → `Jellyfin.Plugin.TvHeadendApi.Service.Auth`)
- **No redundant prefixes:** Namespace provides context (✓ `TokenValidator` in `Service.Auth`, ✗ `AuthTokenValidator`)

### Type Suffixes
| Suffix | Usage |
|--------|-------|
| `Service` | Full lifecycle service |
| `Resolver` | Returns computed/resolved values |
| `Validator` | Validates input |
| `Helper` | Static utility methods |

### Field Naming
- Private fields: `_camelCase` with underscore prefix
- Constants: `PascalCase`
- Parameters/locals: `camelCase`

---

## 4 — Import Organization

Order (alphabetical within each group):
1. `System` / `System.*`
2. `Jellyfin.Plugin.TvHeadendApi.*`
3. `MediaBrowser.*`
4. `Microsoft.Extensions.*`

---

## 5 — Architecture Rules

- **Controller layer:** Thin — delegates to services, no business logic
- **Service layer:** Domain logic organized by responsibility (Guide, Dvr, Stream, Auth, Profile, etc.)
- **Backend layer:** Generic HTTP infrastructure — no domain logic
- **Model layer:** Pure data shapes, no logic beyond property declarations
- **Dependency direction:** Controller → Service → Backend → HTTP (never reverse)
- **All services registered as singletons** via `ServiceRegistrator`

---

## 6 — Error Handling

- Use structured logging with static message templates
- Catch specific exceptions, not `Exception` base
- Return null or empty collections for "not found" scenarios (don't throw)
- Use `CancellationToken` on all async methods and forward to all async calls

---

## 7 — Testing Standards

Full reference: `docs/guides/test-strategy.md`

- **Framework:** xUnit + FluentAssertions + Moq + AutoFixture
- **Naming:** `{MethodName}_{Scenario}_{ExpectedResult}`
- **Class naming:** `{ClassUnderTest}Tests`
- **Location:** Mirror source folder structure under `Tests/`
- **Coverage:** 90% minimum (CI-enforced), 95% target
- **Rules:** No network calls, no filesystem access, no Thread.Sleep, no test ordering

