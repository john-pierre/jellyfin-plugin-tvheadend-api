# Stream Service

Constructs stream URLs and manages stream lifecycle for live TV playback, including MediaInfo cache management to enable Direct Play without FFmpeg probing.

## Detailed Description

The Stream Service has two responsibilities: (1) `MediaSourceService` builds `MediaSourceInfo` objects with the correct stream URL, container format, and codec hints based on the resolved streaming profile, and manages the mediainfo cache (pre-created JSON files that match Jellyfin's internal hash to bypass FFmpeg probing); (2) `LifecycleService` handles stream close notifications and tuner reset operations.

## Domain Context

- **Use Case:** Live TV stream playback initiation and teardown
- **Module Type:** Service
- **Key Domain Entities:** MediaSourceInfo, MediaInfo Cache, Streaming Profile, Direct Play

## Internal Dependencies

- **`Profile`** — Resolved profile determines container/codec hints for MediaSourceInfo
- **`Auth`** — Auth token appended to stream URLs
- **`Backend`** — UrlBuilder constructs base stream URLs

