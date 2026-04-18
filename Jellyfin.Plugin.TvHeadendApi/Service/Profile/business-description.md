# Profile Service

Resolves, provisions, and maps TVHeadend streaming profiles to determine the correct codec/container configuration for live TV streams.

## Detailed Description

The Profile Service manages the relationship between TVHeadend streaming profiles and Jellyfin's media playback expectations. `ProfileResolver` fetches available profiles and retrieves detailed metadata (codec, container type). `ProfileContainerResolver` maps profile types to container strings (e.g., `matroska` → `mkv`). `DefaultProfileService` creates the `jellyfin` transcoding profile in TVHeadend if it doesn't exist. `ProfileMappingHelper` provides static codec/container mapping utilities.

## Domain Context

- **Use Case:** Streaming profile configuration and codec/container resolution
- **Module Type:** Service
- **Key Domain Entities:** ProfileResolver, ProfileContainerResolver, DefaultProfileService, ProfileSnapshot

## Internal Dependencies

- **`Helper`** — ApiClient for profile grid/idnode API calls, IdNodeValueHelper for profile parameter extraction

