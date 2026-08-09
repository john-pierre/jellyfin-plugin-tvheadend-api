# Apple AVPlayer Compatibility Test Suite

## Overview

The `AppleAvPlayerCompatibilityTests` test suite validates that the plugin's relay endpoints are fully compatible with Apple AVPlayer, which is the underlying media player used by:

- **iPhone / iPad** (native Video app, Swiftfin)
- **Apple TV** (Swiftfin, Infuse, native clients)
- **macOS** (Safari, any AVPlayer-based app)

AVPlayer has strict HTTP requirements that many streaming backends fail to satisfy. This test suite exercises every critical aspect of the relay pipeline.

## Test Categories (33 tests)

### 1. HEAD Request Validation (2 tests)
AVPlayer sends HEAD before GET to discover Content-Type, Content-Length, and range support.
- `HeadRequest_StreamEndpoint_ShouldReturnHeadersWithoutBody`
- `HeadRequest_ImageEndpoint_ShouldReturnValidHeaders`

### 2. Range Request Validation (2 tests)
AVPlayer uses `Range: bytes=0-1023` to test partial content support.
- `RangeRequest_StreamRelay_ShouldDocumentMissingRangeSupport`
- `RangeRequest_ImageRelay_ShouldSupportRangeProcessing`

### 3. Content-Type Validation (10 tests)
Correct MIME types are critical for AVPlayer format detection.
- `ContentType_StreamRelay_ShouldPreserveUpstreamMimeType` (6 cases: video/mp2t, video/MP2T, HLS variants, video/mp4, audio/aac)
- `ContentType_StreamRelay_DefaultsToVideoMp2t_WhenUpstreamHasNoContentType`
- `ContentType_StreamRelay_ShouldOverrideGenericMimeTypes` (3 cases: octet-stream, text/plain, text/html)

### 4. Redirect / Relay Security Validation (2 tests)
Ensures no internal TVHeadend URLs are exposed to clients.
- `Redirect_ShouldBeFollowedTransparently_WithoutExposingLocation`
- `Redirect_ChainExceedingMax_ShouldReturnGatewayError`

### 5. Secret Leakage Validation (4 tests)
Verifies no credentials appear in URLs, headers, or response metadata.
- `SecretLeakage_RelayResult_ShouldNeverContainCredentials`
- `SecretLeakage_UrlBuilder_ShouldMaskCredentials`
- `SecretLeakage_ResourceUrl_ShouldNotContainPlaintextCredentials`
- `SecretLeakage_ResourceUrl_WithAuth_ShouldOnlyContainAuthToken`

### 6. Stream Signature Validation (4 tests)
Verifies first bytes match expected format signatures.
- `StreamSignature_MpegTs_ShouldStartWithSyncByte` (0x47)
- `StreamSignature_Hls_ShouldStartWithExtm3u` (#EXTM3U)
- `StreamSignature_Mp4_ShouldContainFtypAtom` (ftyp atom)
- `StreamSignature_EmptyPayload_ShouldBeDetected`

### 7. Stability Validation (4 tests)
Ensures relay handles concurrent and sequential load without issues.
- `Stability_SequentialRequests_ShouldAllSucceed` (10 sequential)
- `Stability_ParallelRequests_ShouldNotCrash` (5 parallel)
- `Stability_ResponseStartupTime_ShouldBeReasonable` (<5s)
- `Stability_CancelledRequest_ShouldNotHang`

### 8. Apple Specific Behavior Checks (5 tests)
Comprehensive AVPlayer compatibility verification.
- `AppleCompatibility_ComprehensiveCheck_ShouldDocumentAllFindings`
- `AppleCompatibility_NoAuthSurprise_StreamShouldNotRequireReAuth`
- `AppleCompatibility_ImageRelay_ShouldPreserveMimeAndContent` (3 cases: PNG, JPEG, SVG)

## Running the Tests

```bash
# Run only Apple AVPlayer compatibility tests
dotnet test --filter "FullyQualifiedName~AppleAvPlayerCompatibilityTests" -v n

# Run as part of the full suite (CI)
dotnet test --filter "Category!=LiveIntegration"
```

## Implemented Fixes

All 6 discovered incompatibilities have been fixed:

### ✅ Fix 1: HEAD Support on Stream Endpoint (Priority 1 — Critical)
**File:** `RelayController.cs`
**Change:** Added `[HttpHead("stream/{channelId}")]` and `[HttpHead("relay/stream/{channelId}")]` attributes. The controller now detects HEAD requests and returns response headers (Content-Type, Accept-Ranges, Content-Length) without streaming the body.
**Impact:** AVPlayer can now discover stream capabilities before initiating playback.

### ✅ Fix 2: Accept-Ranges Header on Stream Responses (Priority 2/5 — Critical)
**Files:** `RelayResult.cs`, `RelayService.cs`, `RelayController.cs`
**Change:** Added `AcceptRanges` property to `RelayResult`. The relay now captures the upstream `Accept-Ranges` header and passes it through to the client. For live streams without upstream range support, `Accept-Ranges: none` is set explicitly.
**Impact:** AVPlayer now receives correct range capability information. `Accept-Ranges: none` tells AVPlayer not to attempt byte-range requests on live streams.

### ✅ Fix 3: Redirect Chain No Longer Leaks Internal URLs (Priority 3 — Security)
**File:** `RelayService.cs` (`MapUpstreamStatus`)
**Change:** Added a case for 3xx status codes: `_ when (int)statusCode >= 300 && (int)statusCode < 400 => 502`. Unfollowed redirects are now mapped to 502 Bad Gateway instead of being passed through with the internal Location header.
**Impact:** Internal TVHeadend hostnames, IPs, ports, and credentials can no longer be leaked to clients through redirect responses.

### ✅ Fix 4: Default Content-Type Uses Lowercase (Priority 4 — Medium)
**File:** `RelayController.cs`
**Change:** Changed default from `"video/MP2T"` to `"video/mp2t"` (IANA-registered lowercase form).
**Impact:** Full compliance with case-sensitive AVPlayer implementations.

### ✅ Fix 5: Accept-Ranges Header Present (Priority 5 — Medium)
**(Combined with Fix 2 above)**

### ✅ Fix 6: Generic Content-Types Overridden for Streams (Priority 6 — Low)
**File:** `RelayService.cs` (new `NormalizeStreamContentType` method)
**Change:** Added content-type normalization in `RelayStreamAsync`. Generic MIME types (`application/octet-stream`, `text/plain`, `text/html`) are automatically replaced with `video/mp2t` for stream responses.
**Impact:** AVPlayer can now correctly identify the stream format even when TVHeadend returns incorrect or generic content types.

## CI Integration

These tests run automatically as part of the existing CI pipeline:
- **Build & Test job** in `build-release.yaml` uses `--filter "Category!=LiveIntegration"` which includes all Apple AVPlayer tests
- No additional CI configuration required — tests use WireMock as a mock TVHeadend backend
- All tests are deterministic, fast (~2-3 seconds total), and require no external services
