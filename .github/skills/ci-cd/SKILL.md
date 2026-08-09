---
name: ci-cd
description: "CI/CD conventions and pipeline knowledge for jellyfin-plugin-tvheadend-api. Covers GitHub Actions workflows, release process, and deployment."
---

# CI/CD Skill — jellyfin-plugin-tvheadend-api

---

## 1 — Pipeline Overview

Single workflow: `.github/workflows/build-release.yaml` with 4 jobs:

| Job | Trigger | Purpose |
|-----|---------|---------|
| `build` | push, PR | Build, test, coverage report (80/90 thresholds), TRX test reporter, sticky PR comment |
| `package` | push, PR | Create plugin zip + MD5/SHA256 checksums; asserts the DLL and `meta.json` sit at the archive ROOT (a nested archive is not installable) |
| `release` | push to main | Release Please → asset upload → manifest.json update PR. `needs: [build, package, integration]` |
| `integration` | push, PR | Live tests + Playwright against `docker-compose.test.yaml`, as a **matrix** over Jellyfin `10.10.7`, `10.11.11` and `12.0-rc4` |

The `integration` matrix uses `fail-fast: false`; the `12.0-rc4` leg is `continue-on-error` so a
release-candidate regression is visible without blocking a release. Each leg asserts the reported
server version and greps the Jellyfin log for assembly-load failures before running functional
tests — that is how an ABI break is caught as an ABI break rather than as a confusing test failure.

Workflow-level `TARGET_ABI` is the single source for the `targetAbi` written into `meta.json` and
`manifest.json`. It must always equal the LOWEST Jellyfin version in the matrix.

Additional workflow: `.github/workflows/pr-title-check.yaml` — validates PR titles against Conventional Commits.

---

## 2 — Release Process

1. PRs merged to `main` with squash merge (PR title = commit message).
2. Release Please evaluates commit types and creates/updates a release PR.
3. On release PR merge: GitHub Release created, assets uploaded.
4. Post-release job updates `manifest.json` with download URL + checksum and opens a pull request. `main` is protected (enforce_admins, required review, linear history), so a direct push is rejected — merge the manifest PR to publish the release to Jellyfin clients.

---

## 3 — Conventions

- **Action versions:** Always tag-based (`@v4`, `@v1.4.3`). Never pin to commit SHAs unless explicitly requested.
- **Runner:** `ubuntu-24.04`
- **SDK:** .NET 8.0.x (setup via `actions/setup-dotnet`)
- **Coverage:** Coverlet → Cobertura XML → `irongut/CodeCoverageSummary` with thresholds `80 90`
- **Test reporting:** TRX format → `dorny/test-reporter`
- **Artifact:** Plugin DLL zipped with `meta.json` derived from `manifest.json`

---

## 4 — Docker Test Environment

The test stack is defined in `docker/docker-compose.test.yaml`:

- **iptv-simulator:** Built from `docker/iptv-simulator/Dockerfile`, port `18888:80`. Generates the
  live MPEG-TS streams, XMLTV EPG, M3U playlist and channel images the whole stack feeds on.
- **TVHeadend:** `linuxserver/tvheadend` started with `-C` (no initial auth), ports `19981:9981`
  (HTTP API) and `19982:9982` (HTSP), healthcheck on `/api/serverinfo`. After bootstrap, access
  requires `testuser` / `testpass` — the anonymous admin is deleted.
- **Jellyfin:** Built from `docker/jellyfin/Dockerfile` (context: repo root), port `18096:8096`.
  The base image is selectable via the `JELLYFIN_IMAGE` build arg so the stack can run against
  every supported server version.
- **tvheadend-bootstrap:** Built from `docker/tvheadend-bootstrap/Dockerfile`. Runs once after
  TVHeadend is healthy (IPTV network, mux scan, channel mapping, EPG grabber, test user, profiles,
  DVR config), then exits.

Live integration tests use `TVHEADEND_URL=http://localhost:19981`, `JELLYFIN_URL=http://localhost:18096`
and the trait `[Trait("Category", "LiveIntegration")]`.

The Playwright suite in `tests/playwright/` runs against the same stack in the `integration` job.

---

## 5 — When Modifying CI

- Keep changes minimal and note them in PR description.
- Test workflow changes on a feature branch before merging.
- Never change coverage thresholds without team discussion.
- Never remove the PR title check workflow.


---

## 6 — Release Readiness Checklist

Work through it before merging a release PR, then give an explicit **GO** or **NO-GO** with the
reasons.

### Build and tests

```bash
dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release
dotnet test Jellyfin.Plugin.TvHeadendApi.Tests/Jellyfin.Plugin.TvHeadendApi.Tests.csproj \
  -c Release --no-build --filter "Category!=LiveIntegration"
```

- Zero warnings, zero errors (`TreatWarningsAsErrors` is on).
- All unit tests pass.
- Coverage meets the CI gate (`thresholds: '80 90'`, `fail_below_min: true`).
- No skipped tests without a documented justification.
- The `integration` matrix is green for every **blocking** Jellyfin version.

### Version consistency

| File | Field |
|---|---|
| `.release-please-manifest.json` | version number — the build job derives the packaged version from this |
| `manifest.json` | `version`, `targetAbi` |
| `Directory.Build.props` | `Version`, `AssemblyVersion` (CI overrides via `-p:`) |
| `CHANGELOG.md` | latest version header |

`TARGET_ABI` in the workflow must equal the lowest Jellyfin version in the integration matrix.

### CHANGELOG completeness

- Version header with date.
- User-facing changes categorized (Added, Changed, Fixed, Removed).
- Breaking changes marked explicitly, with migration steps.
- No entries referencing unreleased or future work.

### Code quality markers

```bash
grep -rn "TODO\|HACK\|FIXME\|TEMP" Jellyfin.Plugin.TvHeadendApi/ --include="*.cs"
```

Each marker must be resolved before release or carry a tracking issue number.

### Backward compatibility

- `PluginConfiguration` defaults must not change in ways that break existing installations.
- New properties need defaults that preserve current behaviour.
- Removed properties must deserialize gracefully.

### Database migration safety

- `DatabaseMigrationService` handles the upgrade from the previous release.
- Migrations are version-gated (run only when the stored schema version is below target).
- `DatabaseRecoveryService` is exercised for corruption recovery.
- No migration drops data without explicit user consent.

### Sign-off

- [ ] Build: zero warnings, zero errors
- [ ] Tests: all pass, coverage met, integration matrix green
- [ ] CHANGELOG: complete
- [ ] Version: consistent across all four files above
- [ ] No unresolved TODO/HACK markers
- [ ] Configuration: backward compatible
- [ ] Database migration: safe from the previous release

## 7 — Rollback Awareness

A release is only reversible if the manifest is. `manifest.json` keeps every published version, so
rolling back means pointing users at the previous entry — never deleting the new one. The plugin zip
and its checksums stay attached to the GitHub release; do not delete release assets to "undo" a bad
release, because installed clients resolve `sourceUrl` from the manifest at update time.
