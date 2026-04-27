# Streaming Profile Skill

Streaming profile resolution for jellyfin-plugin-tvheadend-api.

## Overview

Streaming profiles determine which TVHeadend transcoding profile is used for a given stream request. The system uses hierarchical resolution with multiple override levels. Profile resolution code spans two service domains: `Service/StreamingProfile/` (resolution logic) and `Service/Profile/` (TVHeadend profile management).

## StreamingProfileResolver

`StreamingProfileResolver` (`Service/StreamingProfile/StreamingProfileResolver.cs`) — implements `IStreamingProfileResolver`:

Resolves the profile to use for a stream request by evaluating overrides in priority order:

1. **Channel override** — specific channel mapped to a profile (`ChannelProfileOverride` in `Configuration/`).
2. **Group override** — channel group mapped to a profile (`ChannelGroupProfileOverride` in `Configuration/`).
3. **Client rule** — regex or exact match on client name (`StreamingProfileRule` with `MatchType`).
4. **User rule** — regex or exact match on Jellyfin user name.
5. **Default profile** — fallback configured in `PluginConfiguration`.

Resolution returns a `StreamingProfileResolutionResult` containing the selected profile and the `ResolutionSource` (which level matched).

### StreamingProfileContext

Input to the resolver:
- Channel ID and channel name.
- Channel group name.
- Client name (from Jellyfin's device info).
- User name (from Jellyfin's session).

### StreamingProfileRule

`StreamingProfileRule` (`Configuration/StreamingProfileRule.cs`):

- `MatchType`: `Exact` or `Regex` (defined in `StreamingProfileRuleMatchType`).
- `Pattern`: the string or regex pattern to match.
- `ProfileName`: the TVHeadend profile to use when matched.

## ProfileDiscoveryService

`ProfileDiscoveryService` (`Service/StreamingProfile/ProfileDiscoveryService.cs`) — implements `IProfileDiscoveryService`:

- Discovers available TVHeadend streaming profiles via the API.
- Returns `DiscoveredProfile` objects with name and capabilities.
- Used by the configuration UI to populate profile dropdowns.

## DefaultProfileService

`DefaultProfileService` (`Service/Profile/DefaultProfileService.cs`) — implements `IDefaultProfileService`:

- Creates an optimized "jellyfin" profile in TVHeadend.
- Configures the profile for direct stream passthrough (no transcoding) with container settings optimized for Jellyfin playback.
- Uses the TVHeadend idnode API via `IApiClient`.

## ProfileResolver

`ProfileResolver` (`Service/Profile/ProfileResolver.cs`) — implements `IProfileResolver`:

- Resolves codec and container metadata for a given TVHeadend profile.
- Queries TVHeadend's idnode API to read profile configuration.
- Used by `MediaSourceService` to build accurate `MediaSourceInfo` for Jellyfin.

### ProfileContainerResolver

`ProfileContainerResolver` (`Service/Profile/ProfileContainerResolver.cs`) — implements `IProfileContainerResolver`:

- Maps TVHeadend container/mux types to Jellyfin-compatible container strings.
- `ProfileMappingHelper` provides the mapping tables.

## Configuration

Streaming profile settings in `PluginConfiguration`:
- `StreamingProfileSettings` (`Configuration/StreamingProfileSettings.cs`): holds all profile rules and overrides.
- `ChannelProfileOverride`: per-channel profile mapping.
- `ChannelGroupProfileOverride`: per-group profile mapping.
- `StreamingProfileRule` + `StreamingProfileRuleMatchType`: client/user matching rules.

## API

`StreamingProfileController` (`Api/Endpoint/StreamingProfileController.cs`):
- Endpoints for managing streaming profile rules.
- Profile discovery endpoint for the configuration UI.

## Rules

- Resolution priority order is strict and must not be reordered.
- Regex patterns in rules must be compiled and cached (not recompiled per request).
- Profile discovery results should be cached with a reasonable TTL.
- Default profile creation must be idempotent (safe to call multiple times).
- All TVHeadend API calls go through `IApiClient` with `CancellationToken`.
- Profile names must match what TVHeadend returns — no normalization.
