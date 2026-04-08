# Jellyfin Plugin: TVHeadend API Integration

[![Release Version](https://img.shields.io/github/v/release/john-pierre/jellyfin-plugin-tvheadend-api)](https://github.com/john-pierre/jellyfin-plugin-tvheadend-api/releases)
[![Minimum Jellyfin Version](https://img.shields.io/badge/Jellyfin-10.10.3%2B-blue)](https://jellyfin.org)
[![Minimum TVHeadend Version](https://img.shields.io/badge/TVHeadend-4.3%2B-green)](https://tvheadend.org)
[![License](https://img.shields.io/github/license/john-pierre/jellyfin-plugin-tvheadend-api)](LICENSE)
[![Issues](https://img.shields.io/github/issues/john-pierre/jellyfin-plugin-tvheadend-api)](https://github.com/john-pierre/jellyfin-plugin-tvheadend-api/issues)
[![Target Framework](https://img.shields.io/badge/.NET-8.0-purple)](https://dotnet.microsoft.com/en-us/)

A Jellyfin plugin that integrates [TVHeadend](https://tvheadend.org) exclusively through its HTTP/JSON API. It provides live TV, EPG (Electronic Program Guide), and DVR functionality without relying on the HTSP protocol.

> **Note:** This plugin does **not** use the HTSP protocol. All authentication and streaming is handled via TVHeadend's web API endpoints.

## Table of Contents

- [Features](#features)
- [Requirements](#requirements)
- [Installation](#installation)
- [Configuration](#configuration)
   - [Connection](#connection)
   - [Authentication](#authentication)
   - [Streaming](#streaming)
   - [Recording](#recording)
   - [Image Proxy](#image-proxy)
- [Developer Guide](#developer-guide)
  - [Prerequisites](#prerequisites)
  - [AI Agent Instructions](#ai-agent-instructions)
  - [Building from Source](#building-from-source)
  - [Docker Development Environment](#docker-development-environment)
  - [Project Structure](#project-structure)
- [Release Process](#release-process)
- [Contributing](#contributing)
- [Known Limitations](#known-limitations)
- [Security](#security)
- [License](#license)

## Features

- **Live TV** — Stream live TV channels from TVHeadend in Jellyfin.
- **EPG** — Import the electronic program guide with genre mapping (ETSI EN 300 468).
- **Timers** — Create, update, and cancel single recording timers.
- **Series Timers** — Manage automatic recording rules (autorec) from Jellyfin.
- **Channel Metadata** — Channel icons and tags from TVHeadend.
- **Dynamic Profile Selection** — Streaming and Recording profiles are auto-populated from available TVHeadend profiles.

## Requirements

- **Jellyfin 10.10.3** or later
- **TVHeadend 4.3** or later
- A TVHeadend user account with access to the API, EPG, streaming, and DVR features

## Installation

1. In Jellyfin, navigate to **Dashboard → Plugins → Repositories**.
2. Add the following repository URL:

   ```text
   https://raw.githubusercontent.com/john-pierre/jellyfin-plugin-tvheadend-api/main/manifest.json
   ```

3. Go to the **Catalog** tab and install the **TVHeadend API** plugin.
4. Restart Jellyfin when prompted.
5. Open the plugin configuration at **Dashboard → Plugins → My Plugins → TVHeadend API**.

## Configuration

### Connection

| Setting                    | Description                                                                 | Default     |
|----------------------------|-----------------------------------------------------------------------------|-------------|
| **Host**                   | Hostname or IP address of your TVHeadend server.                            | `127.0.0.1` |
| **Port**                   | TVHeadend HTTP API port.                                                    | `9981`      |
| **Use SSL**                | Enable if TVHeadend is accessible via HTTPS.                                | `false`     |
| **Ignore Certificate Errors** | Accept self-signed certificates. **Not recommended for production.**     | `false`     |
| **Webroot**                | Only required if TVHeadend is published under a sub-path (e.g. `/tvh/`).   | `/`         |

### Authentication

| Setting                  | Description                                                                 | Default |
|--------------------------|-----------------------------------------------------------------------------|---------|
| **Allow Anonymous Access** | Enable only if TVHeadend permits unauthenticated connections.             | `false` |
| **Username**             | TVHeadend username for Basic Auth.                                          | *(empty)* |
| **Password**             | TVHeadend password for Basic Auth.                                          | *(empty)* |
| **Auth Token**           | Optional token appended as `?auth=` to image/query-parameter URLs.          | *(empty)* |

**How authentication works:**
- Regular API calls use **HTTP Basic Auth headers**.
- Stream URLs embed credentials **in the URL** (`http://user:pass@host/…`).
- Image and icon URLs use the **auth token** as a query parameter.

### Streaming

| Setting                        | Description                                      | Default      |
|--------------------------------|--------------------------------------------------|--------------|
| **Streaming Profile**          | TVHeadend stream profile name.                   | `pass`       |
| **BufferMs**                   | Stream buffer size in milliseconds (`0` = Jellyfin default). | `0`          |
| **FallbackMaxStreamingBitrate**| Fallback maximum bitrate in bits per second.     | `3000000`    |
| **AnalyzeDurationMs**          | Stream analysis duration before playback starts. | `200`        |
| **Supports Direct Play**       | Allow direct playback without transcoding.       | `true`       |
| **Supports Direct Stream**     | Allow remuxing without re-encoding.              | `true`       |
| **Supports Transcoding**       | Allow full transcoding.                          | `false`      |
| **Supports Probing**           | Probe stream properties before playback.         | `true`       |
| **Is Infinite Stream**         | Treat the stream as infinite (live TV).          | `true`       |
| **Ignore DTS**                 | Ignore Decode Time Stamps for compatibility.     | `false`      |
| **Pre-create Probe Cache Files** | Pre-create Jellyfin mediainfo cache files for compatible profiles. | `true`    |

#### Auto-Created Transcoding Profiles

The plugin can automatically create optimized transcoding profiles in TVHeadend for fast channel switching. Click **"Create 'jellyfin' Profile"** in the plugin configuration page to automatically create the following profiles:

| Profile Name | Type | Codec | Hardware Acceleration | Use Case |
|--------------|------|-------|----------------------|----------|
| **jellyfin-h264** | Video (libx264) | H.264 | None | CPU-based transcoding for any system |
| **jellyfin-h264-intel** | Video (Intel QSV) | H.264 | Intel Quick Sync Video | **Recommended for Intel CPUs** (7th gen or newer with iGPU) |
| **jellyfin-aac** | Audio | AAC | None | Audio codec profile (48 kHz, 3-channel layout) |
| **jellyfin** | Streaming Profile | H.264 + AAC | Depends on video profile | Main streaming profile that uses the codec profiles above |

**Which video profile should I use?**

- **If you have an Intel CPU with integrated graphics (iGPU):** Use the `jellyfin-h264-intel` profile. It leverages Intel Quick Sync Video for hardware-accelerated encoding, reducing CPU load significantly.
  - Supported: 7th gen Intel Core or newer with iGPU
  - Requires device path: `/dev/dri/renderD128` (adjust if different on your system)
  
- **If you don't have Intel Quick Sync or prefer CPU encoding:** Use the `jellyfin-h264` profile with the standard libx264 encoder.

After creating the profiles, set **Streaming Profile** to `jellyfin` in the plugin configuration.

#### How Probing and Caching Work (Jellyfin Core Behaviour)

Understanding how Jellyfin handles live TV stream probing is critical for optimal channel-switch speed:

| Step | What Jellyfin does | Time cost |
|------|--------------------|-----------|
| 1 | Check disk cache (`<data>/cache/mediainfo/<md5>.json`) | ~0 ms |
| 2 | If cache miss: wait `Math.Max(3000, AnalyzeDurationMs)` ms | **≥ 3 000 ms** |
| 3 | Override `AnalyzeDurationMs` to `3000` for all live streams | — |
| 4 | Run FFmpeg probe (`-analyzeduration 3000000`) | ~1–5 s |
| 5 | Save result to disk cache for future opens | ~0 ms |

**Key takeaways:**

- **First open of a channel with probing enabled always takes ≥ 3 seconds** because of the hard-coded `Math.Max(3000, ...)` delay in Jellyfin's `AddMediaInfoWithProbe`.
- **Subsequent opens** of the same channel hit the **disk cache** and complete in milliseconds (the probe result is stored as `<data>/cache/mediainfo/<md5>.json`).
- Jellyfin **always overrides** the plugin's `AnalyzeDurationMs` to `3000` ms for live streams when probing is active — the plugin value has no effect in that case.
- **Recommended approach:** Keep **Stream Probing ON** when using the `jellyfin` MP4/H.264/AAC profile and `Pre-create Probe Cache Files`. Jellyfin can read cache metadata immediately and usually starts faster.
- The plugin only pre-creates probe cache files for **MP4** streaming profiles. For pass-through profiles like `pass` (`mpegts`), cache pre-creation is skipped to avoid incorrect metadata.

### Recording

| Setting              | Description                                          | Default   |
|----------------------|------------------------------------------------------|-----------|
| **Priority**         | Recording priority (lower = higher priority).        | `5`       |
| **PrePaddingSeconds**| Seconds to start recording before scheduled time.    | `5`       |
| **PostPaddingSeconds**| Seconds to continue recording after scheduled end.  | `5`       |
| **Recording Profile**| TVHeadend DVR configuration profile name.            | `default` |

### Image Proxy

The plugin provides a **secure image proxy endpoint** that fetches channel icons and EPG artwork from TVHeadend without exposing credentials directly to clients. All image access is authenticated through Jellyfin's authorization layer.

#### How It Works

1. **Channel Icons & EPG Artwork** — When retrieving channels or programs, the plugin generates image URLs pointing to Jellyfin's internal proxy endpoint:
   ```
   /api/TvHeadendApi/ImageProxy?imagePath={escaped_image_path}
   ```

2. **Internal Proxy** — The proxy endpoint:
   - Validates the request (requires Jellyfin authentication)
   - Constructs the full TVHeadend image URL with embedded credentials
   - Fetches the image from TVHeadend using the plugin's HTTP client
   - Streams the raw image data back to the client

3. **Credential Masking** — Sensitive data (URLs with embedded credentials) is automatically masked in plugin logs.

#### Benefits

- ✅ **No credential exposure** — Users never receive TVHeadend URLs or auth tokens
- ✅ **Jellyfin authorization** — Image access is protected by Jellyfin's login system
- ✅ **Transparent operation** — Automatic for all channel icons and EPG artwork
- ✅ **Consistent caching** — Jellyfin handles image caching seamlessly

#### Testing the Proxy

To verify that images are loading correctly:

1. Open the Jellyfin web UI and navigate to **Live TV**.
2. Check that **channel icons** appear next to channel names.
3. Open an **EPG program** and verify that artwork displays correctly.
4. Check Jellyfin's plugin logs in **Settings → Logs** — you should see entries like:
   ```
   [ImageProxy] Fetching image from TVHeadend: {masked_url}
   ```
5. Images served via proxy will show URLs like `/api/TvHeadendApi/ImageProxy?imagePath=...` in network inspector.


## Developer Guide

### Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later
- (Optional) [Docker](https://www.docker.com/) for the containerized development environment

### AI Agent Instructions

This repository includes explicit guidance for coding agents and Copilot-based workflows.

- `AGENTS.md` - canonical quick-start for any AI coding agent in this repo.
- `.github/copilot-instructions.md` - GitHub Copilot-specific coding and testing guidance.
- `docs/ai/README.md` - AI docs index.
- `docs/ai/SKILLS.md` - plugin domain skills and task playbooks.
- `docs/ai/INSTRUCTIONS.md` - step-by-step implementation and validation flow.

If AI guidance and other docs ever conflict, follow this order:

1. Security and policy constraints from the active environment.
2. `AGENTS.md`.
3. `.github/copilot-instructions.md`.
4. `docs/ai/*.md`.

### Naming and Structure Conventions

To keep the repository predictable for humans and AI agents, use these conventions for all new files/folders and when touching existing ones:

1. **C# folders and files:** `PascalCase` by domain (`Service`, `Configuration`, `Model`, `Api`).
2. **TVHeadend service internals:** keep TVHeadend-specific helpers in `Jellyfin.Plugin.TvHeadendApi/Service/Tvheadend` (no generic `Utility` bucket).
3. **Class/file alignment:** file name matches the primary type name exactly.
4. **Prefix usage:**
   - Use `Tvheadend*` for services/helpers/adapters.
   - Keep `TvhApi*` only for raw API DTO models under `Model/`.
5. **Scripts/docs naming:**
   - PowerShell scripts: `kebab-case.ps1`.
   - Markdown docs outside C# code: `kebab-case.md` preferred.
6. **No silent moves:** if a rename/move is user-visible for contributors, document it in README/CHANGELOG.

Recent normalization applied in this repository:

- `Utility/` was removed and TVHeadend helpers were moved into `Service/Tvheadend/`.
- `TvhUrlBuilder` -> `TvheadendUrlHelper`
- `TvhHttpClientFactory` -> `TvheadendHttpClientFactory`
- `TvhJsonHelper` -> `TvheadendJsonHelper`
- `TvhProfileMappingHelper` -> `TvheadendProfileMappingHelper`

### Building from Source

```bash
# Clone the repository
git clone https://github.com/john-pierre/jellyfin-plugin-tvheadend-api.git
cd jellyfin-plugin-tvheadend-api

# Restore NuGet packages
dotnet restore Jellyfin.Plugin.TvHeadendApi.sln

# Build in Release mode
dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release --no-restore
```

The compiled plugin DLL can be found at:

```
Jellyfin.Plugin.TvHeadendApi/bin/Release/net8.0/Jellyfin.Plugin.TvHeadendApi.dll
```

To install it manually, copy the DLL (along with a `meta.json`) into a versioned folder under your Jellyfin plugins directory — for example:

```
<jellyfin-data>/plugins/tvheadend_api_1.0.0.0/
  ├── Jellyfin.Plugin.TvHeadendApi.dll
  └── meta.json
```

### Docker Development Environment

A `Dockerfile` and `docker-compose.yaml` are provided to spin up a Jellyfin instance with the plugin pre-installed. This is intended for **development and testing only** - it does not include a TVHeadend backend.

The Compose setup persists the full Jellyfin data directory:

- `./.docker/config:/config` (users, DB, plugins, plugin config)
- `./.docker/cache:/cache`
- `./.docker/media:/media`

This means plugin settings (Host, credentials, Fast Switching settings, etc.) survive container rebuilds and restarts.

For troubleshooting, the development `docker-compose.yaml` enables more verbose Jellyfin logging by default for:

- `Jellyfin.Plugin.TvHeadendApi`
- `MediaBrowser.MediaEncoding.Transcoding`
- `MediaBrowser.MediaEncoding.Encoder`

Global logging remains at `Information` to keep noise manageable.

```bash
docker compose up -d --build
```

#### Intel Quick Sync (VAAPI) in Docker

Für Hardware-Transcoding mit Intel Quick Sync benötigt der Container Zugriff auf `/dev/dri` und die Host-Gruppen für `video` und `render`.

1. Ermittle auf dem Linux-Host die GIDs:

```bash
getent group video
getent group render
```

2. Lege im Projekt eine `.env` an (oder exportiere die Variablen in deiner Shell):

```bash
VIDEO_GID=44
RENDER_GID=109
LIBVA_DRIVER_NAME=iHD
```

Hinweise:

- `iHD` ist der Standard für neuere Intel iGPUs; bei älteren Generationen ggf. `LIBVA_DRIVER_NAME=i965` setzen.
- Die Compose-Datei mapped `/dev/dri:/dev/dri` und nutzt `group_add` für `video`/`render`.
- Danach Container neu erstellen:

```bash
docker compose down
docker compose up -d --build
```

If you want quieter logs again, remove or adjust the `JELLYFIN_Logging__LogLevel__...` environment variables in `docker-compose.yaml` and recreate the container.

#### Quick dev build helper (auto version bump)

Use the script below to auto-increment the 4th version number for development builds
(e.g. `1.0.0.1` -> `1.0.0.2`), rebuild the image and recreate Jellyfin:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\dev-build.ps1
```

Optional dry-run (shows commands only):

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\dev-build.ps1 -DryRun
```

The current dev version state is stored in `./.docker/dev-version.txt`.


#### Migration for older local Docker data

If you previously used `./.docker/config:/config/config`, move existing data once so Jellyfin finds it under `/config`:

```powershell
New-Item -ItemType Directory -Force .\.docker\config | Out-Null
if (Test-Path .\.docker\config\config) {
    Copy-Item .\.docker\config\config\* .\.docker\config\ -Recurse -Force
}
```

Then recreate the container:

```bash
docker compose down
docker compose up -d --build
```

### Project Structure

```
jellyfin-plugin-tvheadend-api/
├── .github/
│   ├── workflows/build-release.yaml   # CI/CD pipeline
│   ├── release-please-config.json     # Release Please configuration
│   └── pr-title-checker-config.json   # Conventional commit PR title check
├── Jellyfin.Plugin.TvHeadendApi/
│   ├── Configuration/
│   │   ├── ConfigPage.html            # Plugin settings page (embedded resource)
│   │   └── PluginConfiguration.cs     # Configuration model
│   ├── Model/                         # TVHeadend API response models
│   ├── Service/
│   │   └── LiveTvService.cs           # Core ILiveTvService implementation
│   ├── Plugin.cs                      # Plugin entry point
│   └── ServiceRegistrator.cs          # DI registration
├── manifest.json                      # Jellyfin plugin repository manifest
├── .release-please-manifest.json      # Current version tracker
├── docker-compose.yaml                # Development Compose file
├── Dockerfile                         # Development image
└── CONTRIBUTING.md                    # Contribution guidelines
```

## Release Process

This repository uses [Release Please](https://github.com/googleapis/release-please) and two GitHub Actions workflows:

- `.github/workflows/pr-title-check.yaml` (PR title policy)
- `.github/workflows/build-release.yaml` (build, package, release, manifest update)

**Workflow overview:**

1. **Pull Requests** — PR title is validated against [Conventional Commits](https://www.conventionalcommits.org/) by the dedicated PR title workflow.
2. **Merge Strategy** — Use **Squash and merge** so the merged commit subject equals the PR title.
3. **Push to `main`** — Release Please analyzes merged commit subjects and opens/updates a release PR when there are releasable changes.
3. **Release created** — When the release PR is merged, the pipeline:
   - Builds the plugin
   - Packages the DLL and `meta.json` into a ZIP archive
   - Calculates MD5 and SHA-256 checksums
   - Uploads all artifacts to the GitHub release
   - Updates `manifest.json` with the new version entry and commits it to `main`

This setup keeps changelog entries aligned with PR titles, as long as squash merge is used consistently.

### Key Files

| File | Purpose |
|------|---------|
| `manifest.json` | Jellyfin plugin repository metadata (consumed by Jellyfin clients) |
| `.release-please-manifest.json` | Current version state for Release Please |
| `.github/release-please-config.json` | Release Please configuration |
| `.github/pr-title-checker-config.json` | Conventional Commit regex for PR title validation |
| `.github/workflows/pr-title-check.yaml` | Dedicated PR title validation workflow |
| `.github/workflows/build-release.yaml` | CI/CD pipeline definition |

## Contributing

Contributions are welcome! Please see [CONTRIBUTING.md](CONTRIBUTING.md) for detailed guidelines.

**Quick summary:**

1. Fork the repository and create a feature branch.
2. Follow [Conventional Commits](https://www.conventionalcommits.org/) for all commit messages.
3. Ensure the project builds without warnings: `dotnet build -c Release`.
4. Submit a pull request against `main`.

## Known Limitations

- There is currently **no test project** in the repository. Unit tests for URL construction, response mapping, and configuration handling are a planned addition.
- The plugin is purely API-based; HTSP-specific features (e.g. subscription weight, low-latency streaming) are not available.
- Stream metadata (codec, resolution, bitrate) depends on the TVHeadend streaming profile and cannot be reliably detected at the plugin level.
- The Docker development environment is a convenience tool for plugin loading — it does not include a TVHeadend instance or mock backend.
- Image access (channel icons, EPG artwork) is exclusively served through the internal Jellyfin proxy endpoint and cannot be cached externally or accessed directly.

## Security

- Default configuration ships with **no pre-filled credentials** or tokens.
- Sensitive information (passwords, auth tokens) is automatically masked in log output.
- The **Ignore Certificate Errors** option should not be enabled in production environments.

## License

This project is licensed under the **GNU General Public License v3.0 (GPL-3.0)**. See the [LICENSE](LICENSE) file for details.
