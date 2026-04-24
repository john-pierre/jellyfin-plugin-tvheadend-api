# Service/StreamingProfile — Business Description

## Purpose

The StreamingProfile module provides **intelligent, configurable control** over which TVHeadend streaming profile is used for Live TV playback. It replaces the single global profile string with a hierarchical, rule-based resolution engine.

## Business Problem

Different clients handle streams differently — some need passthrough, some need TVHeadend transcoding, some need Jellyfin transcoding. SD channels may behave differently than HD channels. Users want control over which profile is used, based on channel, channel group, client type, or user identity.

## Solution

A deterministic resolution hierarchy that evaluates in strict precedence order:

1. **Per-channel override** — highest priority, for problematic individual channels
2. **Per-channel-group override** — for categories like Sports, News, Kids
3. **Client/device rule** — for client-specific compatibility (Swiftfin, Web, Android TV)
4. **User rule** — for user-specific preferences
5. **Global default** — configured default mode and profile
6. **Legacy fallback** — backward-compatible use of `PluginConfiguration.StreamingProfile`
7. **Safe fallback** — hardcoded "pass" profile as last resort

## Key Features

- **Four playback modes:** Auto, PassThrough, TvHeadendTranscode, JellyfinTranscode
- **Explainability:** Every resolution produces debug reasons explaining why a profile was chosen
- **Profile discovery:** Fetches available profiles from TVHeadend with TTL-based caching
- **Configuration validation:** Warns about configured profile names that don't exist in TVHeadend
- **Backward compatible:** Existing installations work without any configuration changes
- **Admin API:** REST endpoints for testing resolution, viewing discovered profiles, and validating configuration

## Files

| File | Purpose |
|------|---------|
| `IStreamingProfileResolver.cs` | Public interface for profile resolution |
| `StreamingProfileResolver.cs` | Central hierarchical resolver implementation |
| `StreamingProfileContext.cs` | Input record for resolution (channel, client, device, user) |
| `StreamingProfileResolutionResult.cs` | Output with effective profile, mode, source, and debug reasons |
| `ResolutionSource.cs` | Enum identifying which hierarchy level produced the result |
| `IProfileDiscoveryService.cs` | Interface for TVHeadend profile discovery and validation |
| `ProfileDiscoveryService.cs` | TTL-cached profile discovery with configuration validation |
| `DiscoveredProfile.cs` | Public DTO for discovered profile (Key, Name) |

