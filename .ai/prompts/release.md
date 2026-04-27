# Release Readiness Review

Verify jellyfin-plugin-tvheadend-api is ready for release.

## Steps

### 1. Build verification

- Clean build with zero warnings and zero errors:
  ```
  dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release
  ```
- StyleCop and Roslyn analyzers pass (enforced via `Jellyfin.ruleset`).

### 2. Test pass

- All unit tests pass:
  ```
  dotnet test Jellyfin.Plugin.TvHeadendApi.Tests -c Release --no-build --filter "Category!=LiveIntegration"
  ```
- Coverage meets minimum threshold (90% CI-enforced, 95% target).
- If live integration tests are available, run with Docker:
  ```
  dotnet test --filter "Category=LiveIntegration"
  ```

### 3. CHANGELOG

- `CHANGELOG.md` has an entry for this version.
- All user-facing changes are documented: features, fixes, breaking changes.
- Breaking changes clearly marked with migration instructions.

### 4. Version management

- `.release-please-manifest.json` reflects the intended version.
- `manifest.json` plugin metadata (version, target ABI) is correct.
- `Jellyfin.Plugin.TvHeadendApi.csproj` version properties are consistent.

### 5. Code quality markers

- Search for `TODO`, `HACK`, `FIXME`, `TEMP` markers:
  ```
  grep -r "TODO\|HACK\|FIXME\|TEMP" Jellyfin.Plugin.TvHeadendApi/ --include="*.cs"
  ```
- All markers either resolved or documented as intentional with tracking issue.

### 6. Configuration compatibility

- `PluginConfiguration` defaults have not changed in a breaking way.
- New configuration options have sensible defaults that preserve existing behavior.
- `ConfigPage.html` reflects all configurable options.

### 7. Dashboard compatibility

- `DashboardPage.html` loads without errors.
- `DashboardService` correctly aggregates all subsystem data.
- API endpoints on `PluginController`, `DashboardController`, and `DashboardLogsController` return expected responses.

### 8. Database migrations

- `DatabaseMigrationService` handles upgrade from previous version.
- Migration is version-gated (only runs when needed).
- `DatabaseRecoveryService` can recover from corruption (backup + rebuild).

### 9. Security review

- No secrets in source (tokens, passwords, connection strings).
- `LogSanitizer` covers all credential patterns.
- Relay token TTL and max-uses are configured with safe defaults.

### 10. Final sign-off

- [ ] Build: zero warnings, zero errors.
- [ ] Tests: all pass, coverage met.
- [ ] CHANGELOG: complete.
- [ ] Version: consistent across manifests.
- [ ] No unresolved TODO/HACK markers.
- [ ] Configuration: backward compatible.
- [ ] Dashboard: functional.
- [ ] Migrations: tested.
- [ ] Security: no credential exposure.
