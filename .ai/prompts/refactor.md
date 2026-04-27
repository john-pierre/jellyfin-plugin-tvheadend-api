# Refactor

Safely refactor a service or component in jellyfin-plugin-tvheadend-api.

## Steps

### 1. Analyze dependencies

- Identify every consumer of the target type (callers, DI registrations in `ServiceRegistrator.cs`, interface references).
- Map cross-domain dependencies. The 24 service domains live under `Jellyfin.Plugin.TvHeadendApi/Service/`:
  Auth, Backend, Comet, Common, Configuration, Dashboard, Database, Diagnostic, Dvr, Guide, Health, Input, Logging, Metric, Profile, Relay, Resilience, Statistic, Status, Storage, Stream, StreamingProfile, Subscription (plus `OrchestratorService.cs` at root).
- Check API controllers under `Api/` and `Api/Endpoint/` for direct usage.
- Note any `IHostedService` registrations that depend on the target.

### 2. Check test coverage

- Locate corresponding tests under `Jellyfin.Plugin.TvHeadendApi.Tests/Service/<Domain>/`.
- Run existing tests for the affected domain:
  ```
  dotnet test Jellyfin.Plugin.TvHeadendApi.Tests -c Release --no-build --filter "FullyQualifiedName~<Domain>"
  ```
- Record which behaviors are covered and which are not.

### 3. Propose changes

- Present a concrete diff plan: what moves, what renames, what splits.
- Preserve all existing public API contracts unless the refactor intentionally changes them.
- Keep interface-implementation pairs together within their service folder.
- Maintain the `business-description.md` in each affected service folder.

### 4. Implement

- Apply changes one service domain at a time.
- Update `ServiceRegistrator.cs` registrations if types move or rename.
- Update `using` statements across all consumers.
- Nullable reference types are enabled — do not suppress warnings without justification.

### 5. Verify

- Build must pass with zero warnings:
  ```
  dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release
  ```
- All unit tests must pass:
  ```
  dotnet test Jellyfin.Plugin.TvHeadendApi.Tests -c Release --no-build --filter "Category!=LiveIntegration"
  ```
- Confirm no unintended behavioral changes by reviewing test output.

### 6. Update documentation

- Update the `business-description.md` in every affected service folder.
- If domain boundaries changed, note it for architecture docs.
