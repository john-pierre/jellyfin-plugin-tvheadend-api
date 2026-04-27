# Plugin Domain Knowledge

## TVHeadend

TVHeadend is an open-source Linux-based TV streaming server for DVB (terrestrial, cable, satellite) and IPTV sources. Key concepts:

| Concept | Description |
|---------|-------------|
| **Mux** | A transport multiplex — a single frequency carrying multiple services |
| **Service** | A single TV/radio channel within a mux |
| **Channel** | A user-facing entity mapped from one or more services |
| **Channel tag** | Grouping label for channels (e.g., "HD", "News", "Sports") |
| **EPG** | Electronic Programme Guide — schedule data for channels |
| **DVR** | Digital Video Recording — scheduled and completed recordings |
| **Timer** | A single recording scheduled for a specific time |
| **Series timer (autorec)** | Auto-recording rule that matches EPG entries by pattern |
| **Streaming profile** | Encoding/muxing configuration (e.g., "pass" for raw stream, "webtv-h264-aac" for transcoded) |
| **Tuner/input** | Physical or virtual tuner device |
| **Subscription** | An active stream consumer (client watching a channel) |

## This Plugin's Role

This plugin implements Jellyfin's `ILiveTvService` interface to provide:

1. **Channel listing** — fetches channels from TVHeadend's `/api/channel/grid`
2. **EPG/Guide data** — fetches programmes from `/api/epg/events/grid`
3. **DVR management** — creates/cancels timers and series timers via `/api/dvr/*`
4. **Stream URLs** — constructs playback URLs for Jellyfin clients
5. **Admin dashboard** — real-time status panel showing tuners, subscriptions, connections

All communication uses TVHeadend's HTTP/JSON API. The HTSP (Home Theater Streaming Protocol) is not used.

## Relay System

The relay proxies TVHeadend streams through the Jellyfin server instead of exposing TVHeadend URLs directly to clients:

**Why relay exists:**
- TVHeadend credentials stay server-side — clients never see them
- Relay tokens replace credentials in client-facing URLs
- Works when TVHeadend is on a different network than clients

**How it works:**
1. Client requests a stream -> plugin generates a scoped, time-limited relay token
2. Client receives a Jellyfin relay URL with the token (not a TVHeadend URL)
3. `RelayController` validates the token, then streams from TVHeadend to the client
4. TVHeadend authentication is handled server-side by `DigestAuthHandler`

**Relay components:**
- `RelayService` — core stream proxy (zero-copy forwarding)
- `RelayUrlBuilder` — constructs relay URLs with tokens
- `RelayTokenService` — generates and manages tokens
- `RelayTokenRepository` — SQLite persistence of token hashes
- `RelayTokenValidatorService` — validates tokens on incoming requests
- `RelayMetricsService` — tracks relay usage metrics
- `RelayActivityTracker` — monitors active relay sessions

## Streaming Profile Resolution

Profiles control how TVHeadend encodes/muxes the stream. This plugin implements hierarchical resolution:

**Resolution order (first match wins):**
1. **Channel override** — specific profile for a specific channel
2. **Channel group override** — profile for a channel tag/group
3. **Client-based rule** — matches client device/app
4. **User-based rule** — matches Jellyfin user
5. **Default profile** — global fallback

Configured via `StreamingProfileSettings` and `StreamingProfileRule` in `PluginConfiguration`.

`ProfileDiscoveryService` discovers available profiles from TVHeadend with TTL caching and validates that configured profile names actually exist.

## Health Monitoring

`HealthService` aggregates plugin health from multiple signals:

- **TVHeadend connectivity** — lightweight `/api/serverinfo` probe
- **Circuit breaker** — `ResilienceHandler` trips after 5 consecutive failures, opens for 30 seconds
- **Health transitions** — persisted to SQLite for historical tracking

## Dashboard

`DashboardPage.html` provides a real-time admin panel in Jellyfin showing:

- TVHeadend connection status
- Active tuner inputs (`InputMonitorService`)
- Active subscriptions (`SubscriptionService`)
- Server connections (`StatusService`)
- Plugin diagnostics (`DiagnosticService`)
- Comet log stream (`CometService`)

Data is aggregated by `DashboardService` and served via `DashboardController`.

## Key TVHeadend API Endpoints Used

| Endpoint | Plugin Usage |
|----------|-------------|
| `/api/serverinfo` | Health check, version detection |
| `/api/channel/grid` | Channel listing |
| `/api/channeltag/list` | Channel tag/group listing |
| `/api/epg/events/grid` | EPG programme data |
| `/api/epg/content_type/list` | EPG content type mapping |
| `/api/dvr/entry/grid` | Timer listing |
| `/api/dvr/entry/create` | Timer creation |
| `/api/dvr/entry/cancel` | Timer cancellation |
| `/api/dvr/autorec/grid` | Series timer listing |
| `/api/dvr/autorec/create` | Series timer creation |
| `/api/dvr/config/grid` | Recording profile lookup |
| `/api/profile/list` | Streaming profile discovery |
| `/api/status/inputs` | Tuner input status |
| `/api/status/subscriptions` | Active subscription status |
| `/api/status/connections` | Active connection status |
| `/api/comet/poll` | Long-poll for log and disk-space updates |
| `/api/idnode/write` | TVHeadend configuration writes (profile creation, user management) |
