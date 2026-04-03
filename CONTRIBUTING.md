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

Pull request **titles** are also validated against this format by the CI pipeline.

## Pull Request Process

1. **Create a feature branch** from `main`:
   ```bash
   git checkout -b feat/my-feature
   ```

2. **Make your changes.** Keep commits focused and atomic.

3. **Build and verify** locally:
   ```bash
   dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release
   ```

4. **Push and open a PR** against the `main` branch.

5. The CI pipeline will:
   - Check your PR title against Conventional Commits
   - Build the plugin
   - Package the artifact and upload it as a PR artifact for manual testing

6. Address any review feedback.

7. Once approved and merged, [Release Please](https://github.com/googleapis/release-please) will automatically determine if a new version should be released based on the commit types.

## Project Layout

| Path | Description |
|------|-------------|
| `Jellyfin.Plugin.TvHeadendApi/Service/LiveTvService.cs` | Core service implementing `ILiveTvService` |
| `Jellyfin.Plugin.TvHeadendApi/Plugin.cs` | Plugin entry point and configuration page registration |
| `Jellyfin.Plugin.TvHeadendApi/ServiceRegistrator.cs` | Dependency injection setup |
| `Jellyfin.Plugin.TvHeadendApi/Configuration/` | Configuration model and embedded HTML settings page |
| `Jellyfin.Plugin.TvHeadendApi/Model/` | TVHeadend API response DTOs |
| `.github/workflows/build-release.yaml` | CI/CD pipeline |
| `manifest.json` | Jellyfin plugin repository manifest |

## Reporting Issues

- Use [GitHub Issues](https://github.com/john-pierre/jellyfin-plugin-tvheadend-api/issues) to report bugs or request features.
- Include your Jellyfin version, TVHeadend version, and relevant log output (with sensitive data redacted).

## License

By contributing to this repository, you agree that your contributions will be licensed under the [GPL-3.0 License](LICENSE).

