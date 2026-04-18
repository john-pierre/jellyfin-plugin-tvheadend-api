# DVR Service

Manages digital video recording operations — scheduling, canceling, and listing timers and series timers — by interfacing with TVHeadend's DVR entry and autorec APIs.

## Detailed Description

The DVR Service handles the full lifecycle of recordings: creating single timers from EPG events, managing series (automatic) recording rules, canceling scheduled or active recordings, and looking up the configured recording profile UUID. It maps between Jellyfin's `TimerInfo`/`SeriesTimerInfo` and TVHeadend's DVR entry/autorec models, handling status translation and priority mapping.

## Domain Context

- **Use Case:** Recording management in Jellyfin's Live TV DVR interface
- **Module Type:** Service
- **Key Domain Entities:** Timer, Series Timer, Recording Profile

## Internal Dependencies

- **`Helper`** — Uses GridFetcher for DVR entry/autorec grid fetching, ApiClient for create/update/delete operations
- **`Guide`** — EPG event references for timer creation

