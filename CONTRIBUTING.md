# Contributing to jellyfin-plugin-tvheadend-api

Thank you for your interest in contributing! This document explains how to set up the project, the coding standards we follow, and how to submit changes.

## Getting Started

### 1. Fork and Clone

```bash
git clone https://github.com/<your-username>/jellyfin-plugin-tvheadend-api.git
cd jellyfin-plugin-tvheadend-api
```

### 2. Install Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later
- (Optional) [Docker](https://www.docker.com/) for the development environment

### 3. Build

```bash
dotnet restore Jellyfin.Plugin.TvHeadendApi.sln
dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release --no-restore
```

The build must succeed with **zero warnings and zero errors** before submitting a pull request.

### 4. Run Locally (Optional)

Use the Docker development environment to load the plugin into a local Jellyfin instance:

```bash
docker compose up -d --build
```

Jellyfin will be available at `http://localhost:8096`. Note that this does **not** include a TVHeadend backend.

## Coding Standards

### Language

- All code comments, XML documentation, log messages, README files, and commit messages **must be in English**.

### Style

- The project uses [StyleCop Analyzers](https://github.com/DotNetAnalyzers/StyleCopAnalyzers) and additional analyzers configured in `Jellyfin.ruleset`. The build enforces `TreatWarningsAsErrors`, so any analyzer violation will fail the build.
- Follow the existing code style — consistent naming, XML doc comments on all public members, and clear inline comments where the intent is not obvious.

### Nullable Reference Types

- Nullable reference types are enabled (`<Nullable>enable</Nullable>`). Avoid suppressing nullable warnings (`!`) unless absolutely necessary and with a comment explaining why.

## Commit Messages

This project follows [Conventional Commits](https://www.conventionalcommits.org/). Every commit message must match this pattern:

```
<type>(<optional scope>): <description>
```

**Allowed types:** `feat`, `fix`, `perf`, `revert`, `docs`, `style`, `refactor`, `test`, `build`, `ci`

**Examples:**

```
feat: add support for TVHeadend time recording rules
fix: correct EPG time range filter to include overlapping programs
docs: update README with streaming configuration table
refactor: extract HTTP client lifecycle into dedicated helper
ci: skip test step when no test projects exist
```

Pull request **titles** are also validated against this format by `.github/workflows/pr-title-check.yaml`.

To keep release changelog entries aligned with PR titles, maintainers should use **Squash and merge**.

## CI Action Versioning Policy

- This repository intentionally keeps GitHub Actions `uses:` references tag-based (for example `@v4` or `@v1.4.3`).
- Do not automatically rewrite action references to pinned commit SHAs unless explicitly requested by maintainers.
- If you update an action tag, keep the change minimal and note it in the PR description.

## Pull Request Process

1. **Create a feature branch** from `main`:
   ```bash
   git checkout -b feat/my-feature
   ```

2. **Make your changes.** Keep commits focused and atomic.

3. **Build and verify** locally:
   ```bash
   dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release
   dotnet test Jellyfin.Plugin.TvHeadendApi.Tests/Jellyfin.Plugin.TvHeadendApi.Tests.csproj -c Release --no-build
   ```

4. **Push and open a PR** against the `main` branch.

5. CI workflows will:
   - Check your PR title against Conventional Commits (`pr-title-check.yaml`)
   - Build the plugin
   - Package the artifact and upload it as a PR artifact for manual testing (`build-release.yaml`)

6. Address any review feedback.

7. Once approved and merged, [Release Please](https://github.com/googleapis/release-please) automatically determines if a new version should be released based on merged commit types (with squash merge: PR title).
8. On a created release, the pipeline updates `manifest.json` with the new release artifact URL and checksum and commits it back to `main`.

## Project Layout

| Path | Description |
|------|-------------|
| `Jellyfin.Plugin.TvHeadendApi/Service/OrchestratorService.cs` | Core service implementing `ILiveTvService` |
| `Jellyfin.Plugin.TvHeadendApi/Plugin.cs` | Plugin entry point and configuration page registration |
| `Jellyfin.Plugin.TvHeadendApi/ServiceRegistrator.cs` | Dependency injection setup |
| `Jellyfin.Plugin.TvHeadendApi/Configuration/` | Configuration model and embedded HTML settings page |
| `Jellyfin.Plugin.TvHeadendApi/Model/` | TVHeadend API response DTOs |
| `Jellyfin.Plugin.TvHeadendApi.Tests/` | Unit and integration tests |
| `docs/` | Architecture, naming, test strategy, roadmap documentation |
| `docker/` | Docker files (Dockerfile, compose files, build script) |
| `.github/workflows/build-release.yaml` | CI/CD pipeline |
| `manifest.json` | Jellyfin plugin repository manifest |

## Reporting Issues

- Use [GitHub Issues](https://github.com/john-pierre/jellyfin-plugin-tvheadend-api/issues) to report bugs or request features.
- Include your Jellyfin version, TVHeadend version, and relevant log output (with sensitive data redacted).

## License

By contributing to this repository, you agree that your contributions will be licensed under the [GPL-3.0 License](LICENSE).

