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
| `build` | push, PR | Build, test, coverage report (90/95 thresholds), TRX test reporter, sticky PR comment |
| `package` | push to main | Create plugin zip + SHA256 checksums |
| `release` | push to main | Release Please → asset upload → manifest.json auto-update |
| `integration` | push to main, PR | Live tests against `docker-compose.test.yml` (TVHeadend + Jellyfin) |

Additional workflow: `.github/workflows/pr-title-check.yaml` — validates PR titles against Conventional Commits.

---

## 2 — Release Process

1. PRs merged to `main` with squash merge (PR title = commit message).
2. Release Please evaluates commit types and creates/updates a release PR.
3. On release PR merge: GitHub Release created, assets uploaded.
4. Post-release job updates `manifest.json` with download URL + checksum, commits back to `main`.

---

## 3 — Conventions

- **Action versions:** Always tag-based (`@v4`, `@v1.4.3`). Never pin to commit SHAs unless explicitly requested.
- **Runner:** `ubuntu-24.04`
- **SDK:** .NET 8.0.x (setup via `actions/setup-dotnet`)
- **Coverage:** Coverlet → Cobertura XML → `irongut/CodeCoverageSummary` with thresholds `90 95`
- **Test reporting:** TRX format → `dorny/test-reporter`
- **Artifact:** Plugin DLL zipped with `meta.json` derived from `manifest.json`

---

## 4 — Docker Test Environment

```yaml
# docker-compose.test.yml
services:
  tvheadend:
    image: linuxserver/tvheadend
    command: -C  # no-auth first-run
    ports: ["19981:9981"]
    healthcheck: curl http://localhost:9981/api/serverinfo

  jellyfin:
    build: .
    ports: ["18096:8096"]
```

Live integration tests use `TVHEADEND_URL=http://localhost:19981` and trait `[Trait("Category", "LiveIntegration")]`.

---

## 5 — When Modifying CI

- Keep changes minimal and note them in PR description.
- Test workflow changes on a feature branch before merging.
- Never change coverage thresholds without team discussion.
- Never remove the PR title check workflow.

