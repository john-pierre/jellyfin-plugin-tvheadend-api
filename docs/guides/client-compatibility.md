# Client Compatibility Testing

This document provides guidance for testing live TV playback across different Jellyfin clients and devices.

## Why Client Testing Matters

The plugin configures stream URLs and metadata, but the actual playback path depends on:

- **Client codec support** — what the app/device can decode natively.
- **Jellyfin user playback policy** — transcoding limits, bitrate caps.
- **Network topology** — whether the client can reach TVHeadend directly.
- **Container/codec combination** — what TVHeadend delivers via the selected streaming profile.

A configuration that Direct Plays on one client may remux or transcode on another.

## Test Matrix Template

Use this table to document results for your deployment:

| Client | OS / Device | Profile | Play Method | Switch Time | Notes |
|---|---|---|---|---|---|
| Jellyfin Web (Chrome) | Windows/macOS | `jellyfin` | Direct Play | ~2s | Baseline reference |
| Jellyfin Web (Firefox) | Windows/macOS | `jellyfin` | | | |
| Jellyfin Web (Safari) | macOS/iOS | `jellyfin` | | | Check H.264/AAC support |
| Jellyfin Android TV | Shield/Fire TV | `jellyfin` | | | |
| Jellyfin Android Mobile | Phone/Tablet | `jellyfin` | | | |
| Jellyfin iOS | iPhone/iPad | `jellyfin` | | | |
| Jellyfin Desktop (MPV) | Windows/Linux | `jellyfin` | | | |
| Infuse | Apple TV/iOS | `jellyfin` | | | Third-party client |
| Swiftfin | Apple TV/iOS | `jellyfin` | | | |
| Jellyfin Roku | Roku device | `jellyfin` | | | |

## How to Test

### 1. Prepare

- Set Streaming Profile to `jellyfin` (or whichever profile you want to test).
- Enable Direct Play, Direct Stream, and Probing in plugin settings (probing also enables mediainfo cache pre-creation).
- Run Diagnose — confirm compatibility score ≥80.
- Note the TVHeadend and Jellyfin versions.

### 2. Test Procedure per Client

1. Open Live TV → Channels on the client.
2. Select a test channel with known good reception.
3. **First tune:** Measure time from tap/click to first visible frame.
4. Note the playback mode from Jellyfin's playback info overlay:
   - Direct Play, Direct Stream, or Transcode.
5. **Warm tune:** Close the stream, wait 3 seconds, re-open the same channel.
6. Repeat for 3–5 channels.
7. Record results in the matrix above.

### 3. What to Record

- **Play Method** — the critical indicator. Direct Play = fast, everything else = slower.
- **Switch Time** — seconds from channel selection to first frame.
- **Audio/Video codec** — from playback info overlay (e.g., H.264 + AAC).
- **Any errors** — buffering, 401, black screen, audio-only.

## Common Client Issues

| Symptom | Likely Cause | Fix |
|---|---|---|
| Direct Play on web but Transcode on mobile | Mobile client reports limited codec support | Check client settings → allow all codecs |
| Works on Android but not iOS | Safari/WebKit H.264 profile level restrictions | Ensure TVHeadend outputs Baseline/Main profile |
| 401 on stream start | Auth token not reaching TVHeadend from client | Verify token is alphanumeric, check proxy headers |
| Black screen, audio plays | Client can't decode video codec | Switch to `jellyfin` profile for consistent H.264 |
| "Transcoding" shown for every client | Probing detects unsupported format | Run Diagnose → check profile type and codec links |

## Network Topology Considerations

| Scenario | Expected Behavior |
|---|---|
| Client → TVHeadend directly | Best case with `Direct to TVHeadend` delivery mode: Direct Play works, fastest switching |
| Client → Jellyfin relay → TVHeadend | Default (`Relay` delivery mode): client plays the token-secured relay URL; Direct Play still possible, clients only need to reach Jellyfin |
| Client → Reverse Proxy → TVHeadend | Works if proxy passes auth headers; verify stream URL reachability |
| Client on different subnet/VLAN | May need routing/firewall rules for direct TVHeadend access (not needed in `Relay` mode) |

## Reporting Results

When filing issues or discussing compatibility:

1. Include Jellyfin version, TVHeadend version, client name + version.
2. Include the playback mode (Direct Play / Direct Stream / Transcode).
3. Include the Streaming Profile name and type (pass/transcode).
4. Include the Diagnose output (compatibility score + checks).
5. Redact auth tokens and passwords from logs.

