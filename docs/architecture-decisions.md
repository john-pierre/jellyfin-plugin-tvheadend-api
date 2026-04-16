# Architecture Decisions

This document records significant architecture decisions for the plugin using lightweight ADR-style entries.

---

## ADR-001: HTTP/JSON API Only — No HTSP

**Status:** Accepted (since v1.0.0)

**Context:** TVHeadend provides two integration paths: HTSP (binary protocol) and HTTP/JSON API. The official Jellyfin plugin uses HTSP.

**Decision:** This plugin uses HTTP/JSON API exclusively.

**Rationale:**
- Simpler implementation and debugging (standard HTTP tooling).
- No binary protocol dependency.
- Enables auth token–based direct play from clients to TVHeadend.
- Stream URL can be forwarded directly to clients for Direct Play.

**Consequences:**
- Some HTSP-only features are not available.
- Depends on TVHeadend HTTP API stability.

---

## ADR-002: Thin Orchestrator, Domain Services

**Status:** Accepted

**Context:** `ILiveTvService` has a large surface (~20 methods). Putting all logic in one class creates a god object.

**Decision:** `OrchestratorService` is a pure delegator. Each domain concern (Guide, DVR, Stream, etc.) is a separate service.

**Rationale:**
- Each service is independently testable.
- Clear responsibility boundaries.
- Services can evolve independently.

**Consequences:**
- Slightly more DI registrations.
- OrchestratorService must stay thin (no business logic).

---

## ADR-003: MediaInfo Cache Pre-Creation

**Status:** Accepted

**Context:** Jellyfin probes live streams with FFmpeg, adding 3+ seconds to first tune. The probe result is cached as `cache/mediainfo/<hash>.json`.

**Decision:** The plugin pre-creates cache files based on the selected TVHeadend streaming profile, so Jellyfin finds probe data instantly.

**Rationale:**
- Reduces first-tune latency from 3+ seconds to near-zero.
- Cache file format matches Jellyfin core's expected schema.
- Profile metadata from TVHeadend is used to populate codec/container fields.

**Consequences:**
- Plugin must mirror Jellyfin's cache key hashing algorithm exactly.
- Cache files become stale if the TVHeadend profile changes (mitigated by validation option).
- Tight coupling to Jellyfin's internal cache format (not a public API).

---

## ADR-004: Auth Token for Direct Play URLs

**Status:** Accepted

**Context:** Direct Play means the client opens a direct HTTP connection to TVHeadend. The client needs authentication.

**Decision:** The plugin appends `?auth=<token>` to stream and image URLs.

**Rationale:**
- Avoids embedding username:password in URLs (visible in client logs).
- TVHeadend supports persistent API tokens.
- Token can be rotated independently.

**Consequences:**
- Token must be alphanumeric for FFmpeg URL safety.
- Token generation requires TVHeadend admin access.

---

## ADR-005: Singleton Service Lifetimes

**Status:** Accepted

**Context:** Jellyfin plugins register services in the host DI container.

**Decision:** All plugin services are registered as singletons.

**Rationale:**
- Matches Jellyfin's plugin lifecycle expectations.
- `StatisticsService` maintains in-memory state and must be singleton.
- No per-request state in any service.

**Consequences:**
- Services must be thread-safe.
- `HttpClient` instances are created per-call (not ideal; see future improvement for `IHttpClientFactory`).

---

## ADR-006: Plugin Configuration via Jellyfin BasePluginConfiguration

**Status:** Accepted

**Context:** Jellyfin provides `BasePlugin<TConfiguration>` with XML-serialized configuration.

**Decision:** Use `PluginConfiguration` extending `BasePluginConfiguration` with sensible defaults.

**Rationale:**
- Standard Jellyfin plugin pattern.
- Configuration accessible via `Plugin.Instance.Configuration`.
- Admin UI via embedded HTML page.

**Consequences:**
- Configuration changes require plugin page save + potential restart for some settings.
- No configuration validation beyond what the admin UI enforces.

---

## ADR-007: No HTSP, No Recording File Access

**Status:** Accepted

**Context:** Recording file playback typically uses HTSP or direct file system access.

**Decision:** This plugin focuses on live TV. Recording management (timers) is supported, but recording file playback is delegated to TVHeadend's HTTP streaming or external access.

**Rationale:**
- Keeps plugin scope focused.
- Recording file access depends on deployment topology.

**Consequences:**
- Users may need additional setup for recording playback.

