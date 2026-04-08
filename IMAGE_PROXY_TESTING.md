# Image Proxy Testing Guide

This guide documents how to test the Image Proxy feature of the TVHeadend API plugin.

## Overview

The Image Proxy is a secure endpoint that:
- Proxies image requests through Jellyfin's authorization layer
- Fetches channel icons and EPG artwork from TVHeadend
- Masks sensitive credentials in logs
- Automatically handles all image rendering

## Prerequisites

1. Jellyfin instance with TVHeadend API plugin installed
2. TVHeadend backend configured and running
3. TVHeadend channels with icons/artwork set up
4. Valid TVHeadend credentials configured in the plugin

## Test Scenarios

### 1. Verify Channel Icons Load

**Steps:**
1. Open Jellyfin Web UI → **Live TV**
2. Navigate to the **Channels** view
3. Observe channel list

**Expected Results:**
- ✅ Channel icons appear next to channel names
- ✅ Icons are loaded without errors
- ✅ Icons match TVHeadend's configured channel icons

**Debug Info:**
- Check browser Network tab → filter for `ImageProxy` requests
- All requests should return **HTTP 200**
- Check Jellyfin logs for proxy endpoint calls

### 2. Verify EPG Artwork Displays

**Steps:**
1. Open Jellyfin Web UI → **Live TV**
2. Navigate to **Programs** or **Upcoming**
3. Select any program/event
4. View the program details page

**Expected Results:**
- ✅ Program artwork/poster appears (if available in TVHeadend EPG)
- ✅ Image loads without errors
- ✅ No credentials visible in requests or browser console

**Debug Info:**
- Browser Network tab should show `ImageProxy` requests with query parameters
- Response headers should include appropriate `Content-Type` (e.g., `image/jpeg`)

### 3. Check Log Masking

**Steps:**
1. Open Jellyfin Dashboard
2. Navigate to **Settings → Logs**
3. Filter for `ImageProxy` or `ConstructImageUrl` messages
4. Review recent plugin logs

**Expected Results:**
- ✅ Logs show image proxy activities
- ✅ **No TVHeadend URLs with embedded credentials** in logs
- ✅ All sensitive data is masked or redacted
- Example log entry:
  ```
  [ImageProxy] Fetching image from TVHeadend: {masked_url}
  ```

### 4. Verify Proxy Endpoint Security

**Steps:**
1. Open browser Developer Tools (F12)
2. Navigate to Live TV with a logged-in session
3. Inspect Network tab → filter for `ImageProxy` requests
4. Try to access the proxy URL directly without authentication

**Expected Results:**
- ✅ Authenticated requests (from Jellyfin session) succeed (HTTP 200)
- ✅ Image proxy URLs are only accessible to authenticated users
- ✅ Unauthenticated requests should return HTTP 401 or 403
- ✅ No TVHeadend credentials appear in the URL

### 5. Test with Invalid Image Paths

**Steps:**
1. Check Jellyfin plugin logs for any error handling scenarios
2. Test edge cases:
   - Empty image path
   - Non-existent image ID
   - Malformed image path

**Expected Results:**
- ✅ Invalid paths return appropriate HTTP errors (400/404)
- ✅ Error responses don't leak sensitive information
- ✅ Logs contain clear error messages for debugging

## API Endpoint Details

### Endpoint

```
GET /api/TvHeadendApi/ImageProxy?imagePath={escaped_path}
```

### Authentication

- Requires Jellyfin user authentication (via session cookie or token)
- Requires elevated privileges (admin access)

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `imagePath` | string | Yes | TVHeadend image endpoint relative path (e.g., `imagecache/1715`) |

### Response

- **Success (HTTP 200):**
  - Content-Type: `image/jpeg`, `image/png`, or other image MIME type
  - Body: Raw image data

- **Error (HTTP 4xx/5xx):**
  - JSON response with error details
  - Example: `{"error": "Invalid image path"}`

## Performance Considerations

- **First request:** Image is fetched from TVHeadend (~100-500ms depending on network)
- **Subsequent requests:** Jellyfin caches images on disk; requests hit the cache
- **Cache location:** `<jellyfin-data>/cache/images/`
- **Image proxy latency:** Typically <50ms for cached images

## Troubleshooting

### Images Not Loading

**Symptoms:** Channel icons or EPG artwork missing

**Steps to diagnose:**
1. Check Jellyfin plugin logs for ImageProxy errors
2. Verify TVHeadend connectivity (run Diagnose from plugin page)
3. Confirm channel icons are configured in TVHeadend
4. Check browser console for JavaScript errors
5. Verify Jellyfin user has admin privileges

### Proxy Endpoint Returns 404

**Symptoms:** Image proxy requests return HTTP 404

**Steps to diagnose:**
1. Verify `imagePath` parameter is URL-encoded correctly
2. Check that the image exists in TVHeadend
3. Test direct TVHeadend API call (requires auth):
   ```bash
   curl -u user:pass http://tvheadend:9981/imagecache/1715
   ```
4. Review plugin logs for path handling errors

### Credentials Exposed in Logs

**Symptoms:** TVHeadend URLs with credentials appear in Jellyfin logs

**Steps to verify:**
1. This should NOT happen — the plugin automatically masks sensitive data
2. If observed, report as a security issue
3. Check plugin version — this may be fixed in newer versions

## Manual Testing Commands

### Test Image Endpoint via curl (requires Jellyfin API key or auth)

```bash
# With Basic Auth (if using legacy API access):
curl -u "jellyfin_user:jellyfin_pass" \
  "http://jellyfin-host:8096/api/TvHeadendApi/ImageProxy?imagePath=imagecache%2F1715" \
  -o test_image.jpg

# With API Key:
curl -H "X-Emby-Authorization: MediaBrowser Token=\"your-api-key\"" \
  "http://jellyfin-host:8096/api/TvHeadendApi/ImageProxy?imagePath=imagecache%2F1715" \
  -o test_image.jpg
```

### Verify TVHeadend Backend Connectivity

```bash
# Test TVHeadend imagecache endpoint directly:
curl -u "tvheadend_user:tvheadend_pass" \
  http://tvheadend-host:9981/imagecache/1715 \
  -o tvheadend_image.jpg

# Check response headers:
curl -u "tvheadend_user:tvheadend_pass" \
  -I http://tvheadend-host:9981/imagecache/1715
```

## Feature Completeness Checklist

- [x] Image proxy endpoint implemented
- [x] Channel icons proxied through endpoint
- [x] EPG artwork proxied through endpoint
- [x] Credential masking in logs
- [x] Authentication required (Jellyfin session)
- [x] Error handling for invalid paths
- [x] Content-Type detection from TVHeadend response
- [x] StyleCop compliance (all classes and methods ordered correctly)
- [x] Build succeeds without warnings
- [x] Documentation updated

## Related Code Files

- `Jellyfin.Plugin.TvHeadendApi/Service/LiveTvService.cs`
  - `ConstructImageUrl()` — Builds TVHeadend image URLs with embedded credentials
  - `FetchImageAsync()` — Fetches image from TVHeadend
  
- `Jellyfin.Plugin.TvHeadendApi/Api/TvHeadendApiController.cs`
  - `GetImageProxy()` — Jellyfin API endpoint that proxies requests

- `Jellyfin.Plugin.TvHeadendApi/Configuration/PluginConfiguration.cs`
  - Authentication settings used by proxy (username, password, auth token)

## See Also

- README.md — Image Proxy section in main documentation
- AGENTS.md — Architecture overview
- copilot-instructions.md — Developer workflow

