# Feature

Add a new feature to jellyfin-plugin-tvheadend-api.

## Steps

### 1. Plan

- Define the feature scope: what it does, which service domains it touches.
- Identify where it fits in the existing domain structure under `Service/`. The 24 domains are:
  Auth, Backend, Comet, Common, Configuration, Dashboard, Database, Diagnostic, Dvr, Guide, Health, Input, Logging, Metric, Profile, Relay, Resilience, Statistic, Status, Storage, Stream, StreamingProfile, Subscription.
- Decide if this needs a new service folder or extends an existing one.
- Check `docs/architecture/module-responsibilities.md` for boundary rules.
- Use terms from `docs/guides/naming-conventions.md` and the domain language glossary.

### 2. Create interface and implementation

- Define an interface (`I<ServiceName>.cs`) in the appropriate `Service/<Domain>/` folder.
- Implement the service class in the same folder.
- Follow project conventions:
  - Constructor injection for all dependencies.
  - `ILogger<T>` with static message templates (no string interpolation).
  - Forward `CancellationToken` to all async calls.
  - Nullable reference types are enabled — handle nulls explicitly.
  - XML doc comments on all public members.

### 3. Register in ServiceRegistrator

- Add the service registration in `ServiceRegistrator.cs`.
- Use singleton lifetime (project convention) unless there is a specific reason for scoped/transient.
- If the feature includes a background task, register as `IHostedService`.
- If it needs an `HttpClient`, register via `AddHttpClient` with appropriate handlers.

### 4. Add API endpoints (if needed)

- Add controller actions to the appropriate controller in `Api/`:
  - `PluginController` — plugin configuration and general endpoints.
  - `DashboardController` — dashboard data aggregation.
  - `Api/Endpoint/` — feature-specific controllers.
- Use `[Authorize(Policy = "RequiresElevation")]` for admin-only endpoints.

### 5. Add tests

- Create test class(es) in `Jellyfin.Plugin.TvHeadendApi.Tests/Service/<Domain>/`.
- Test naming: `{MethodName}_{Scenario}_{ExpectedResult}`.
- Framework: xUnit + Moq + `NullLogger<T>.Instance`.
- Cover: happy path, edge cases, error conditions, cancellation.
- Minimum 90% coverage on new code (95% target).

### 6. Verify

- Build with zero warnings:
  ```
  dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release
  ```
- All tests pass:
  ```
  dotnet test Jellyfin.Plugin.TvHeadendApi.Tests -c Release --no-build --filter "Category!=LiveIntegration"
  ```

### 7. Update documentation

- Create or update `business-description.md` in the affected service folder(s).
- If the feature adds configuration options, update `PluginConfiguration` and the `ConfigPage.html`.
- If the feature adds dashboard data, update `DashboardPage.html`.
