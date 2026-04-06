<#
.SYNOPSIS
    Analyse channels through the Jellyfin pipeline (PlaybackInfo -> stream open -> data verification).

.DESCRIPTION
    Builds the plugin, deploys it, authenticates with Jellyfin, then tests N channels
    exclusively through the Jellyfin LiveTV pipeline. Measures PlaybackInfo latency,
    time-to-first-byte, stream data rate, and generates a Markdown report.

    Default mode "matrix" generates all possible combinations of plugin config parameters
    (DirectPlay, DirectStream, Transcoding, Probing, IgnoreDts, BufferMs, AnalyzeDurationMs)
    crossed with the requested streaming profiles, to find the optimal configuration for
    minimal stream start latency.

.PARAMETER MaxChannels
    Number of channels to test. 0 = all channels.
    Can also be set via environment variable CHANNEL_COUNT.

.PARAMETER StreamHoldSec
    How many seconds to keep each stream open. Default: 30.
    Can also be set via environment variable STREAM_HOLD_SEC.

.PARAMETER MinStreamHoldSec
    Minimum seconds each stream must stay open. Default: 60.
    Can also be set via environment variable MIN_STREAM_HOLD_SEC.

.PARAMETER JellyfinUrl
    Jellyfin base URL. Default: http://localhost:8096.
    Can also be set via environment variable JELLYFIN_URL.

.PARAMETER JellyfinUser
    Jellyfin admin username. Default: root.
    Can also be set via environment variable JELLYFIN_USER.

.PARAMETER JellyfinPassword
    Jellyfin admin password. Default: admin.
    Can also be set via environment variable JELLYFIN_PASSWORD.

.PARAMETER SkipBuild
    Skip building and redeploying the plugin.

.PARAMETER PauseBetweenTestsSec
    Pause between channel tests. Default: 3.

.PARAMETER PlaybackInfoTimeoutSec
    Timeout for PlaybackInfo API calls. Default: 30.

.PARAMETER ReportPath
    Output path for the Markdown report.

.PARAMETER Scenarios
    Comma-separated list of scenarios to run. Default: "matrix" (generate all combinations).
    Available: matrix, directplay, probing, transcoding, probing+transcoding, directstream, analyze-low, analyze-high, all.
    "matrix" generates all combinations of DirectPlay/DirectStream/Transcoding/Probing/IgnoreDts/BufferMs/AnalyzeDurationMs.
    "all" runs all predefined named scenarios.
    "current" is deprecated and maps to "directplay".

.PARAMETER StreamingProfiles
    Comma-separated TVH streaming profiles to test. Default: "jellyfin,pass".
    Examples: "jellyfin,pass", "pass", "current".

.PARAMETER ClientProfile
    Jellyfin client request profile to emulate. Default: "web".
    Available: web, legacy.

.PARAMETER ClientDeviceId
    DeviceId used for Jellyfin auth and playback requests.
    Can also be set via environment variable JELLYFIN_DEVICE_ID.

.PARAMETER PlaybackTemplatePath
    Optional path to a JSON template with captured Jellyfin Web headers/body.
    Supports top-level properties: Headers and Body.

.PARAMETER AnalyzeDurationValues
    Comma-separated AnalyzeDurationMs values for the matrix. Default: "0,200,2000".
    Only varied when SupportsProbing is true; otherwise the first value is used.

.PARAMETER BufferMsValues
    Comma-separated BufferMs values for the matrix. Default: "0".
    Add e.g. "0,1000,3000" to test different buffer sizes.

.PARAMETER IgnoreDtsValues
    Comma-separated boolean values for the IgnoreDts axis. Default: "false,true".
    Use "false" to test only with DTS enabled, "false,true" for both.

.EXAMPLE
    .\analyze-tvh.ps1 -MaxChannels 2 -StreamHoldSec 10 -SkipBuild

.EXAMPLE
    .\analyze-tvh.ps1 -MaxChannels 1 -SkipBuild -Scenarios "matrix" -StreamingProfiles "pass,jellyfin"

.EXAMPLE
    .\analyze-tvh.ps1 -MaxChannels 2 -Scenarios "directplay,probing,transcoding"

.EXAMPLE
    .\analyze-tvh.ps1 -MaxChannels 1 -SkipBuild -Scenarios "matrix" -AnalyzeDurationValues "0,200,2000,5000" -BufferMsValues "0,1000" -IgnoreDtsValues "false,true"
#>
param(
    [int]$MaxChannels            = $(if ($env:CHANNEL_COUNT)    { [int]$env:CHANNEL_COUNT }    else { 1 }),
    [int]$StreamHoldSec          = $(if ($env:STREAM_HOLD_SEC)  { [int]$env:STREAM_HOLD_SEC }  else { 10 }),
    [int]$MinStreamHoldSec       = $(if ($env:MIN_STREAM_HOLD_SEC) { [int]$env:MIN_STREAM_HOLD_SEC } else { 10 }),
    [string]$JellyfinUrl         = $(if ($env:JELLYFIN_URL)     { $env:JELLYFIN_URL }          else { "http://localhost:8096" }),
    [string]$JellyfinUser        = $(if ($env:JELLYFIN_USER)    { $env:JELLYFIN_USER }         else { "root" }),
    [string]$JellyfinPassword    = $(if ($env:JELLYFIN_PASSWORD){ $env:JELLYFIN_PASSWORD }     else { "admin" }),
    [switch]$SkipBuild,
    [int]$PauseBetweenTestsSec   = 3,
    [int]$PlaybackInfoTimeoutSec = 30,
    [string]$ReportPath          = "",
    [string]$Scenarios           = "matrix",
    [string]$StreamingProfiles   = "pass",
    [string]$ArtifactRoot        = "",
    [string]$ClientProfile       = $(if ($env:JELLYFIN_CLIENT_PROFILE) { $env:JELLYFIN_CLIENT_PROFILE } else { "web" }),
    [string]$ClientName          = $(if ($env:JELLYFIN_CLIENT_NAME) { $env:JELLYFIN_CLIENT_NAME } else { "Jellyfin Web" }),
    [string]$ClientDevice        = $(if ($env:JELLYFIN_CLIENT_DEVICE) { $env:JELLYFIN_CLIENT_DEVICE } else { "Chrome" }),
    [string]$ClientVersion       = $(if ($env:JELLYFIN_CLIENT_VERSION) { $env:JELLYFIN_CLIENT_VERSION } else { "10.10.7" }),
    [string]$ClientDeviceId      = $(if ($env:JELLYFIN_DEVICE_ID) { $env:JELLYFIN_DEVICE_ID } else { "analyze-tvh-web" }),
    [string]$ClientUserAgent     = $(if ($env:JELLYFIN_USER_AGENT) { $env:JELLYFIN_USER_AGENT } else { "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36" }),
    [string]$ClientAcceptLanguage = $(if ($env:JELLYFIN_ACCEPT_LANGUAGE) { $env:JELLYFIN_ACCEPT_LANGUAGE } else { "de-DE,de;q=0.9,en-US;q=0.8,en;q=0.7" }),
    [int]$ClientMaxStreamingBitrate = $(if ($env:JELLYFIN_MAX_STREAMING_BITRATE) { [int]$env:JELLYFIN_MAX_STREAMING_BITRATE } else { 437394831 }),
    [int]$ClientProfileMaxStreamingBitrate = $(if ($env:JELLYFIN_PROFILE_MAX_STREAMING_BITRATE) { [int]$env:JELLYFIN_PROFILE_MAX_STREAMING_BITRATE } else { 120000000 }),
    [int]$ClientProfileMaxStaticBitrate = $(if ($env:JELLYFIN_PROFILE_MAX_STATIC_BITRATE) { [int]$env:JELLYFIN_PROFILE_MAX_STATIC_BITRATE } else { 100000000 }),
    [string]$PlaybackTemplatePath = $(if ($env:JELLYFIN_PLAYBACK_TEMPLATE) { $env:JELLYFIN_PLAYBACK_TEMPLATE } else { "" }),
    [string]$AnalyzeDurationValues = "200",
    [string]$BufferMsValues      = "500",
    [string]$IgnoreDtsValues     = "true"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$PLUGIN_GUID    = "ae9f5148-d656-43ab-ab83-94192f9e840a"
$CONTAINER_NAME = "jellyfin-tvheadend-dev"
$SCRIPT_VERSION = "5.0.0"
$TEST_START     = Get-Date
$REPO_ROOT      = Split-Path -Parent $PSScriptRoot
$REPORTS_ROOT   = Join-Path $REPO_ROOT "reports"

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $timestamp = $TEST_START.ToString("yyyyMMdd_HHmmss")
    $ReportPath = Join-Path $REPORTS_ROOT "report_$timestamp.md"
}

if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    $timestamp = $TEST_START.ToString("yyyyMMdd_HHmmss")
    $ArtifactRoot = Join-Path $REPORTS_ROOT "artifacts_$timestamp"
}

$Script:AuthToken        = $null
$Script:UserId           = $null
$Script:AuthHeader       = @{}
$Script:ClientIdentity   = $null
$Script:PluginConfig     = $null
$Script:TvhBaseUrl       = $null
$Script:TvhCredential    = $null
$Script:TvhToJellyfinMap = @{}
$Script:WebPlaybackTemplate = $null
$Script:RequestedStreamHoldSec = $StreamHoldSec

if ($MinStreamHoldSec -lt 0) { $MinStreamHoldSec = 0 }
if ($StreamHoldSec -lt $MinStreamHoldSec) {
    $StreamHoldSec = $MinStreamHoldSec
}

# --- Scenario definitions ---------------------------------------------------

$Script:ScenarioDefinitions = [ordered]@{
    "directplay" = [ordered]@{
        Label              = "A: DirectPlay (Baseline)"
        Description        = "DirectPlay + DirectStream enabled, no probing, no transcoding"
        SupportsDirectPlay   = $true
        SupportsDirectStream = $true
        SupportsTranscoding  = $false
        SupportsProbing      = $false
        IgnoreDts            = $false
        BufferMs             = 500
        AnalyzeDurationMs    = 200
    }
    "probing" = [ordered]@{
        Label              = "B: With Probing"
        Description        = "DirectPlay + DirectStream enabled, FFmpeg probing enabled"
        SupportsDirectPlay   = $true
        SupportsDirectStream = $true
        SupportsTranscoding  = $false
        SupportsProbing      = $true
        IgnoreDts            = $true
        BufferMs             = 500
        AnalyzeDurationMs    = 200
    }
    "transcoding" = [ordered]@{
        Label              = "C: Transcoding"
        Description        = "Transcoding enabled, DirectPlay/DirectStream disabled"
        SupportsDirectPlay   = $false
        SupportsDirectStream = $false
        SupportsTranscoding  = $true
        SupportsProbing      = $false
        IgnoreDts            = $true
        BufferMs             = 500
        AnalyzeDurationMs    = 200
    }
    "probing+transcoding" = [ordered]@{
        Label              = "D: Probing + Transcoding"
        Description        = "Both probing and transcoding enabled"
        SupportsDirectPlay   = $false
        SupportsDirectStream = $false
        SupportsTranscoding  = $true
        SupportsProbing      = $true
        IgnoreDts            = $true
        BufferMs             = 500
        AnalyzeDurationMs    = 200
    }
    "directstream" = [ordered]@{
        Label              = "E: DirectStream Only"
        Description        = "Only DirectStream (remux), no DirectPlay, no transcoding"
        SupportsDirectPlay   = $false
        SupportsDirectStream = $true
        SupportsTranscoding  = $false
        SupportsProbing      = $false
        IgnoreDts            = $true
        BufferMs             = 500
        AnalyzeDurationMs    = 200
    }
}

$Script:OriginalConfig = $null

# --- Matrix generator -------------------------------------------------------

function Build-MatrixScenarios {
    param(
        [int[]]$AnalyzeDurations = @(0, 200, 2000),
        [int[]]$BufferValues = @(0),
        [bool[]]$IgnoreDtsChoices = @($false, $true)
    )

    $dpValues = @($true, $false)
    $dsValues = @($true, $false)
    $trValues = @($true, $false)
    $prValues = @($true, $false)

    $generated = [ordered]@{}
    $counter = 0

    foreach ($dp in $dpValues) {
        foreach ($ds in $dsValues) {
            foreach ($tr in $trValues) {
                # At least one playback method must be enabled
                if (-not $dp -and -not $ds -and -not $tr) { continue }

                foreach ($pr in $prValues) {
                    foreach ($idt in $IgnoreDtsChoices) {
                        foreach ($buf in $BufferValues) {
                            # AnalyzeDurationMs only varies when probing is enabled;
                            # when probing is off, use the smallest value once.
                            $analyzeDurationsForThis = if ($pr) { $AnalyzeDurations } else { @($AnalyzeDurations[0]) }
                            foreach ($adur in $analyzeDurationsForThis) {
                                $counter++
                                $key = "m{0:D3}" -f $counter

                                $dpStr  = if ($dp)  { "DP"  } else { "" }
                                $dsStr  = if ($ds)  { "DS"  } else { "" }
                                $trStr  = if ($tr)  { "TR"  } else { "" }
                                $prStr  = if ($pr)  { "Probe" } else { "" }
                                $idtStr = if ($idt) { "IgnDts" } else { "" }
                                $parts  = @($dpStr, $dsStr, $trStr, $prStr, $idtStr) | Where-Object { $_ }
                                $flagSummary = $parts -join "+"

                                $label = "M{0:D3}: {1} Buf={2} Analyze={3}" -f $counter, $flagSummary, $buf, $adur

                                $generated[$key] = [ordered]@{
                                    Label              = $label
                                    Description        = "DirectPlay=$dp DirectStream=$ds Transcoding=$tr Probing=$pr IgnoreDts=$idt Buffer=${buf}ms Analyze=${adur}ms"
                                    SupportsDirectPlay   = $dp
                                    SupportsDirectStream = $ds
                                    SupportsTranscoding  = $tr
                                    SupportsProbing      = $pr
                                    IgnoreDts            = $idt
                                    BufferMs             = $buf
                                    AnalyzeDurationMs    = $adur
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    Write-OK "Matrix generated: $counter scenarios (from $($dpValues.Count) DP x $($dsValues.Count) DS x $($trValues.Count) TR x $($prValues.Count) Probe x $($IgnoreDtsChoices.Count) IgnoreDts x $($BufferValues.Count) Buffer x $($AnalyzeDurations.Count) Analyze)"
    return $generated
}

# --- Helpers ----------------------------------------------------------------

function Write-Step  { param([string]$Msg); Write-Host "`n=== $Msg" -ForegroundColor Cyan; Write-Host ("=" * 70) -ForegroundColor DarkCyan }
function Write-Info  { param([string]$Msg); Write-Host "  > $Msg" -ForegroundColor Gray }
function Write-OK    { param([string]$Msg); Write-Host "  [OK] $Msg" -ForegroundColor Green }
function Write-Warn  { param([string]$Msg); Write-Host "  [WARN] $Msg" -ForegroundColor Yellow }
function Write-Err   { param([string]$Msg); Write-Host "  [ERR] $Msg" -ForegroundColor Red }

function Convert-ToSafeName {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return "unnamed" }
    $safe = $Value -replace '[\\/:*?"<>|\[\]]', '_' -replace '\s+', '_'
    return $safe.Trim('_')
}

function Ensure-Directory {
    param([string]$Path)
    if (-not (Test-Path $Path)) { New-Item -ItemType Directory -Path $Path -Force | Out-Null }
}

function Get-UrlEncodedValue {
    param([AllowNull()][string]$Value)
    if ($null -eq $Value) { return "" }
    return [System.Uri]::EscapeDataString($Value)
}

function Copy-DeepObject {
    param([AllowNull()][object]$InputObject)
    if ($null -eq $InputObject) { return $null }
    return ($InputObject | ConvertTo-Json -Depth 30 | ConvertFrom-Json)
}

function Set-ObjectPropertyValue {
    param(
        [object]$Object,
        [string]$Name,
        [AllowNull()][object]$Value
    )

    if ($null -eq $Object -or [string]::IsNullOrWhiteSpace($Name)) { return }
    if ($Object -is [System.Collections.IDictionary]) {
        $Object[$Name] = $Value
        return
    }

    $prop = $Object.PSObject.Properties[$Name]
    if ($prop) {
        $prop.Value = $Value
    } else {
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value -Force
    }
}

function Convert-ToAbsoluteJellyfinUrl {
    param([string]$UrlOrPath)

    if ([string]::IsNullOrWhiteSpace($UrlOrPath)) { return $null }
    if ($UrlOrPath -match '^https?://') { return $UrlOrPath }
    if ($UrlOrPath.StartsWith('/')) { return "$JellyfinUrl$UrlOrPath" }
    return "$($JellyfinUrl.TrimEnd('/'))/$UrlOrPath"
}

function Test-IsJellyfinManagedUrl {
    param([string]$UrlOrPath)

    if ([string]::IsNullOrWhiteSpace($UrlOrPath)) { return $false }
    if (-not ($UrlOrPath -match '^https?://')) { return $UrlOrPath.StartsWith('/') }

    try {
        $targetUri = [System.Uri]$UrlOrPath
        $jellyfinUri = [System.Uri]$JellyfinUrl
        return ($targetUri.Host -eq $jellyfinUri.Host -and $targetUri.Port -eq $jellyfinUri.Port)
    } catch {
        return $false
    }
}

function Import-WebPlaybackTemplate {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { return }

    $resolvedPath = if ([System.IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $PSScriptRoot $Path }
    if (-not (Test-Path $resolvedPath)) {
        Write-Warn "Playback template not found: $resolvedPath"
        return
    }

    try {
        $raw = Get-Content -Path $resolvedPath -Raw -Encoding UTF8
        $json = $raw | ConvertFrom-Json
    } catch {
        Write-Warn "Playback template parse failed: $($_.Exception.Message)"
        return
    }

    $headers = [ordered]@{}
    $body = $null

    if ($json.PSObject.Properties.Name -contains 'Headers') {
        foreach ($p in $json.Headers.PSObject.Properties) { $headers[$p.Name] = [string]$p.Value }
    }

    if ($json.PSObject.Properties.Name -contains 'Body') {
        $body = $json.Body
    } elseif ($json.PSObject.Properties.Name -contains 'DeviceProfile' -or $json.PSObject.Properties.Name -contains 'UserId') {
        $body = $json
    }

    $Script:WebPlaybackTemplate = [ordered]@{
        SourcePath = $resolvedPath
        Headers = $headers
        Body = $body
    }

    if ($headers.Contains('User-Agent') -and -not [string]::IsNullOrWhiteSpace($headers['User-Agent'])) {
        $script:ClientUserAgent = $headers['User-Agent']
    }
    if ($headers.Contains('Accept-Language') -and -not [string]::IsNullOrWhiteSpace($headers['Accept-Language'])) {
        $script:ClientAcceptLanguage = $headers['Accept-Language']
    }

    $authTemplate = $null
    if ($headers.Contains('Authorization')) { $authTemplate = [string]$headers['Authorization'] }
    elseif ($headers.Contains('X-Emby-Authorization')) { $authTemplate = [string]$headers['X-Emby-Authorization'] }

    if (-not [string]::IsNullOrWhiteSpace($authTemplate)) {
        if ($authTemplate -match 'Client="([^"]+)"') { $script:ClientName = [System.Uri]::UnescapeDataString($Matches[1]) }
        if ($authTemplate -match 'Device="([^"]+)"') { $script:ClientDevice = [System.Uri]::UnescapeDataString($Matches[1]) }
        if ($authTemplate -match 'DeviceId="([^"]+)"') { $script:ClientDeviceId = [System.Uri]::UnescapeDataString($Matches[1]) }
        if ($authTemplate -match 'Version="([^"]+)"') { $script:ClientVersion = [System.Uri]::UnescapeDataString($Matches[1]) }
    }

    Write-OK "Loaded web playback template: $resolvedPath"
}

function New-JellyfinAuthorizationValue {
    param([string]$Token = "")

    $templateAuth = $null
    if ($Script:WebPlaybackTemplate -and $Script:WebPlaybackTemplate.Headers) {
        if ($Script:WebPlaybackTemplate.Headers.Contains('Authorization')) {
            $templateAuth = [string]$Script:WebPlaybackTemplate.Headers['Authorization']
        } elseif ($Script:WebPlaybackTemplate.Headers.Contains('X-Emby-Authorization')) {
            $templateAuth = [string]$Script:WebPlaybackTemplate.Headers['X-Emby-Authorization']
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($templateAuth)) {
        $base = ($templateAuth -replace ',\s*Token="[^"]*"', '') -replace 'Token="[^"]*",\s*', ''
        if (-not [string]::IsNullOrWhiteSpace($Token)) {
            return ('{0}, Token="{1}"' -f $base, (Get-UrlEncodedValue $Token))
        }
        return $base
    }

    $parts = @(
        ('MediaBrowser Client="{0}"' -f (Get-UrlEncodedValue $ClientName)),
        ('Device="{0}"' -f (Get-UrlEncodedValue $ClientDevice)),
        ('DeviceId="{0}"' -f (Get-UrlEncodedValue $ClientDeviceId)),
        ('Version="{0}"' -f (Get-UrlEncodedValue $ClientVersion))
    )

    if (-not [string]::IsNullOrWhiteSpace($Token)) {
        $parts += ('Token="{0}"' -f (Get-UrlEncodedValue $Token))
    }

    return ($parts -join ', ')
}

function Get-JellyfinDefaultApiHeaders {
    param([string]$Token = "")

    $authorizationValue = New-JellyfinAuthorizationValue -Token $Token
    $headers = [ordered]@{
        "Accept" = "application/json, text/plain, */*"
        "Accept-Language" = $ClientAcceptLanguage
        "Cache-Control" = "no-cache"
        "Pragma" = "no-cache"
        "Origin" = $JellyfinUrl
        "User-Agent" = $ClientUserAgent
    }

    if ($Script:WebPlaybackTemplate -and $Script:WebPlaybackTemplate.Headers) {
        foreach ($k in $Script:WebPlaybackTemplate.Headers.Keys) {
            if ($k -match '^(Authorization|X-Emby-Authorization|X-Emby-Token|X-MediaBrowser-Token)$') { continue }
            if ($k -match '^(Host|Connection|Content-Length|Transfer-Encoding|Expect|Date)$') { continue }
            $headers[$k] = [string]$Script:WebPlaybackTemplate.Headers[$k]
        }
    }

    $headers["X-Emby-Authorization"] = $authorizationValue
    $headers["Authorization"] = $authorizationValue

    if (-not [string]::IsNullOrWhiteSpace($Token)) {
        $headers["X-Emby-Token"] = $Token
        $headers["X-MediaBrowser-Token"] = $Token
    }

    return $headers
}

function Get-JellyfinStreamHeaders {
    param([System.Collections.IDictionary]$RequiredHeaders = $null)

    $headers = Get-JellyfinDefaultApiHeaders -Token $Script:AuthToken
    $headers["Accept"] = "*/*"

    if ($RequiredHeaders) {
        foreach ($k in $RequiredHeaders.Keys) {
            if ($k -match '^(Host|Connection|Content-Length)$') { continue }
            $headers[$k] = [string]$RequiredHeaders[$k]
        }
    }

    return $headers
}

function Set-HttpWebRequestHeaders {
    param(
        [System.Net.HttpWebRequest]$Request,
        [System.Collections.IDictionary]$Headers
    )

    if (-not $Request -or -not $Headers) { return }

    foreach ($key in $Headers.Keys) {
        $value = [string]$Headers[$key]
        switch -Regex ($key) {
            '^Accept$' { $Request.Accept = $value; continue }
            '^User-Agent$' { $Request.UserAgent = $value; continue }
            default { $Request.Headers[$key] = $value }
        }
    }
}

function Resolve-HlsSegmentUrl {
    param(
        [string]$PlaylistUrl,
        [System.Collections.IDictionary]$Headers,
        [int]$TimeoutSec = 10,
        [int]$Depth = 0,
        [int]$MaxAttempts = 8,
        [int]$RetryDelayMs = 750,
        [string]$ExcludeUrl = ""
    )

    if ([string]::IsNullOrWhiteSpace($PlaylistUrl) -or $Depth -ge 4) {
        return $null
    }

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            $request = [System.Net.HttpWebRequest]::Create($PlaylistUrl)
            $request.Method = 'GET'
            $request.Timeout = $TimeoutSec * 1000
            $request.ReadWriteTimeout = $TimeoutSec * 1000
            $request.AllowAutoRedirect = $true
            Set-HttpWebRequestHeaders -Request $request -Headers $Headers

            $response = $request.GetResponse()
            try {
                $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
                $content = $reader.ReadToEnd()
            } finally {
                if ($reader) { $reader.Dispose() }
                $response.Close()
            }

            $lines = @($content -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

            # Collect all media segment lines (non-comment, non-m3u8)
            $allSegmentLines = @($lines | Where-Object { -not $_.StartsWith('#') })

            # For live streams pick the LAST (newest) segment; if an ExcludeUrl
            # was given, find the newest segment that is different.
            $candidate = $null
            if ($allSegmentLines.Count -gt 0) {
                # Walk backwards to find a segment that differs from ExcludeUrl
                for ($si = $allSegmentLines.Count - 1; $si -ge 0; $si--) {
                    $segLine = $allSegmentLines[$si]
                    $resolvedCandidate = [System.Uri]::new([System.Uri]$PlaylistUrl, $segLine).AbsoluteUri
                    if ([string]::IsNullOrWhiteSpace($ExcludeUrl) -or $resolvedCandidate -ne $ExcludeUrl) {
                        $candidate = $segLine
                        break
                    }
                }
                # If every segment matches ExcludeUrl, fall through to retry
            }

            # Some fresh event playlists have no media segment yet; fallback to init/preload entries.
            if ([string]::IsNullOrWhiteSpace($candidate)) {
                $mapLine = $lines | Where-Object { $_ -match '^#EXT-X-MAP:' } | Select-Object -First 1
                if ($mapLine -and $mapLine -match 'URI="([^"]+)"') {
                    $candidate = $Matches[1]
                }
            }

            if ([string]::IsNullOrWhiteSpace($candidate)) {
                $hintLine = $lines | Where-Object { $_ -match '^#EXT-X-PRELOAD-HINT:' } | Select-Object -First 1
                if ($hintLine -and $hintLine -match 'URI="([^"]+)"') {
                    $candidate = $Matches[1]
                }
            }

            if (-not [string]::IsNullOrWhiteSpace($candidate)) {
                $resolved = [System.Uri]::new([System.Uri]$PlaylistUrl, $candidate).AbsoluteUri
                if ($resolved -match '(?i)\.m3u8($|\?)') {
                    return Resolve-HlsSegmentUrl -PlaylistUrl $resolved -Headers $Headers -TimeoutSec $TimeoutSec -Depth ($Depth + 1) -MaxAttempts $MaxAttempts -RetryDelayMs $RetryDelayMs -ExcludeUrl $ExcludeUrl
                }

                return [PSCustomObject]@{
                    SegmentUrl = $resolved
                    PlaylistUrl = $PlaylistUrl
                }
            }
        } catch {
        }

        if ($attempt -lt $MaxAttempts) {
            Start-Sleep -Milliseconds $RetryDelayMs
        }
    }

    return $null
}

function Get-StreamCaptureExtension {
    param(
        [string]$Container,
        [string]$ContentType,
        [string]$StreamUrl
    )

    $normalizedContainer = ""
    if (-not [string]::IsNullOrWhiteSpace($Container)) {
        $normalizedContainer = (($Container -split ',')[0]).Trim().ToLowerInvariant()
    }

    switch ($normalizedContainer) {
        "hls" { return "ts" }
        "mpegts" { return "ts" }
        "ts" { return "ts" }
        "mp4" { return "mp4" }
        "m4v" { return "m4v" }
        "m4a" { return "m4a" }
        "m4b" { return "m4b" }
        "mkv" { return "mkv" }
        "matroska" { return "mkv" }
        "mov" { return "mov" }
        "webm" { return "webm" }
        "webma" { return "webm" }
        "mp3" { return "mp3" }
        "aac" { return "aac" }
        "flac" { return "flac" }
        "wav" { return "wav" }
        "ogg" { return "ogg" }
        "oga" { return "ogg" }
        "opus" { return "opus" }
    }

    $normalizedContentType = if ([string]::IsNullOrWhiteSpace($ContentType)) { "" } else { $ContentType.ToLowerInvariant() }
    if ($normalizedContentType -match 'video/mp2t|application/mp2t') { return "ts" }
    if ($normalizedContentType -match 'video/mp4|audio/mp4|application/mp4') { return "mp4" }
    if ($normalizedContentType -match 'x-matroska') { return "mkv" }
    if ($normalizedContentType -match 'webm') { return "webm" }
    if ($normalizedContentType -match 'audio/mpeg') { return "mp3" }
    if ($normalizedContentType -match 'audio/aac') { return "aac" }
    if ($normalizedContentType -match 'audio/flac') { return "flac" }
    if ($normalizedContentType -match 'audio/wav') { return "wav" }
    if ($normalizedContentType -match 'audio/ogg') { return "ogg" }
    if ($normalizedContentType -match 'audio/opus') { return "opus" }

    if (-not [string]::IsNullOrWhiteSpace($StreamUrl)) {
        try {
            $uri = [System.Uri]$StreamUrl
            $fileName = [System.IO.Path]::GetFileName($uri.AbsolutePath)
            if (-not [string]::IsNullOrWhiteSpace($fileName)) {
                $urlExtension = [System.IO.Path]::GetExtension($fileName)
                if (-not [string]::IsNullOrWhiteSpace($urlExtension)) {
                    return $urlExtension.TrimStart('.').ToLowerInvariant()
                }
            }
        } catch {
        }
    }

    return "bin"
}

function Get-WebPlaybackDeviceProfile {
    return [ordered]@{
        MaxStreamingBitrate = $ClientProfileMaxStreamingBitrate
        MaxStaticBitrate = $ClientProfileMaxStaticBitrate
        MusicStreamingTranscodingBitrate = 384000
        DirectPlayProfiles = @(
            @{ Container = "webm"; Type = "Video"; VideoCodec = "vp8,vp9,av1"; AudioCodec = "vorbis,opus" },
            @{ Container = "mp4,m4v"; Type = "Video"; VideoCodec = "h264,hevc,vp9,av1"; AudioCodec = "aac,mp3,mp2,opus,flac,vorbis" },
            @{ Container = "mkv"; Type = "Video"; VideoCodec = "h264,hevc,vp9,av1"; AudioCodec = "aac,mp3,mp2,opus,flac,vorbis" },
            @{ Container = "mov"; Type = "Video"; VideoCodec = "h264"; AudioCodec = "aac,mp3,mp2,opus,flac,vorbis" },
            @{ Container = "opus"; Type = "Audio" },
            @{ Container = "webm"; AudioCodec = "opus"; Type = "Audio" },
            @{ Container = "ts"; AudioCodec = "mp3"; Type = "Audio" },
            @{ Container = "mp3"; Type = "Audio" },
            @{ Container = "aac"; Type = "Audio" },
            @{ Container = "m4a"; AudioCodec = "aac"; Type = "Audio" },
            @{ Container = "m4b"; AudioCodec = "aac"; Type = "Audio" },
            @{ Container = "flac"; Type = "Audio" },
            @{ Container = "webma"; Type = "Audio" },
            @{ Container = "webm"; AudioCodec = "webma"; Type = "Audio" },
            @{ Container = "wav"; Type = "Audio" },
            @{ Container = "ogg"; Type = "Audio" },
            @{ Container = "hls"; Type = "Video"; VideoCodec = "av1,hevc,h264,vp9"; AudioCodec = "aac,mp2,opus,flac" },
            @{ Container = "hls"; Type = "Video"; VideoCodec = "h264"; AudioCodec = "aac,mp3,mp2" }
        )
        TranscodingProfiles = @(
            @{ Container = "mp4"; Type = "Audio"; AudioCodec = "aac"; Context = "Streaming"; Protocol = "hls"; MaxAudioChannels = "2"; MinSegments = "1"; BreakOnNonKeyFrames = $false; EnableAudioVbrEncoding = $true },
            @{ Container = "aac"; Type = "Audio"; AudioCodec = "aac"; Context = "Streaming"; Protocol = "http"; MaxAudioChannels = "2" },
            @{ Container = "mp3"; Type = "Audio"; AudioCodec = "mp3"; Context = "Streaming"; Protocol = "http"; MaxAudioChannels = "2" },
            @{ Container = "opus"; Type = "Audio"; AudioCodec = "opus"; Context = "Streaming"; Protocol = "http"; MaxAudioChannels = "2" },
            @{ Container = "wav"; Type = "Audio"; AudioCodec = "wav"; Context = "Streaming"; Protocol = "http"; MaxAudioChannels = "2" },
            @{ Container = "opus"; Type = "Audio"; AudioCodec = "opus"; Context = "Static"; Protocol = "http"; MaxAudioChannels = "2" },
            @{ Container = "mp3"; Type = "Audio"; AudioCodec = "mp3"; Context = "Static"; Protocol = "http"; MaxAudioChannels = "2" },
            @{ Container = "aac"; Type = "Audio"; AudioCodec = "aac"; Context = "Static"; Protocol = "http"; MaxAudioChannels = "2" },
            @{ Container = "wav"; Type = "Audio"; AudioCodec = "wav"; Context = "Static"; Protocol = "http"; MaxAudioChannels = "2" },
            @{ Container = "mp4"; Type = "Video"; AudioCodec = "aac,mp2,opus,flac"; VideoCodec = "av1,hevc,h264,vp9"; Context = "Streaming"; Protocol = "hls"; MaxAudioChannels = "2"; MinSegments = "1"; BreakOnNonKeyFrames = $false },
            @{ Container = "ts"; Type = "Video"; AudioCodec = "aac,mp3,mp2"; VideoCodec = "h264"; Context = "Streaming"; Protocol = "hls"; MaxAudioChannels = "2"; MinSegments = "1"; BreakOnNonKeyFrames = $false }
        )
        ContainerProfiles = @()
        CodecProfiles = @(
            @{ Type = "VideoAudio"; Codec = "aac"; Conditions = @(@{ Condition = "Equals"; Property = "IsSecondaryAudio"; Value = "false"; IsRequired = $false }) },
            @{ Type = "VideoAudio"; Conditions = @(@{ Condition = "Equals"; Property = "IsSecondaryAudio"; Value = "false"; IsRequired = $false }) },
            @{ Type = "Video"; Codec = "h264"; Conditions = @(
                @{ Condition = "NotEquals"; Property = "IsAnamorphic"; Value = "true"; IsRequired = $false },
                @{ Condition = "EqualsAny"; Property = "VideoProfile"; Value = "high|main|baseline|constrained baseline|high 10"; IsRequired = $false },
                @{ Condition = "EqualsAny"; Property = "VideoRangeType"; Value = "SDR"; IsRequired = $false },
                @{ Condition = "LessThanEqual"; Property = "VideoLevel"; Value = "52"; IsRequired = $false },
                @{ Condition = "NotEquals"; Property = "IsInterlaced"; Value = "true"; IsRequired = $false }
            ) },
            @{ Type = "Video"; Codec = "hevc"; Conditions = @(
                @{ Condition = "NotEquals"; Property = "IsAnamorphic"; Value = "true"; IsRequired = $false },
                @{ Condition = "EqualsAny"; Property = "VideoProfile"; Value = "main|main 10"; IsRequired = $false },
                @{ Condition = "EqualsAny"; Property = "VideoRangeType"; Value = "SDR|HDR10|HLG"; IsRequired = $false },
                @{ Condition = "LessThanEqual"; Property = "VideoLevel"; Value = "183"; IsRequired = $false },
                @{ Condition = "NotEquals"; Property = "IsInterlaced"; Value = "true"; IsRequired = $false }
            ) },
            @{ Type = "Video"; Codec = "vp9"; Conditions = @(
                @{ Condition = "EqualsAny"; Property = "VideoRangeType"; Value = "SDR|HDR10|HLG"; IsRequired = $false }
            ) },
            @{ Type = "Video"; Codec = "av1"; Conditions = @(
                @{ Condition = "NotEquals"; Property = "IsAnamorphic"; Value = "true"; IsRequired = $false },
                @{ Condition = "EqualsAny"; Property = "VideoProfile"; Value = "main"; IsRequired = $false },
                @{ Condition = "EqualsAny"; Property = "VideoRangeType"; Value = "SDR|HDR10|HLG"; IsRequired = $false },
                @{ Condition = "LessThanEqual"; Property = "VideoLevel"; Value = "19"; IsRequired = $false }
            ) }
        )
        SubtitleProfiles = @(
            @{ Format = "vtt"; Method = "External" },
            @{ Format = "ass"; Method = "External" },
            @{ Format = "ssa"; Method = "External" }
        )
        ResponseProfiles = @(
            @{ Type = "Video"; Container = "m4v"; MimeType = "video/mp4" }
        )
    }
}

function Get-PlaybackInfoRequestBody {
    param([string]$UserId)

    if ($ClientProfile.Trim().ToLowerInvariant() -eq "legacy") {
        return [ordered]@{
            UserId = $UserId
            IsPlayback = $true
            AutoOpenLiveStream = $true
        }
    }

    if ($Script:WebPlaybackTemplate -and $Script:WebPlaybackTemplate.Body) {
        $templateBody = Copy-DeepObject -InputObject $Script:WebPlaybackTemplate.Body
        Set-ObjectPropertyValue -Object $templateBody -Name 'UserId' -Value $UserId
        Set-ObjectPropertyValue -Object $templateBody -Name 'IsPlayback' -Value $true
        Set-ObjectPropertyValue -Object $templateBody -Name 'AutoOpenLiveStream' -Value $true
        if (-not ($templateBody.PSObject.Properties.Name -contains 'StartTimeTicks')) {
            Set-ObjectPropertyValue -Object $templateBody -Name 'StartTimeTicks' -Value 0
        }
        return $templateBody
    }

    return [ordered]@{
        UserId = $UserId
        StartTimeTicks = 0
        IsPlayback = $true
        AutoOpenLiveStream = $true
        SubtitleStreamIndex = ""
        MaxStreamingBitrate = $ClientMaxStreamingBitrate
        AlwaysBurnInSubtitleWhenTranscoding = $false
        DeviceProfile = Get-WebPlaybackDeviceProfile
    }
}

function Get-JellyfinManagedPlaybackUrl {
    param(
        [string]$JellyfinItemId,
        [object]$MediaSource,
        [string]$PlaySessionId = ""
    )

    if (-not $MediaSource) { return $null }

    $requiredHeaders = [ordered]@{}
    if ($MediaSource.PSObject.Properties.Name -contains 'RequiredHttpHeaders' -and $MediaSource.RequiredHttpHeaders) {
        foreach ($p in $MediaSource.RequiredHttpHeaders.PSObject.Properties) {
            $requiredHeaders[$p.Name] = [string]$p.Value
        }
    }

    $transcodingUrl = if ($MediaSource.PSObject.Properties.Name -contains 'TranscodingUrl') { [string]$MediaSource.TranscodingUrl } else { "" }
    $directStreamUrl = if ($MediaSource.PSObject.Properties.Name -contains 'DirectStreamUrl') { [string]$MediaSource.DirectStreamUrl } else { "" }
    $mediaSourcePath = if ($MediaSource.PSObject.Properties.Name -contains 'Path') { [string]$MediaSource.Path } else { "" }

    $candidates = @(
        @{ Value = $transcodingUrl; Type = "JellyfinTranscodingUrl" },
        @{ Value = $directStreamUrl; Type = "JellyfinDirectStreamUrl" },
        @{ Value = $mediaSourcePath; Type = "JellyfinMediaSourcePath" }
    )

    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate.Value)) { continue }
        $absoluteUrl = Convert-ToAbsoluteJellyfinUrl -UrlOrPath $candidate.Value
        if ([string]::IsNullOrWhiteSpace($absoluteUrl)) { continue }
        if (-not (Test-IsJellyfinManagedUrl -UrlOrPath $absoluteUrl)) { continue }

        return [PSCustomObject]@{
            Url = $absoluteUrl
            UrlType = [string]$candidate.Type
            RequiredHeaders = $requiredHeaders
        }
    }

    return $null
}

function Send-PlaybackHeartbeat {
    param(
        [string]$PlaySessionId,
        [string]$ItemId,
        [string]$MediaSourceId = "",
        [bool]$IsPaused = $false
    )

    if ([string]::IsNullOrWhiteSpace($PlaySessionId) -or [string]::IsNullOrWhiteSpace($ItemId)) { return }

    $body = [ordered]@{
        PlaySessionId = $PlaySessionId
        ItemId = $ItemId
        IsPaused = $IsPaused
        PositionTicks = 0
        PlayMethod = "Transcode"
        RepeatMode = "RepeatNone"
    }
    if (-not [string]::IsNullOrWhiteSpace($MediaSourceId)) {
        $body["MediaSourceId"] = $MediaSourceId
    }

    try {
        Invoke-JellyfinApi -Path "/Sessions/Playing/Progress" -Method POST -Body $body -TimeoutSec 5 | Out-Null
    } catch {
        # Heartbeat failure is non-fatal
    }
}

function Invoke-JellyfinApi {
    param([string]$Path, [string]$Method = "GET", [object]$Body = $null, [int]$TimeoutSec = 30)
    $url = "$JellyfinUrl$Path"
    if ($Script:AuthToken) {
        $sep = if ($url.Contains('?')) { '&' } else { '?' }
        $url = "$url${sep}api_key=$($Script:AuthToken)"
    }
    $headers = @{}
    foreach ($key in $Script:AuthHeader.Keys) { $headers[$key] = $Script:AuthHeader[$key] }
    $params = @{ Uri = $url; Method = $Method; Headers = $headers; ContentType = "application/json"; TimeoutSec = $TimeoutSec; UseBasicParsing = $true }
    if ($null -ne $Body) {
        $params["Body"] = if ($Body -is [string]) { $Body } else { ($Body | ConvertTo-Json -Depth 10 -Compress) }
    }
    try { return Invoke-RestMethod @params }
    catch { Write-Warn "API failed: $Method $Path - $($_.Exception.Message)"; return $null }
}

function Invoke-TvhApi {
    param([string]$Path, [string]$Method = "GET", [int]$TimeoutSec = 15)
    if (-not $Script:TvhBaseUrl) { return $null }
    $params = @{ Uri = "$($Script:TvhBaseUrl)$Path"; Method = $Method; TimeoutSec = $TimeoutSec; UseBasicParsing = $true }
    if ($Script:TvhCredential) { $params["Credential"] = $Script:TvhCredential }
    try { return Invoke-RestMethod @params }
    catch { Write-Warn "TVH API: $Path - $($_.Exception.Message)"; return $null }
}

# --- Log capture helpers ----------------------------------------------------

function Get-DockerLogTimestamp {
    return [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
}

function Get-JellyfinContainerLogs {
    param([string]$Since, [string]$Until)
    try {
        $raw = docker logs --since $Since --until $Until $CONTAINER_NAME 2>&1
        if ($raw) {
            # Use Write-Output -NoEnumerate to prevent PowerShell 5.1 from unrolling empty arrays to $null
            $arr = @($raw | ForEach-Object { $_.ToString() })
            return , $arr
        }
    } catch { }
    return , @()
}

function Expand-JellyfinLogLines {
    param([string[]]$LogLines)

    $expanded = [System.Collections.ArrayList]::new()
    # Regex matching Jellyfin log timestamp prefix: [HH:MM:SS] [LVL]
    $timestampRx = [regex]::new('\[\d{2}:\d{2}:\d{2}\]\s*\[[A-Z]{3}\]')

    foreach ($line in $LogLines) {
        $text = if ($null -eq $line) { "" } else { $line.ToString() }
        if ([string]::IsNullOrWhiteSpace($text)) { continue }

        # First split real line breaks.
        $parts = @($text -split "`r?`n")
        foreach ($part in $parts) {
            if ([string]::IsNullOrWhiteSpace($part)) { continue }

            # Some environments (Docker on Windows / PS 5.1) collapse multiline
            # docker output into one long line.  Use [regex]::Matches to find
            # every Jellyfin timestamp and extract substrings between them.
            $allMatches = $timestampRx.Matches($part)

            if ($allMatches.Count -le 1) {
                # Single log entry (or no timestamp) – keep as-is.
                $trimmed = $part.Trim()
                if (-not [string]::IsNullOrWhiteSpace($trimmed)) {
                    [void]$expanded.Add($trimmed)
                }
            } else {
                for ($mi = 0; $mi -lt $allMatches.Count; $mi++) {
                    $startIdx = $allMatches[$mi].Index
                    $endIdx   = if ($mi + 1 -lt $allMatches.Count) { $allMatches[$mi + 1].Index } else { $part.Length }
                    # Trim trailing whitespace that was the separator between entries.
                    $segment = $part.Substring($startIdx, $endIdx - $startIdx).TrimEnd()
                    if (-not [string]::IsNullOrWhiteSpace($segment)) {
                        [void]$expanded.Add($segment)
                    }
                }
                # Content before the very first timestamp (rare, e.g. docker ts prefix).
                if ($allMatches[0].Index -gt 0) {
                    $prefix = $part.Substring(0, $allMatches[0].Index).Trim()
                    if (-not [string]::IsNullOrWhiteSpace($prefix)) {
                        $expanded.Insert(0, $prefix)
                    }
                }
            }
        }
    }

    return , @($expanded)
}

function Get-TvhSubscriptions {
    $subs = Invoke-TvhApi -Path "api/status/subscriptions"
    if ($subs -and $subs.entries) { return , @($subs.entries) }
    return , @()
}

function Get-TvhInputStatus {
    $inp = Invoke-TvhApi -Path "api/status/inputs"
    if ($inp -and $inp.entries) { return , @($inp.entries) }
    return , @()
}

function Get-TvhConnections {
    $conn = Invoke-TvhApi -Path "api/status/connections"
    if ($conn -and $conn.entries) { return , @($conn.entries) }
    return , @()
}

function Get-UrlQueryParameterValue {
    param([string]$Url, [string]$Name)

    if ([string]::IsNullOrWhiteSpace($Url) -or [string]::IsNullOrWhiteSpace($Name)) { return "" }
    try {
        $query = ""
        if ($Url -match '^https?://') {
            $query = ([System.Uri]$Url).Query
        } else {
            $idx = $Url.IndexOf('?')
            if ($idx -ge 0) { $query = $Url.Substring($idx) }
        }

        if ([string]::IsNullOrWhiteSpace($query)) { return "" }
        if ($query -match ([regex]::Escape("$Name=") + '([^&]+)')) {
            return [System.Uri]::UnescapeDataString($Matches[1])
        }
    } catch { }
    return ""
}

function Select-TvhSubscriptionBestMatch {
    param(
        [array]$Subscriptions,
        [string]$ChannelName,
        [string]$ProfileHint = ""
    )

    if (-not $Subscriptions -or $Subscriptions.Count -eq 0) { return $null }

    $best = $null
    $bestScore = -1
    foreach ($sub in $Subscriptions) {
        $score = 0
        $subChannel = if ($sub.PSObject.Properties.Name -contains 'channel') { [string]$sub.channel } else { "" }
        $subService = if ($sub.PSObject.Properties.Name -contains 'service') { [string]$sub.service } else { "" }
        $subProfile = if ($sub.PSObject.Properties.Name -contains 'profile') { [string]$sub.profile } else { "" }
        $subClient = if ($sub.PSObject.Properties.Name -contains 'client') { [string]$sub.client } else { "" }

        if (-not [string]::IsNullOrWhiteSpace($ChannelName)) {
            if ($subChannel -eq $ChannelName) { $score += 8 }
            elseif ($subChannel -like "*$ChannelName*") { $score += 5 }
            if ($subService -like "*$ChannelName*") { $score += 3 }
        }

        if (-not [string]::IsNullOrWhiteSpace($ProfileHint) -and $subProfile -eq $ProfileHint) { $score += 4 }
        if ($subClient -match '(?i)(lavf|ffmpeg)') { $score += 2 }
        if ($sub.PSObject.Properties.Name -contains 'in' -and [double]$sub.'in' -gt 0) { $score += 1 }

        if ($score -gt $bestScore) {
            $bestScore = $score
            $best = $sub
        }
    }

    return $best
}

function Set-ResultFromTvhSubscription {
    param(
        # Do NOT type-constrain to [hashtable] – the caller passes an
        # OrderedDictionary and PS 5.1 would silently copy it, losing changes.
        $Result,
        [AllowNull()][object]$Subscription,
        [string]$ProfileFallback = ""
    )

    if ($null -eq $Result) { return }

    function Get-ResultValue {
        param([string]$Name)
        if ($Result -is [System.Collections.IDictionary]) { return [string]$Result[$Name] }
        $prop = $Result.PSObject.Properties[$Name]
        if ($prop) { return [string]$prop.Value }
        return ""
    }

    function Set-ResultValueIfNonEmptyOrMissing {
        param([string]$Name, [AllowNull()][object]$Value)

        $newVal = if ($null -eq $Value) { "" } else { [string]$Value }
        $existingVal = Get-ResultValue -Name $Name

        if ([string]::IsNullOrWhiteSpace($newVal) -and -not [string]::IsNullOrWhiteSpace($existingVal)) {
            return
        }

        if ($Result -is [System.Collections.IDictionary]) {
            $Result[$Name] = $newVal
        } else {
            $prop = $Result.PSObject.Properties[$Name]
            if ($prop) { $prop.Value = $newVal } else { $Result | Add-Member -NotePropertyName $Name -NotePropertyValue $newVal -Force }
        }
    }

    if ($Subscription) {
        $subProfile = if ($Subscription.PSObject.Properties.Name -contains 'profile') { [string]$Subscription.profile } else { "" }
        $subService = if ($Subscription.PSObject.Properties.Name -contains 'service') { [string]$Subscription.service } else { "" }
        $subChannel = if ($Subscription.PSObject.Properties.Name -contains 'channel') { [string]$Subscription.channel } else { "" }
        $subTitle = if ($Subscription.PSObject.Properties.Name -contains 'title') { [string]$Subscription.title } else { "" }

        Set-ResultValueIfNonEmptyOrMissing -Name 'TvhProfile' -Value $subProfile
        if (-not [string]::IsNullOrWhiteSpace($subService)) {
            Set-ResultValueIfNonEmptyOrMissing -Name 'TvhService' -Value $subService
        } elseif (-not [string]::IsNullOrWhiteSpace($subChannel)) {
            Set-ResultValueIfNonEmptyOrMissing -Name 'TvhService' -Value $subChannel
        } else {
            Set-ResultValueIfNonEmptyOrMissing -Name 'TvhService' -Value $subTitle
        }

        if ($Subscription.PSObject.Properties.Name -contains 'in') { Set-ResultValueIfNonEmptyOrMissing -Name 'TvhInRate' -Value "$($Subscription.'in')" }
        if ($Subscription.PSObject.Properties.Name -contains 'out') { Set-ResultValueIfNonEmptyOrMissing -Name 'TvhOutRate' -Value "$($Subscription.'out')" }
        if ($Subscription.PSObject.Properties.Name -contains 'errors') {
            if ($Result -is [System.Collections.IDictionary]) { $Result['TvhErrors'] = $Subscription.errors }
            else {
                $propErr = $Result.PSObject.Properties['TvhErrors']
                if ($propErr) { $propErr.Value = $Subscription.errors } else { $Result | Add-Member -NotePropertyName 'TvhErrors' -NotePropertyValue $Subscription.errors -Force }
            }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($ProfileFallback)) {
        Set-ResultValueIfNonEmptyOrMissing -Name 'TvhProfile' -Value $ProfileFallback
    }
}

function Parse-JellyfinLogs {
    param([string[]]$LogLines, [string]$ChannelId)

    $parsed = [ordered]@{
        ProbingDetected      = $false
        ProbingRequested     = $false
        ProbingDisabled      = $false
        FfmpegLaunched       = $false
        FfmpegArgs           = ""
        StreamOpened         = $false
        StreamOpenedAfterMs  = ""
        SupportsProbing      = ""
        AnalyzeDurationMs    = ""
        Container            = ""
        HasStreamDetails     = $false
        DetailedMediaSource  = $false
        TranscodingUrl       = ""
        DirectPlayDecision   = ""
        # Transformation tracking
        FfmpegInputCodecVideo  = ""
        FfmpegInputCodecAudio  = ""
        FfmpegOutputCodecVideo = ""
        FfmpegOutputCodecAudio = ""
        FfmpegInputContainer   = ""
        FfmpegOutputContainer  = ""
        JellyfinTranscodeReasons = ""
        Errors               = [System.Collections.ArrayList]::new()
        Warnings             = [System.Collections.ArrayList]::new()
        RelevantLines        = [System.Collections.ArrayList]::new()
        AllLines             = [System.Collections.ArrayList]::new()
    }

    foreach ($line in $LogLines) {
        $lineStr = $line.ToString().Trim()
        if ([string]::IsNullOrWhiteSpace($lineStr)) { continue }
        [void]$parsed.AllLines.Add($lineStr)

        # Filter to lines relevant to this channel or general pipeline events
        $isRelevant = $false

        # Channel-specific
        if ($ChannelId -and $lineStr -match [regex]::Escape($ChannelId)) { $isRelevant = $true }

        # General pipeline keywords
        if ($lineStr -match '(?i)(probe|ffmpeg|transcode|MediaSourceManager|LiveTvMediaSourceProvider|stream opened|stream url|Built detailed|Fast Channel|No TVH service|PlaybackInfo|MediaEncoder|ProcessId|Starting\s+FFmpeg)') {
            $isRelevant = $true
        }

        # Error/warning lines
        if ($lineStr -match '\[(ERR|WRN)\]') { $isRelevant = $true }

        if (-not $isRelevant) { continue }

        [void]$parsed.RelevantLines.Add($lineStr)

        # --- Parse specific events ---

        # Stream opened timing
        if ($lineStr -match 'Live stream opened after ([\d.]+)ms') {
            $parsed.StreamOpened = $true
            $parsed.StreamOpenedAfterMs = $Matches[1]
        }

        # Built detailed MediaSourceInfo (probing disabled)
        if ($lineStr -match 'Built detailed MediaSourceInfo') {
            $parsed.DetailedMediaSource = $true
            $parsed.HasStreamDetails = $true
        }

        # SupportsProbing from MediaSourceInfo JSON
        if ($lineStr -match '"SupportsProbing":\s*(true|false)') {
            $parsed.SupportsProbing = $Matches[1]
            if ($Matches[1] -eq "true") { $parsed.ProbingRequested = $true }
            else { $parsed.ProbingDisabled = $true }
        }

        # AnalyzeDurationMs from JSON
        if ($lineStr -match '"AnalyzeDurationMs":\s*(\d+)') {
            $parsed.AnalyzeDurationMs = $Matches[1]
        }

        # Container from JSON
        if ($lineStr -match '"Container":\s*"([^"]+)"') {
            $parsed.Container = $Matches[1]
        }

        # TranscodingUrl
        if ($lineStr -match '"TranscodingUrl":\s*"([^"]+)"') {
            $parsed.TranscodingUrl = $Matches[1]
        }

        # Probing actually happening (Jellyfin core logs)
        if ($lineStr -match '(?i)(Probing\s+stream|Running\s+media\s+probe|ffprobe|MediaEncoder.*probe)') {
            $parsed.ProbingDetected = $true
        }

        # FFmpeg launch
        if ($lineStr -match '(?i)(Starting\s+FFmpeg|ffmpeg.*-i\s|ProcessId.*ffmpeg)') {
            $parsed.FfmpegLaunched = $true
        }

        # FFmpeg arguments
        if ($lineStr -match '(?i)ffmpeg\s+(.+-i\s.+)') {
            $parsed.FfmpegArgs = $Matches[1]

            # Extract input container from -f before -i
            if ($Matches[1] -match '-f\s+(\S+)\s.*-i\s') { $parsed.FfmpegInputContainer = $Matches[1] }
            # Extract output container from -f after the last -i
            $afterLastI = ($Matches[1] -split '-i\s+\S+')[-1]
            if ($afterLastI -match '-f\s+(\S+)') { $parsed.FfmpegOutputContainer = $Matches[1] }
            # Extract video codec: -c:v or -vcodec
            if ($afterLastI -match '-(?:c:v|vcodec)\s+(\S+)') { $parsed.FfmpegOutputCodecVideo = $Matches[1] }
            # Extract audio codec: -c:a or -acodec
            if ($afterLastI -match '-(?:c:a|acodec)\s+(\S+)') { $parsed.FfmpegOutputCodecAudio = $Matches[1] }
        }

        # FFmpeg stream mapping lines: Input #0 / Stream #0:0: Video: h264 ...
        if ($lineStr -match 'Stream\s+#\d+:\d+.*?:\s+Video:\s+(\w+)' -and -not $parsed.FfmpegInputCodecVideo) {
            $parsed.FfmpegInputCodecVideo = $Matches[1]
        }
        if ($lineStr -match 'Stream\s+#\d+:\d+.*?:\s+Audio:\s+(\w+)' -and -not $parsed.FfmpegInputCodecAudio) {
            $parsed.FfmpegInputCodecAudio = $Matches[1]
        }

        # Transcode reasons from Jellyfin logs
        if ($lineStr -match '(?i)TranscodeReason[s]?["\s:=]+([^"}\]]+)') {
            $parsed.JellyfinTranscodeReasons = $Matches[1].Trim()
        }

        # Probing disabled info from plugin
        if ($lineStr -match 'Probing disabled per config') {
            $parsed.ProbingDisabled = $true
        }

        # Jellyfin will probe
        if ($lineStr -match 'Jellyfin will probe') {
            $parsed.ProbingRequested = $true
        }

        # No stream details / fallback
        if ($lineStr -match 'No TVH service data|Using Fast Channel Switching|No stream details') {
            $parsed.HasStreamDetails = $false
        }

        # Errors
        if ($lineStr -match '\[ERR\]') {
            [void]$parsed.Errors.Add($lineStr)
        }

        # Warnings
        if ($lineStr -match '\[WRN\]') {
            [void]$parsed.Warnings.Add($lineStr)
        }
    }

    return $parsed
}

# --- Config management ------------------------------------------------------

function Save-OriginalConfig {
    $config = Invoke-JellyfinApi -Path "/Plugins/$PLUGIN_GUID/Configuration"
    if ($config) {
        $Script:OriginalConfig = $config
        Write-OK "Original config saved."
        return $true
    }
    Write-Err "Cannot read plugin config."
    return $false
}

function Restore-OriginalConfig {
    if (-not $Script:OriginalConfig) { Write-Warn "No original config to restore."; return }
    Write-Info "Restoring original plugin config..."
    $body = $Script:OriginalConfig | ConvertTo-Json -Depth 10 -Compress
    $url = "$JellyfinUrl/Plugins/$PLUGIN_GUID/Configuration"
    if ($Script:AuthToken) { $url = "$url`?api_key=$($Script:AuthToken)" }
    try {
        Invoke-RestMethod -Uri $url -Method POST -ContentType "application/json" -Body $body -UseBasicParsing | Out-Null
        $Script:PluginConfig = $Script:OriginalConfig
        Write-OK "Original config restored."
    } catch { Write-Err "Failed to restore config: $($_.Exception.Message)" }
}

function Set-ScenarioConfig {
    param([string]$ScenarioKey, [string]$StreamingProfile)
    $scenario = $Script:ScenarioDefinitions[$ScenarioKey]
    if (-not $scenario) { Write-Err "Unknown scenario: $ScenarioKey"; return $false }

    # Read current config
    $config = Invoke-JellyfinApi -Path "/Plugins/$PLUGIN_GUID/Configuration"
    if (-not $config) { Write-Err "Cannot read current config."; return $false }

    # Apply scenario overrides
    $config.SupportsDirectPlay   = $scenario.SupportsDirectPlay
    $config.SupportsDirectStream = $scenario.SupportsDirectStream
    $config.SupportsTranscoding  = $scenario.SupportsTranscoding
    $config.SupportsProbing      = $scenario.SupportsProbing
    if (-not [string]::IsNullOrWhiteSpace($StreamingProfile)) { $config.StreamingProfile = $StreamingProfile }
    $config.AnalyzeDurationMs = [int]$scenario.AnalyzeDurationMs
    if ($scenario.Contains('IgnoreDts')) { $config.IgnoreDts = [bool]$scenario.IgnoreDts }
    if ($scenario.Contains('BufferMs')) { $config.BufferMs = [int]$scenario.BufferMs }

    # Save config via API
    $body = $config | ConvertTo-Json -Depth 10 -Compress
    $url = "$JellyfinUrl/Plugins/$PLUGIN_GUID/Configuration"
    if ($Script:AuthToken) { $url = "$url`?api_key=$($Script:AuthToken)" }
    try {
        Invoke-RestMethod -Uri $url -Method POST -ContentType "application/json" -Body $body -UseBasicParsing | Out-Null
        $Script:PluginConfig = $config
        Write-OK "Config applied: $($scenario.Label)"
        Write-Info "  Profile=$($config.StreamingProfile) | DirectPlay=$($config.SupportsDirectPlay) | DirectStream=$($config.SupportsDirectStream) | Transcoding=$($config.SupportsTranscoding) | Probing=$($config.SupportsProbing) | IgnoreDts=$($config.IgnoreDts) | BufferMs=$($config.BufferMs) | AnalyzeMs=$($config.AnalyzeDurationMs)"
        Start-Sleep -Seconds 2
        return $true
    } catch {
        Write-Err "Failed to apply scenario config: $($_.Exception.Message)"
        return $false
    }
}

function Export-MatrixReport {
    param([hashtable]$AllResults, [array]$RunConfigs, [array]$Channels = @())

    Write-Step "PHASE 7: Generating Report"
    $sb = [System.Text.StringBuilder]::new()

    [void]$sb.AppendLine("# Jellyfin LiveTV Pipeline Analysis")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("| Property | Value |")
    [void]$sb.AppendLine("|----------|-------|")
    [void]$sb.AppendLine("| Date | $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') |")
    [void]$sb.AppendLine("| Script Version | $SCRIPT_VERSION |")
    [void]$sb.AppendLine("| Channels per run | $MaxChannels |")
    [void]$sb.AppendLine("| Stream hold time | ${StreamHoldSec}s |")
    [void]$sb.AppendLine("| Test runs | $($RunConfigs.Count) |")
    [void]$sb.AppendLine("| Profiles tested | $(($RunConfigs | Select-Object -ExpandProperty Profile -Unique) -join ', ') |")
    [void]$sb.AppendLine("| Client profile | $ClientProfile |")
    [void]$sb.AppendLine("| Client identity | $ClientName / $ClientDevice / $ClientVersion |")
    [void]$sb.AppendLine("| Client device id | $ClientDeviceId |")
    [void]$sb.AppendLine("| Artifact root | $ArtifactRoot |")
    [void]$sb.AppendLine("")

    $planChannels = @()
    if ($Channels -and $Channels.Count -gt 0) {
        $planChannels = @($Channels)
    } elseif ($RunConfigs -and $RunConfigs.Count -gt 0) {
        $firstRunResults = @($AllResults[$RunConfigs[0].Key])
        foreach ($r in $firstRunResults) {
            $planChannels += [PSCustomObject]@{
                ChannelNumber = $r.ChannelNumber
                Name = $r.ChannelName
                Id = $r.ChannelId
            }
        }
    }

    $planChannelCount = $planChannels.Count
    $planRunCount = if ($RunConfigs) { $RunConfigs.Count } else { 0 }
    $planTotalChannelTests = $planChannelCount * $planRunCount
    $secondsPerChannel = $StreamHoldSec + 2
    $secondsPerRun = ($planChannelCount * $secondsPerChannel) + ([Math]::Max(0, $planChannelCount - 1) * $PauseBetweenTestsSec)
    $estimatedSeconds = ($planRunCount * $secondsPerRun) + ([Math]::Max(0, $planRunCount - 1) * 5)
    $estimatedDuration = [TimeSpan]::FromSeconds([Math]::Max(0, [math]::Round($estimatedSeconds, 0)))

    [void]$sb.AppendLine("## Test Plan")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("| Property | Value |")
    [void]$sb.AppendLine("|----------|-------|")
    [void]$sb.AppendLine("| Report output | $ReportPath |")
    [void]$sb.AppendLine("| Artifact root | $ArtifactRoot |")
    [void]$sb.AppendLine("| Channel tests total | $planTotalChannelTests ($planChannelCount channels x $planRunCount runs) |")
    [void]$sb.AppendLine("| Hold/Pause | hold=${StreamHoldSec}s, between channels=${PauseBetweenTestsSec}s, between runs=5s |")
    [void]$sb.AppendLine("| Estimated minimum runtime | $($estimatedDuration.ToString()) (without build/auth/setup) |")
    [void]$sb.AppendLine("")

    [void]$sb.AppendLine("### Planned Runs")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("| Run | Profile | Scenario | Mode | Artifacts |")
    [void]$sb.AppendLine("|-----|---------|----------|------|-----------|")
    foreach ($run in $RunConfigs) {
        $profileToApply = if ($run.Profile -eq "current") { "(original)" } else { $run.Profile }
        $scenarioMode = Get-ScenarioModeDescription -ScenarioKey $run.ScenarioKey
        $runArtifactRoot = Join-Path $ArtifactRoot (("{0}_{1}_{2}" -f $run.Label.Replace(' ', ''), (Convert-ToSafeName $run.Profile), (Convert-ToSafeName $run.ScenarioKey)))
        [void]$sb.AppendLine("| $($run.Label) | $profileToApply | $($run.ScenarioDisplay) | $scenarioMode | $runArtifactRoot |")
    }
    [void]$sb.AppendLine("")

    [void]$sb.AppendLine("### Planned Channels")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("| # | Channel | TVH Id |")
    [void]$sb.AppendLine("|---|---------|--------|")
    $maxChannelRows = [Math]::Min($planChannelCount, 20)
    for ($i = 0; $i -lt $maxChannelRows; $i++) {
        $ch = $planChannels[$i]
        [void]$sb.AppendLine("| $($ch.ChannelNumber) | $($ch.Name) | $($ch.Id) |")
    }
    if ($planChannelCount -gt $maxChannelRows) {
        [void]$sb.AppendLine("| ... | ... | +$($planChannelCount - $maxChannelRows) weitere Kanaele |")
    }
    [void]$sb.AppendLine("")

    [void]$sb.AppendLine("## Run Comparison")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("| Run | Profile | Scenario | Success | Avg PBI (ms) | Avg TTFB (ms) | Probing Active | FFmpeg | TVH bypass |")
    [void]$sb.AppendLine("|-----|---------|----------|---------|--------------|---------------|----------------|--------|------------|")

    $ranking = [System.Collections.ArrayList]::new()
    foreach ($rc in $RunConfigs) {
        $results = @($AllResults[$rc.Key])
        $ok = @($results | Where-Object { $_.Success })
        $pbi = @($ok | Where-Object { $_.PlaybackInfoMs -ge 0 } | ForEach-Object { $_.PlaybackInfoMs })
        $ttfb = @($ok | Where-Object { $_.StreamOpenMs -ge 0 } | ForEach-Object { $_.StreamOpenMs })
        $avgPbi = if ($pbi.Count -gt 0) { [math]::Round(($pbi | Measure-Object -Average).Average, 1) } else { "N/A" }
        $avgTtfb = if ($ttfb.Count -gt 0) { [math]::Round(($ttfb | Measure-Object -Average).Average, 1) } else { "N/A" }
        $probingCount = @($results | Where-Object { $_.ProbingDetected }).Count
        $ffmpegCount = @($results | Where-Object { $_.FfmpegLaunched }).Count
        $bypassCount = @($results | Where-Object { $_.BypassedJellyfinPipeline }).Count

        [void]$sb.AppendLine("| $($rc.Label) | $($rc.Profile) | $($rc.ScenarioDisplay) | $($ok.Count)/$($results.Count) | $avgPbi | $avgTtfb | $probingCount/$($results.Count) | $ffmpegCount/$($results.Count) | $bypassCount/$($results.Count) |")

        # Score: favor success and low TTFB/PBI, penalize failures
        $successRate = if ($results.Count -gt 0) { [double]$ok.Count / [double]$results.Count } else { 0.0 }
        $ttfbScore = if ($avgTtfb -is [double] -or $avgTtfb -is [int] -or $avgTtfb -is [decimal]) { [math]::Max(0, 2000 - [double]$avgTtfb) } else { 0 }
        $pbiScore = if ($avgPbi -is [double] -or $avgPbi -is [int] -or $avgPbi -is [decimal]) { [math]::Max(0, 1000 - [double]$avgPbi) } else { 0 }
        $score = ($successRate * 10000) + $ttfbScore + $pbiScore - (($results.Count - $ok.Count) * 500)
        [void]$ranking.Add([PSCustomObject]@{ Key = $rc.Key; Label = $rc.Label; Profile = $rc.Profile; Scenario = $rc.ScenarioDisplay; Score = [math]::Round($score, 1); Success = "$($ok.Count)/$($results.Count)"; AvgTtfb = $avgTtfb; AvgPbi = $avgPbi })
    }
    [void]$sb.AppendLine("")

    foreach ($rc in $RunConfigs) {
        $results = @($AllResults[$rc.Key])
        [void]$sb.AppendLine("## Run: $($rc.Label)")
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("> Profile: **$($rc.Profile)** | Scenario: **$($rc.ScenarioDisplay)**")
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("| # | Channel | PBI (ms) | TTFB (ms) | Decision | URL Type | Data (MB) | Bitrate (kbps) | Probing | FFmpeg | Status |")
        [void]$sb.AppendLine("|---|---------|----------|-----------|----------|----------|-----------|----------------|---------|--------|--------|")
        foreach ($r in $results) {
            $st   = if ($r.Success) { "OK" } else { "FAIL" }
            $n    = ($r.ChannelName) -replace [regex]::Escape('|'), '/'
            $p    = if ($r.PlaybackInfoMs -ge 0) { "$($r.PlaybackInfoMs)" } else { "-" }
            $t    = if ($r.StreamOpenMs -ge 0) { "$($r.StreamOpenMs)" } else { "-" }
            $d    = if ($r.TranscodeDecision) { $r.TranscodeDecision } else { "-" }
            $u    = if ($r.PlaybackUrlType) { $r.PlaybackUrlType } else { "-" }
            $mb   = if ($r.BytesReceived -gt 0) { [math]::Round($r.BytesReceived / 1MB, 1) } else { "-" }
            $b    = if ($r.BitrateKbps -gt 0) { "$($r.BitrateKbps)" } else { "-" }
            $prb  = if ($r.ProbingDetected) { "ACTIVE" } elseif ($r.ProbingRequested) { "requested" } elseif ($r.ProbingDisabled) { "disabled" } else { "no" }
            $ff   = if ($r.FfmpegLaunched) { "YES" } else { "no" }
            [void]$sb.AppendLine("| $($r.ChannelNumber) | $n | $p | $t | $d | $u | $mb | $b | $prb | $ff | $st |")
        }
        [void]$sb.AppendLine("")

        [void]$sb.AppendLine("### Hinweise zu diesem Run")
        [void]$sb.AppendLine("")
        $bypassInRun = @($results | Where-Object { $_.BypassedJellyfinPipeline }).Count
        if ($bypassInRun -gt 0) {
            [void]$sb.AppendLine("- In diesem Run wurden mindestens einige Streams **direkt ueber TVHeadend** geoeffnet. Dann taucht FFmpeg in Jellyfin-Logs erwartbar **nicht** auf.")
        } else {
            [void]$sb.AppendLine("- In diesem Run wurde der Stream nicht direkt ueber TVHeadend klassifiziert.")
        }
        [void]$sb.AppendLine("")
    }

    # --- Per-run channel details with logs and transformation pipeline ---
    foreach ($rc in $RunConfigs) {
        $results = @($AllResults[$rc.Key])
        [void]$sb.AppendLine("## Channel Details: $($rc.Label) ($($rc.Profile) / $($rc.ScenarioDisplay))")
        [void]$sb.AppendLine("")

        foreach ($r in $results) {
            $chLabel = "[$($r.ChannelNumber)] $($r.ChannelName)"

            [void]$sb.AppendLine("<details>")
            [void]$sb.AppendLine("<summary><b>$chLabel</b> — $(if ($r.Success) { 'OK' } else { 'FAIL' }) | TTFB: $($r.StreamOpenMs)ms | $($r.TranscodeDecision)</summary>")
            [void]$sb.AppendLine("")

            [void]$sb.AppendLine("| Property | Value |")
            [void]$sb.AppendLine("|----------|-------|")
            [void]$sb.AppendLine("| PlaybackInfo latency | $($r.PlaybackInfoMs) ms |")
            [void]$sb.AppendLine("| Time to first byte | $($r.StreamOpenMs) ms |")
            [void]$sb.AppendLine("| Total test duration | $($r.TotalMs) ms |")
            [void]$sb.AppendLine("| Data received | $([math]::Round($r.BytesReceived / 1MB, 2)) MB |")
            [void]$sb.AppendLine("| Bitrate | $($r.BitrateKbps) kbps |")
            [void]$sb.AppendLine("| Container | $($r.Container) |")
            [void]$sb.AppendLine("| Media Streams | $($r.MediaStreams) |")
            [void]$sb.AppendLine("| Decision | $($r.TranscodeDecision) |")
            [void]$sb.AppendLine("| Playback URL Type | $($r.PlaybackUrlType) |")
            [void]$sb.AppendLine("| Bypassed Jellyfin pipeline | $($r.BypassedJellyfinPipeline) |")
            [void]$sb.AppendLine("| SupportsProbing (API) | $($r.SupportsProbing) |")
            [void]$sb.AppendLine("| AnalyzeDurationMs (API) | $($r.AnalyzeDurationMs) |")
            [void]$sb.AppendLine("| Probing detected | $($r.ProbingDetected) |")
            [void]$sb.AppendLine("| FFmpeg launched | $($r.FfmpegLaunched) |")
            [void]$sb.AppendLine("| Detailed MediaSource | $($r.DetailedMediaSource) |")
            [void]$sb.AppendLine("| TVH subs | $($r.TvhSubscriptionCount) |")
            [void]$sb.AppendLine("| TVH profile | $($r.TvhProfile) |")
            [void]$sb.AppendLine("| TVH service | $($r.TvhService) |")
            [void]$sb.AppendLine("| TVH errors | $($r.TvhErrors) |")
            [void]$sb.AppendLine("")

            # ── Transformation Pipeline ──────────────────────────────────
            $tvhTranscoding = ($r.TvhProfile -and $r.TvhProfile -ne 'pass' -and $r.TvhProfile -ne '(Default profile)')
            $jellyfinTranscoding = $r.FfmpegLaunched
            $ffInV  = if ($r.FfmpegInputCodecVideo)  { $r.FfmpegInputCodecVideo }  else { "?" }
            $ffInA  = if ($r.FfmpegInputCodecAudio)  { $r.FfmpegInputCodecAudio }  else { "?" }
            $ffOutV = if ($r.FfmpegOutputCodecVideo)  { $r.FfmpegOutputCodecVideo }  else { "copy" }
            $ffOutA = if ($r.FfmpegOutputCodecAudio)  { $r.FfmpegOutputCodecAudio }  else { "copy" }
            $ffInC  = if ($r.FfmpegInputContainer)  { $r.FfmpegInputContainer }  else { $r.Container }
            $ffOutC = if ($r.FfmpegOutputContainer)  { $r.FfmpegOutputContainer }  else { "?" }

            [void]$sb.AppendLine("#### Transformation Pipeline")
            [void]$sb.AppendLine("")
            [void]$sb.AppendLine('```')

            if ($tvhTranscoding) {
                [void]$sb.AppendLine("DVB Broadcast")
                [void]$sb.AppendLine("  |")
                [void]$sb.AppendLine("  v")
                [void]$sb.AppendLine("[TVHeadend] profile=$($r.TvhProfile) service=$($r.TvhService)")
                [void]$sb.AppendLine("  | TVH transcodes/remuxes to: $($r.Container)")
                [void]$sb.AppendLine("  v")
            } else {
                [void]$sb.AppendLine("DVB Broadcast")
                [void]$sb.AppendLine("  |")
                [void]$sb.AppendLine("  v")
                [void]$sb.AppendLine("[TVHeadend] profile=$($r.TvhProfile) (passthrough)")
                [void]$sb.AppendLine("  | raw stream ($($r.Container))")
                [void]$sb.AppendLine("  v")
            }

            if ($jellyfinTranscoding) {
                $vChange = if ($ffOutV -eq 'copy') { "$ffInV (copy)" } else { "$ffInV -> $ffOutV" }
                $aChange = if ($ffOutA -eq 'copy') { "$ffInA (copy)" } else { "$ffInA -> $ffOutA" }
                [void]$sb.AppendLine("[Jellyfin FFmpeg] $($r.TranscodeDecision)")
                [void]$sb.AppendLine("  | Video: $vChange")
                [void]$sb.AppendLine("  | Audio: $aChange")
                [void]$sb.AppendLine("  | Container: $ffInC -> $ffOutC")
                if ($r.JellyfinTranscodeReasons) {
                    [void]$sb.AppendLine("  | Reasons: $($r.JellyfinTranscodeReasons)")
                }
                [void]$sb.AppendLine("  v")
                [void]$sb.AppendLine("[Client]")
            } else {
                [void]$sb.AppendLine("[Jellyfin] $($r.TranscodeDecision) (no FFmpeg)")
                [void]$sb.AppendLine("  |")
                [void]$sb.AppendLine("  v")
                [void]$sb.AppendLine("[Client]")
            }

            [void]$sb.AppendLine('```')
            [void]$sb.AppendLine("")

            if ($r.FfmpegArgs) {
                [void]$sb.AppendLine("**FFmpeg Arguments:** ``$($r.FfmpegArgs)``")
                [void]$sb.AppendLine("")
            }

            if ($r.JellyfinLogErrors -and $r.JellyfinLogErrors.Count -gt 0) {
                [void]$sb.AppendLine("**Errors:** $($r.JellyfinLogErrors.Count)")
                foreach ($e in $r.JellyfinLogErrors | Select-Object -First 10) {
                    $eSafe = $e -replace [regex]::Escape('|'), '/'
                    [void]$sb.AppendLine("- ``$eSafe``")
                }
                [void]$sb.AppendLine("")
            }

            if ($r.JellyfinRelevantLogs -and $r.JellyfinRelevantLogs.Count -gt 0) {
                [void]$sb.AppendLine("<details>")
                [void]$sb.AppendLine("<summary>Jellyfin Relevant Logs ($($r.JellyfinRelevantLogs.Count) lines)</summary>")
                [void]$sb.AppendLine("")
                [void]$sb.AppendLine('```')
                foreach ($logLine in $r.JellyfinRelevantLogs) {
                    $safe = $logLine -replace 'root:admin', '***:***' -replace 'http://[^@]+@', 'http://***@'
                    [void]$sb.AppendLine($safe)
                }
                [void]$sb.AppendLine('```')
                [void]$sb.AppendLine("")
                [void]$sb.AppendLine("</details>")
                [void]$sb.AppendLine("")
            }

            if ($r.JellyfinAllLogs -and $r.JellyfinAllLogs.Count -gt 0) {
                [void]$sb.AppendLine("<details>")
                [void]$sb.AppendLine("<summary>Jellyfin Full Log ($($r.JellyfinAllLogs.Count) lines)</summary>")
                [void]$sb.AppendLine("")
                [void]$sb.AppendLine('```')
                foreach ($logLine in $r.JellyfinAllLogs) {
                    $safe = $logLine -replace 'root:admin', '***:***' -replace 'http://[^@]+@', 'http://***@'
                    [void]$sb.AppendLine($safe)
                }
                [void]$sb.AppendLine('```')
                [void]$sb.AppendLine("")
                [void]$sb.AppendLine("</details>")
                [void]$sb.AppendLine("")
            }

            if ($r.TvhSubscriptions -and $r.TvhSubscriptions.Count -gt 0) {
                [void]$sb.AppendLine("<details>")
                [void]$sb.AppendLine("<summary>TVH Subscriptions ($($r.TvhSubscriptions.Count))</summary>")
                [void]$sb.AppendLine("")
                [void]$sb.AppendLine('```json')
                try { [void]$sb.AppendLine(($r.TvhSubscriptions | ConvertTo-Json -Depth 5 -Compress:$false)) } catch { [void]$sb.AppendLine("(serialization error)") }
                [void]$sb.AppendLine('```')
                [void]$sb.AppendLine("")
                [void]$sb.AppendLine("</details>")
                [void]$sb.AppendLine("")
            }

            if ($r.TvhInputs -and $r.TvhInputs.Count -gt 0) {
                [void]$sb.AppendLine("<details>")
                [void]$sb.AppendLine("<summary>TVH Inputs ($($r.TvhInputs.Count))</summary>")
                [void]$sb.AppendLine("")
                [void]$sb.AppendLine('```json')
                try { [void]$sb.AppendLine(($r.TvhInputs | ConvertTo-Json -Depth 5 -Compress:$false)) } catch { [void]$sb.AppendLine("(serialization error)") }
                [void]$sb.AppendLine('```')
                [void]$sb.AppendLine("")
                [void]$sb.AppendLine("</details>")
                [void]$sb.AppendLine("")
            }

            if ($r.TvhConnections -and $r.TvhConnections.Count -gt 0) {
                [void]$sb.AppendLine("<details>")
                [void]$sb.AppendLine("<summary>TVH Connections ($($r.TvhConnections.Count))</summary>")
                [void]$sb.AppendLine("")
                [void]$sb.AppendLine('```json')
                try { [void]$sb.AppendLine(($r.TvhConnections | ConvertTo-Json -Depth 5 -Compress:$false)) } catch { [void]$sb.AppendLine("(serialization error)") }
                [void]$sb.AppendLine('```')
                [void]$sb.AppendLine("")
                [void]$sb.AppendLine("</details>")
                [void]$sb.AppendLine("")
            }

            [void]$sb.AppendLine("</details>")
            [void]$sb.AppendLine("")
        }
    }

    $best = $ranking | Sort-Object -Property Score -Descending | Select-Object -First 1
    $stable = $ranking | Where-Object { $_.Success -notmatch '^0/' } | Sort-Object -Property Score -Descending | Select-Object -First 1
    $anySuccess = @($ranking | Where-Object { $_.Success -notmatch '^0/' }).Count -gt 0

    [void]$sb.AppendLine("## Empfehlungen")
    [void]$sb.AppendLine("")
    if ($best -and $anySuccess) {
        [void]$sb.AppendLine("- **Beste Gesamtkonfiguration:** Profile=$($best.Profile) + $($best.Scenario) (Score: $($best.Score), Success: $($best.Success), Avg TTFB: $($best.AvgTtfb) ms).")
    }
    if ($stable) {
        [void]$sb.AppendLine("- **Empfehlung fuer stabilen Betrieb:** Profile=$($stable.Profile) + $($stable.Scenario).")
    }
    if (-not $anySuccess) {
        [void]$sb.AppendLine("- **Keine belastbare Empfehlung moeglich:** Alle Jellyfin-only-Runs sind fehlgeschlagen. Die aktuellen Stream-Endpunkte liefern HTML/Auth-Antworten statt Media-Daten.")
        [void]$sb.AppendLine("- **Naechster technischer Schritt:** den echten von Jellyfin-Web/App genutzten Live-TV-Request (inkl. aller Query-Parameter/Header) mitschneiden und exakt nachbauen.")
    }
    [void]$sb.AppendLine("- **Hinweis:** Wenn weiterhin `No TVH service data available` in den Logs steht, bringt Probing/Transcoding meist wenig. Dann zuerst die TVH-Service-Zuordnung im Plugin pruefen.")
    [void]$sb.AppendLine("- **Profilwahl:** `pass` ist oft robuster fuer Rohstreaming; `jellyfin` kann Vorteile bringen, wenn TVH-Profil bereits client-kompatibel optimiert ist.")
    [void]$sb.AppendLine("")

    $dir = Split-Path -Parent $ReportPath
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Set-Content -Path $ReportPath -Value $sb.ToString() -Encoding UTF8
    Write-OK "Report: $ReportPath"
}

# --- Phase 1: Build --------------------------------------------------------

function Invoke-BuildAndDeploy {
    Write-Step "PHASE 1: Build & Deploy"
    if ($SkipBuild) { Write-Info "Skipped (-SkipBuild)."; return }
    $buildScript = Join-Path $PSScriptRoot "dev-build.ps1"
    if (-not (Test-Path $buildScript)) { Write-Err "Build script not found: $buildScript"; return }
    Write-Info "Running dev-build.ps1 ..."
    & $buildScript -NoLogs
    Write-OK "Build and deploy complete."
}

# --- Phase 2: Wait ---------------------------------------------------------

function Wait-JellyfinReady {
    Write-Step "PHASE 2: Waiting for Jellyfin"
    $maxWait = 120; $elapsed = 0
    while ($elapsed -lt $maxWait) {
        try {
            $h = Invoke-RestMethod -Uri "$JellyfinUrl/health" -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
            if ($h -eq "Healthy") { Write-OK "Healthy after ${elapsed}s."; return $true }
        } catch { }
        Start-Sleep 2; $elapsed += 2; Write-Host "." -NoNewline -ForegroundColor DarkGray
    }
    Write-Err "Timeout."; return $false
}

# --- Phase 3: Auth ----------------------------------------------------------

function Connect-Jellyfin {
    Write-Step "PHASE 3: Authenticating"
    $body = @{ Username = $JellyfinUser; Pw = $JellyfinPassword } | ConvertTo-Json -Compress
    try {
        $resp = Invoke-RestMethod -Uri "$JellyfinUrl/Users/AuthenticateByName" -Method POST `
            -ContentType "application/json" -Headers (Get-JellyfinDefaultApiHeaders) `
            -Body $body -UseBasicParsing
        $Script:AuthToken  = $resp.AccessToken
        $Script:UserId     = $resp.User.Id
        $Script:AuthHeader = Get-JellyfinDefaultApiHeaders -Token $Script:AuthToken
        $Script:ClientIdentity = [ordered]@{
            Profile = $ClientProfile
            Client = $ClientName
            Device = $ClientDevice
            DeviceId = $ClientDeviceId
            Version = $ClientVersion
            UserAgent = $ClientUserAgent
            AcceptLanguage = $ClientAcceptLanguage
        }
        Write-OK "Authenticated as $($resp.User.Name)"
        Write-Info "Client profile: $ClientProfile | $ClientName / $ClientDevice | DeviceId=$ClientDeviceId | Version=$ClientVersion"
        return $true
    } catch { Write-Err "Auth failed: $($_.Exception.Message)"; return $false }
}

# --- Phase 4: Setup ---------------------------------------------------------

function Initialize-Setup {
    Write-Step "PHASE 4: Plugin & TVH Setup"

    $pluginInfo = Invoke-JellyfinApi -Path "/TvHeadendApi/PluginInfo"
    if ($pluginInfo) { Write-OK "Plugin: $($pluginInfo.Name) v$($pluginInfo.Version)" }

    $sysInfo = Invoke-JellyfinApi -Path "/System/Info"
    if ($sysInfo) { Write-OK "Jellyfin: v$($sysInfo.Version)" }

    $config = Invoke-JellyfinApi -Path "/Plugins/$PLUGIN_GUID/Configuration"
    if (-not $config) { Write-Err "Cannot read config."; return $false }
    $Script:PluginConfig = $config

    $proto = if ($config.UseSSL) { "https" } else { "http" }
    $wr = if ([string]::IsNullOrWhiteSpace($config.Webroot) -or $config.Webroot -eq "/") { "/" } else { $config.Webroot.TrimEnd('/') + "/" }
    $Script:TvhBaseUrl = "${proto}://$($config.Host):$($config.Port)${wr}"

    if (-not $config.AllowAnonymousAccess -and -not [string]::IsNullOrWhiteSpace($config.Username)) {
        $secPw = ConvertTo-SecureString $config.Password -AsPlainText -Force
        $Script:TvhCredential = [pscredential]::new($config.Username, $secPw)
    }

    Write-OK "TVH: $($Script:TvhBaseUrl)"
    Write-Info "Streaming Profile: $($config.StreamingProfile)"
    Write-Info "DirectPlay=$($config.SupportsDirectPlay) | DirectStream=$($config.SupportsDirectStream) | Transcoding=$($config.SupportsTranscoding)"
    Write-Info "Probing=$($config.SupportsProbing) | AnalyzeDurationMs=$($config.AnalyzeDurationMs)"

    $diag = Invoke-JellyfinApi -Path "/TvHeadendApi/Diagnose"
    if ($diag) {
        Write-OK "Diagnose: $($diag.OverallStatus) (Score: $($diag.CompatibilityScore))"
        Write-OK "Server: $($diag.ServerVersion) | Channels: $($diag.ChannelCount)"
    }

    $profile = Invoke-JellyfinApi -Path "/TvHeadendApi/DetectProfile"
    if ($profile -and $profile.Success) {
        Write-OK "Profile: $($profile.ProfileName) ($($profile.ProfileClass)) -> Container: $($profile.Container)"
    }

    return $true
}

# --- Channel mapping --------------------------------------------------------

function Build-ChannelMap {
    Write-Info "Building TVH -> Jellyfin channel mapping..."
    $jellyfinCh = Invoke-JellyfinApi -Path "/LiveTv/Channels?SortBy=Number&SortOrder=Ascending&Limit=500&AddCurrentProgram=false"
    if (-not $jellyfinCh -or -not $jellyfinCh.Items -or $jellyfinCh.Items.Count -eq 0) {
        Write-Warn "No Jellyfin channels found."; return
    }
    $tvhCh = Invoke-TvhApi -Path "api/channel/grid?limit=500&sort=number"
    $tvhByName = @{}
    if ($tvhCh -and $tvhCh.entries) {
        foreach ($c in $tvhCh.entries) { $tvhByName[$c.name.Trim().ToLowerInvariant()] = $c.uuid }
    }
    $mapCount = 0
    foreach ($ch in $jellyfinCh.Items) {
        $tvhUuid = $null
        if ($ch.PSObject.Properties.Name -contains 'ExternalId' -and $ch.ExternalId -match '[0-9a-f]{32}') {
            $tvhUuid = $Matches[0]
        }
        if (-not $tvhUuid -and $ch.Name) {
            $norm = $ch.Name.Trim().ToLowerInvariant()
            if ($tvhByName.ContainsKey($norm)) { $tvhUuid = $tvhByName[$norm] }
        }
        if ($tvhUuid) {
            $Script:TvhToJellyfinMap[$tvhUuid] = [PSCustomObject]@{
                JellyfinItemId = $ch.Id; Name = $ch.Name
                Number = if ($ch.PSObject.Properties.Name -contains 'ChannelNumber') { $ch.ChannelNumber } else { "" }
            }
            $mapCount++
        }
    }
    Write-OK "Mapped $mapCount channels."
}

function Get-TestChannels {
    Write-Step "PHASE 5: Selecting channels"
    $tvhCh = Invoke-TvhApi -Path "api/channel/grid?limit=500&sort=number"
    if (-not $tvhCh -or -not $tvhCh.entries -or $tvhCh.entries.Count -eq 0) {
        Write-Err "No TVH channels."; return @()
    }
    $allChannels = [System.Collections.ArrayList]::new()
    foreach ($c in $tvhCh.entries) {
        $num = if ($c.number) { if ($c.number % 1 -eq 0) { [int]$c.number } else { $c.number } } else { 0 }
        $enabled = if ($null -ne $c.enabled) { $c.enabled } else { $true }
        if ($enabled) {
            [void]$allChannels.Add([PSCustomObject]@{ Id = $c.uuid; Name = $c.name; ChannelNumber = $num })
        }
    }
    $allChannels = [System.Collections.ArrayList]@($allChannels | Sort-Object -Property ChannelNumber)

    if ($MaxChannels -gt 0 -and $allChannels.Count -gt $MaxChannels) {
        $step = [math]::Floor($allChannels.Count / $MaxChannels)
        $selected = [System.Collections.ArrayList]::new()
        for ($i = 0; $i -lt $MaxChannels -and ($i * $step) -lt $allChannels.Count; $i++) {
            [void]$selected.Add($allChannels[$i * $step])
        }
        $allChannels = $selected
    }
    Write-OK "Selected $($allChannels.Count) of $($tvhCh.entries.Count) channels."
    return $allChannels
}

# --- Jellyfin pipeline test (single channel) --------------------------------

function Test-ChannelViaJellyfin {
    param(
        [string]$TvhChannelId,
        [int]$HoldSec = 30,
        [int]$Timeout = 30,
        [string]$RunKey = "",
        [string]$RunArtifactRoot = ""
    )

    $result = [ordered]@{
        ChannelId = $TvhChannelId; ChannelName = ""; ChannelNumber = ""; JellyfinItemId = ""
        PlaybackInfoMs = -1; StreamOpenMs = -1; TotalMs = -1
        BytesReceived = 0; BitrateKbps = 0; TranscodeDecision = ""; Container = ""
        SupportsProbing = ""; AnalyzeDurationMs = ""; MediaStreams = ""
        HoldDurationSec = $HoldSec; Success = $false; Error = ""
        # New: detailed log analysis
        ProbingDetected = $false; ProbingRequested = $false; ProbingDisabled = $false
        FfmpegLaunched = $false; FfmpegArgs = ""
        FfmpegInputCodecVideo = ""; FfmpegInputCodecAudio = ""
        FfmpegOutputCodecVideo = ""; FfmpegOutputCodecAudio = ""
        FfmpegInputContainer = ""; FfmpegOutputContainer = ""
        JellyfinTranscodeReasons = ""
        HasStreamDetails = $false; DetailedMediaSource = $false
        StreamOpenedAfterMs = ""; JellyfinStreamOpenMs = ""
        TvhSubscriptions = @(); TvhSubscriptionCount = 0
        TvhProfile = ""; TvhService = ""; TvhInRate = ""; TvhOutRate = ""; TvhErrors = 0
        TvhInputs = @(); TvhConnections = @()
        JellyfinLogErrors = @(); JellyfinLogWarnings = @()
        JellyfinRelevantLogs = @(); JellyfinAllLogs = @()
        PlaybackUrlType = ""; PlaybackUrl = ""; MediaSourcePath = ""; JellyfinTranscodingUrl = ""
        MediaSourcePathOutsideJellyfin = $false
        BypassedJellyfinPipeline = $false
        ArtifactDirectory = ""; SavedStreamPath = ""; SavedLogPath = ""; SavedAllLogPath = ""; SavedMetadataPath = ""; SavedTvhPath = ""; SavedTvhInputsPath = ""; SavedTvhConnectionsPath = ""
    }

    $jellyfinItem = $null
    if ($Script:TvhToJellyfinMap.ContainsKey($TvhChannelId)) { $jellyfinItem = $Script:TvhToJellyfinMap[$TvhChannelId] }
    if (-not $jellyfinItem) { $result.Error = "No Jellyfin mapping"; return $result }
    $result.JellyfinItemId = $jellyfinItem.JellyfinItemId
    $result.ChannelName    = $jellyfinItem.Name
    $result.ChannelNumber  = $jellyfinItem.Number

    if (-not [string]::IsNullOrWhiteSpace($RunArtifactRoot)) {
        $channelDirName = "{0}_{1}" -f (Convert-ToSafeName "$($result.ChannelNumber)"), (Convert-ToSafeName $result.ChannelName)
        $channelArtifactDir = Join-Path $RunArtifactRoot $channelDirName
        Ensure-Directory $channelArtifactDir
        $result.ArtifactDirectory = $channelArtifactDir
    }

    $playSessionId = $null; $liveStreamId = $null; $playbackRequestBody = $null
    $totalSw = [System.Diagnostics.Stopwatch]::StartNew()

    # Capture log start timestamp BEFORE the test
    $logSince = Get-DockerLogTimestamp

    try {
        # 1. PlaybackInfo
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $playbackRequestBody = Get-PlaybackInfoRequestBody -UserId $Script:UserId
        $playbackInfo = Invoke-JellyfinApi -Path "/Items/$($jellyfinItem.JellyfinItemId)/PlaybackInfo" -Method POST -Body $playbackRequestBody -TimeoutSec $Timeout
        $sw.Stop()
        $result.PlaybackInfoMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 1)

        if (-not $playbackInfo -or -not $playbackInfo.MediaSources -or $playbackInfo.MediaSources.Count -eq 0) {
            $result.Error = "PlaybackInfo returned no MediaSources"; return $result
        }

        $playSessionId = $playbackInfo.PlaySessionId
        $ms = $playbackInfo.MediaSources[0]
        $liveStreamId = if ($ms.PSObject.Properties.Name -contains 'LiveStreamId') { $ms.LiveStreamId } else { $null }

        $result.Container         = if ($ms.PSObject.Properties.Name -contains 'Container') { $ms.Container } else { "" }
        $result.SupportsProbing   = if ($ms.PSObject.Properties.Name -contains 'SupportsProbing') { "$($ms.SupportsProbing)" } else { "" }
        $result.AnalyzeDurationMs = if ($ms.PSObject.Properties.Name -contains 'AnalyzeDurationMs' -and $ms.AnalyzeDurationMs) { "$($ms.AnalyzeDurationMs)" } else { "" }
        $result.MediaSourcePath   = if ($ms.PSObject.Properties.Name -contains 'Path' -and $ms.Path) { $ms.Path } else { "" }
        $result.JellyfinTranscodingUrl = if ($ms.PSObject.Properties.Name -contains 'TranscodingUrl' -and $ms.TranscodingUrl) { $ms.TranscodingUrl } else { "" }
        if (-not [string]::IsNullOrWhiteSpace($result.MediaSourcePath) -and -not (Test-IsJellyfinManagedUrl -UrlOrPath $result.MediaSourcePath)) {
            $result.MediaSourcePathOutsideJellyfin = $true
        }

        if ($ms.PSObject.Properties.Name -contains 'MediaStreams' -and $ms.MediaStreams) {
            $result.MediaStreams = ($ms.MediaStreams | ForEach-Object {
                $sType = if ($_.PSObject.Properties.Name -contains 'Type' -and $_.Type) { [string]$_.Type } else { "unknown" }
                $sCodec = if ($_.PSObject.Properties.Name -contains 'Codec' -and $_.Codec) { [string]$_.Codec } else { "n/a" }
                ('{0}:{1}' -f $sType, $sCodec)
            }) -join ", "
        }

        $hasTranscodingUrl = $ms.PSObject.Properties.Name -contains 'TranscodingUrl' -and -not [string]::IsNullOrWhiteSpace($ms.TranscodingUrl)
        if ($hasTranscodingUrl) { $result.TranscodeDecision = "Transcode" }
        elseif ($ms.PSObject.Properties.Name -contains 'SupportsDirectPlay' -and $ms.SupportsDirectPlay) { $result.TranscodeDecision = "DirectPlay" }
        elseif ($ms.PSObject.Properties.Name -contains 'SupportsDirectStream' -and $ms.SupportsDirectStream) { $result.TranscodeDecision = "DirectStream" }
        else { $result.TranscodeDecision = "Unknown" }

        # 1b. Capture TVH subscriptions, inputs, and connections while stream is being set up
        Start-Sleep -Milliseconds 500
        $tvhSubs = @(Get-TvhSubscriptions)
        $profileHint = Get-UrlQueryParameterValue -Url $result.MediaSourcePath -Name "profile"
        if ([string]::IsNullOrWhiteSpace($profileHint) -and $Script:PluginConfig) {
            $profileHint = [string]$Script:PluginConfig.StreamingProfile
        }
        if ($tvhSubs -and $tvhSubs.Count -gt 0) {
            $result.TvhSubscriptionCount = $tvhSubs.Count
            $result.TvhSubscriptions = $tvhSubs
            $bestSub = Select-TvhSubscriptionBestMatch -Subscriptions $tvhSubs -ChannelName $result.ChannelName -ProfileHint $profileHint
            Set-ResultFromTvhSubscription -Result $result -Subscription $bestSub -ProfileFallback $profileHint
        } else {
            Set-ResultFromTvhSubscription -Result $result -Subscription $null -ProfileFallback $profileHint
        }
        $result.TvhInputs = @(Get-TvhInputStatus)
        $result.TvhConnections = @(Get-TvhConnections)

        # 2. Resolve a Jellyfin-managed playback URL and hold it open.
        $streamUrl = $null; $extraHeaders = @{}; $credential = $null
        $resolvedPlayback = Get-JellyfinManagedPlaybackUrl -JellyfinItemId $jellyfinItem.JellyfinItemId -MediaSource $ms -PlaySessionId $playSessionId
        if ($resolvedPlayback) {
            $streamUrl = $resolvedPlayback.Url
            $result.PlaybackUrlType = $resolvedPlayback.UrlType
            $extraHeaders = Get-JellyfinStreamHeaders -RequiredHeaders $resolvedPlayback.RequiredHeaders
        }

        $result.PlaybackUrl = $streamUrl

        if (-not $streamUrl) {
            if ($result.MediaSourcePathOutsideJellyfin) {
                $result.Error = "No Jellyfin-managed playback URL available. MediaSource.Path points outside Jellyfin and is intentionally rejected."
            } else {
                $result.Error = "No Jellyfin-managed playback URL available in PlaybackInfo response (TranscodingUrl/DirectStreamUrl/managed Path missing)."
            }
            return $result
        }

        $result.BypassedJellyfinPipeline = -not (Test-IsJellyfinManagedUrl -UrlOrPath $streamUrl)

        # Notify Jellyfin that playback has started (keeps transcode session alive)
        if ($playSessionId) {
            $mediaSourceId = if ($ms.PSObject.Properties.Name -contains 'Id') { $ms.Id } else { "" }
            try {
                Invoke-JellyfinApi -Path "/Sessions/Playing" -Method POST -Body ([ordered]@{
                    PlaySessionId = $playSessionId
                    ItemId = $jellyfinItem.JellyfinItemId
                    MediaSourceId = $mediaSourceId
                    PositionTicks = 0
                    PlayMethod = "Transcode"
                    CanSeek = $false
                    IsPaused = $false
                }) -TimeoutSec 5 | Out-Null
            } catch { }
        }

        if ($streamUrl) {
            $streamSw = [System.Diagnostics.Stopwatch]::StartNew()
            $fileStream = $null
            $lastHeartbeatTime = [DateTime]::UtcNow
            try {
                $request = [System.Net.HttpWebRequest]::Create($streamUrl)
                $request.Method = "GET"
                $request.Timeout = ($HoldSec + 15) * 1000
                $request.ReadWriteTimeout = ($HoldSec + 15) * 1000
                $request.AllowAutoRedirect = $true
                Set-HttpWebRequestHeaders -Request $request -Headers $extraHeaders

                $response = $request.GetResponse()
                $stream = $response.GetResponseStream()
                $buffer = New-Object byte[] 65536
                $totalRead = 0; $firstByteRecorded = $false
                $deadline = $null
                $isHlsSegmentMode = $false
                $hlsPlaylistUrl = $null
                $lastHlsSegmentUrl = $null
                $hlsSegmentRetryCount = 0

                # Capture TVH subscriptions once data is flowing
                $tvhCaptured = $false

                $initialBytes = New-Object byte[] 512
                $initialRead = $stream.Read($initialBytes, 0, $initialBytes.Length)
                if ($initialRead -le 0) {
                    throw "Jellyfin stream endpoint returned no data."
                }

                $initialText = [System.Text.Encoding]::ASCII.GetString($initialBytes, 0, [Math]::Min($initialRead, 256))
                $contentType = if ($response.ContentType) { $response.ContentType } else { "" }
                if ($contentType -match 'text/html|text/plain' -or $initialText -match '^(<!DOCTYPE|<HTML|<html|401 Unauthorized|403 Forbidden)') {
                    throw "Jellyfin stream endpoint returned non-media content ($contentType). First bytes indicate HTML/auth page."
                }

                if ($contentType -match '(?i)(mpegurl|vnd\.apple\.mpegurl)' -or $initialText -match '^#EXTM3U') {
                    $stream.Close()
                    $response.Close()

                    $resolvedSegment = Resolve-HlsSegmentUrl -PlaylistUrl $streamUrl -Headers $extraHeaders -TimeoutSec ([Math]::Max(5, $HoldSec + 5))
                    if (-not $resolvedSegment -or [string]::IsNullOrWhiteSpace($resolvedSegment.SegmentUrl)) {
                        throw "HLS playlist returned, but no media segment could be resolved."
                    }

                    $result.PlaybackUrlType = "$($result.PlaybackUrlType):HlsSegment"
                    $result.PlaybackUrl = $resolvedSegment.SegmentUrl
                    $isHlsSegmentMode = $true
                    $hlsPlaylistUrl = $resolvedSegment.PlaylistUrl
                    $lastHlsSegmentUrl = $resolvedSegment.SegmentUrl

                    $request = [System.Net.HttpWebRequest]::Create($resolvedSegment.SegmentUrl)
                    $request.Method = "GET"
                    $request.Timeout = ($HoldSec + 15) * 1000
                    $request.ReadWriteTimeout = ($HoldSec + 15) * 1000
                    $request.AllowAutoRedirect = $true
                    Set-HttpWebRequestHeaders -Request $request -Headers $extraHeaders

                    $response = $request.GetResponse()
                    $stream = $response.GetResponseStream()
                    $initialRead = $stream.Read($initialBytes, 0, $initialBytes.Length)
                    if ($initialRead -le 0) {
                        throw "Resolved HLS media segment returned no data."
                    }

                    $initialText = [System.Text.Encoding]::ASCII.GetString($initialBytes, 0, [Math]::Min($initialRead, 128))
                    $contentType = if ($response.ContentType) { $response.ContentType } else { "" }
                    if ($contentType -match 'text/html|text/plain' -or $initialText -match '^(<!DOCTYPE|<HTML|<html|401 Unauthorized|403 Forbidden|#EXTM3U)') {
                        throw "Resolved HLS media segment returned non-media content ($contentType)."
                    }
                }

                # Hold timer starts after first validated media bytes, not after request setup.
                $deadline = [DateTime]::UtcNow.AddSeconds($HoldSec)

                if ($result.ArtifactDirectory) {
                    $captureExtension = Get-StreamCaptureExtension -Container $result.Container -ContentType $contentType -StreamUrl $result.PlaybackUrl
                    $streamFilePath = Join-Path $result.ArtifactDirectory ("stream_capture.{0}" -f $captureExtension)
                    $fileStream = [System.IO.File]::Open($streamFilePath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::Read)
                    $result.SavedStreamPath = $streamFilePath
                }

                if (-not $firstByteRecorded) {
                    $result.StreamOpenMs = [math]::Round($streamSw.Elapsed.TotalMilliseconds, 1)
                    $firstByteRecorded = $true
                }
                $totalRead += $initialRead
                if ($fileStream) { $fileStream.Write($initialBytes, 0, $initialRead) }

                while ([DateTime]::UtcNow -lt $deadline) {
                    $bytesRead = $stream.Read($buffer, 0, $buffer.Length)
                    if ($bytesRead -le 0) {
                        if ($isHlsSegmentMode -and -not [string]::IsNullOrWhiteSpace($hlsPlaylistUrl) -and [DateTime]::UtcNow -lt $deadline) {
                            $nextSegment = Resolve-HlsSegmentUrl -PlaylistUrl $hlsPlaylistUrl -Headers $extraHeaders -TimeoutSec 10 -ExcludeUrl $lastHlsSegmentUrl
                            if ($nextSegment -and -not [string]::IsNullOrWhiteSpace($nextSegment.SegmentUrl) -and $nextSegment.SegmentUrl -ne $lastHlsSegmentUrl) {
                                $stream.Close()
                                $response.Close()

                                $request = [System.Net.HttpWebRequest]::Create($nextSegment.SegmentUrl)
                                $request.Method = "GET"
                                $request.Timeout = ($HoldSec + 15) * 1000
                                $request.ReadWriteTimeout = ($HoldSec + 15) * 1000
                                $request.AllowAutoRedirect = $true
                                Set-HttpWebRequestHeaders -Request $request -Headers $extraHeaders

                                $response = $request.GetResponse()
                                $stream = $response.GetResponseStream()
                                $lastHlsSegmentUrl = $nextSegment.SegmentUrl
                                $hlsSegmentRetryCount = 0
                                continue
                            }

                            $hlsSegmentRetryCount++
                            if ($hlsSegmentRetryCount -le 20) {
                                # Send heartbeat while waiting for next HLS segment
                                if ($playSessionId -and ([DateTime]::UtcNow - $lastHeartbeatTime).TotalSeconds -ge 5) {
                                    $lastHeartbeatTime = [DateTime]::UtcNow
                                    Send-PlaybackHeartbeat -PlaySessionId $playSessionId -ItemId $jellyfinItem.JellyfinItemId -MediaSourceId $mediaSourceId
                                }
                                Start-Sleep -Milliseconds 1000
                                continue
                            }
                        }

                        break
                    }
                    $totalRead += $bytesRead
                    if ($fileStream) { $fileStream.Write($buffer, 0, $bytesRead) }

                    # Capture TVH subscription details once after ~2s of streaming
                    if (-not $tvhCaptured -and $streamSw.Elapsed.TotalSeconds -ge 2) {
                        $tvhCaptured = $true
                        $midSubs = @(Get-TvhSubscriptions)
                        if ($midSubs -and $midSubs.Count -gt 0) {
                            $result.TvhSubscriptionCount = $midSubs.Count
                            $result.TvhSubscriptions = $midSubs
                            $midHint = Get-UrlQueryParameterValue -Url $result.PlaybackUrl -Name "profile"
                            if ([string]::IsNullOrWhiteSpace($midHint)) { $midHint = $profileHint }
                            $bestMidSub = Select-TvhSubscriptionBestMatch -Subscriptions $midSubs -ChannelName $result.ChannelName -ProfileHint $midHint
                            Set-ResultFromTvhSubscription -Result $result -Subscription $bestMidSub -ProfileFallback $midHint
                        }
                    }

                    # Send periodic heartbeat to keep Jellyfin transcode session alive
                    if ($playSessionId -and ([DateTime]::UtcNow - $lastHeartbeatTime).TotalSeconds -ge 5) {
                        $lastHeartbeatTime = [DateTime]::UtcNow
                        Send-PlaybackHeartbeat -PlaySessionId $playSessionId -ItemId $jellyfinItem.JellyfinItemId -MediaSourceId $mediaSourceId
                    }
                }

                if ($fileStream) { $fileStream.Flush(); $fileStream.Dispose(); $fileStream = $null }
                $stream.Close(); $response.Close(); $streamSw.Stop()
                $result.BytesReceived = $totalRead
                $holdMs = $streamSw.Elapsed.TotalMilliseconds
                if ($holdMs -gt 0 -and $totalRead -gt 0) {
                    $result.BitrateKbps = [math]::Round(($totalRead * 8) / ($holdMs / 1000) / 1000, 0)
                }
            } catch {
                if ($fileStream) { try { $fileStream.Dispose() } catch { } }
                $streamSw.Stop()
                $result.StreamOpenMs = [math]::Round($streamSw.Elapsed.TotalMilliseconds, 1)
                $result.Error = "Stream error: $($_.Exception.Message)"
            }
        }

        $result.Success = ($result.BytesReceived -gt 0)
    }
    catch { $result.Error = $_.Exception.Message }
    finally {
        try {
            if ($playSessionId) {
                Invoke-JellyfinApi -Path "/Sessions/Playing/Stopped" -Method POST -Body @{
                    PlaySessionId = $playSessionId; ItemId = $jellyfinItem.JellyfinItemId
                } | Out-Null
            }
        } catch { }
    }

    $totalSw.Stop()
    $result.TotalMs = [math]::Round($totalSw.Elapsed.TotalMilliseconds, 1)

    # 3. Capture and parse Jellyfin logs for this channel
    Start-Sleep -Seconds 2  # Allow log flush
    $logUntil = Get-DockerLogTimestamp
    # Do NOT wrap in @() — the functions use unary comma (, @(...)) to prevent
    # pipeline unrolling.  Wrapping with @() would nest the array, and PS 5.1
    # would space-join the inner array when binding to [string[]] parameters,
    # collapsing all log lines into a single long string.
    $logLinesRaw = Get-JellyfinContainerLogs -Since $logSince -Until $logUntil
    $logLines = Expand-JellyfinLogLines -LogLines $logLinesRaw
    if ($logLines -and $logLines.Count -gt 0) {
        $logParsed = Parse-JellyfinLogs -LogLines $logLines -ChannelId $TvhChannelId
        $result.ProbingDetected      = $logParsed.ProbingDetected
        $result.ProbingRequested     = $logParsed.ProbingRequested
        $result.ProbingDisabled      = $logParsed.ProbingDisabled
        $result.FfmpegLaunched       = $logParsed.FfmpegLaunched
        $result.FfmpegArgs           = $logParsed.FfmpegArgs
        $result.FfmpegInputCodecVideo  = $logParsed.FfmpegInputCodecVideo
        $result.FfmpegInputCodecAudio  = $logParsed.FfmpegInputCodecAudio
        $result.FfmpegOutputCodecVideo = $logParsed.FfmpegOutputCodecVideo
        $result.FfmpegOutputCodecAudio = $logParsed.FfmpegOutputCodecAudio
        $result.FfmpegInputContainer   = $logParsed.FfmpegInputContainer
        $result.FfmpegOutputContainer  = $logParsed.FfmpegOutputContainer
        $result.JellyfinTranscodeReasons = $logParsed.JellyfinTranscodeReasons
        $result.HasStreamDetails     = $logParsed.HasStreamDetails
        $result.DetailedMediaSource  = $logParsed.DetailedMediaSource
        $result.JellyfinStreamOpenMs = $logParsed.StreamOpenedAfterMs
        $result.JellyfinLogErrors    = @($logParsed.Errors)
        $result.JellyfinLogWarnings  = @($logParsed.Warnings)
        $result.JellyfinRelevantLogs = @($logParsed.RelevantLines)
        $result.JellyfinAllLogs      = @($logParsed.AllLines)
    }

    if ($result.ArtifactDirectory) {
        try {
            $logPath = Join-Path $result.ArtifactDirectory "jellyfin_logs.txt"
            $allLogPath = Join-Path $result.ArtifactDirectory "jellyfin_all_logs.txt"
            $metaPath = Join-Path $result.ArtifactDirectory "metadata.json"
            $tvhPath = Join-Path $result.ArtifactDirectory "tvh_subscriptions.json"
            $tvhInputsPath = Join-Path $result.ArtifactDirectory "tvh_inputs.json"
            $tvhConnectionsPath = Join-Path $result.ArtifactDirectory "tvh_connections.json"

            if ($result.JellyfinRelevantLogs) {
                $result.JellyfinRelevantLogs | Out-File -FilePath $logPath -Encoding UTF8
                $result.SavedLogPath = $logPath
            }

            if ($result.JellyfinAllLogs -and $result.JellyfinAllLogs.Count -gt 0) {
                $result.JellyfinAllLogs | Out-File -FilePath $allLogPath -Encoding UTF8
                $result.SavedAllLogPath = $allLogPath
            }

            if ($result.TvhSubscriptions) {
                Set-Content -Path $tvhPath -Value ($result.TvhSubscriptions | ConvertTo-Json -Depth 8) -Encoding UTF8
                $result.SavedTvhPath = $tvhPath
            }

            if ($result.TvhInputs -and $result.TvhInputs.Count -gt 0) {
                Set-Content -Path $tvhInputsPath -Value ($result.TvhInputs | ConvertTo-Json -Depth 8) -Encoding UTF8
                $result.SavedTvhInputsPath = $tvhInputsPath
            }

            if ($result.TvhConnections -and $result.TvhConnections.Count -gt 0) {
                Set-Content -Path $tvhConnectionsPath -Value ($result.TvhConnections | ConvertTo-Json -Depth 8) -Encoding UTF8
                $result.SavedTvhConnectionsPath = $tvhConnectionsPath
            }

            $meta = [ordered]@{
                RunKey = $RunKey
                ChannelId = $result.ChannelId
                ChannelName = $result.ChannelName
                ChannelNumber = $result.ChannelNumber
                ClientProfile = $ClientProfile
                ClientIdentity = $Script:ClientIdentity
                PlaybackTemplatePath = if ($Script:WebPlaybackTemplate) { $Script:WebPlaybackTemplate.SourcePath } else { "" }
                PlaybackInfoRequestHeaders = $Script:AuthHeader
                PlaybackInfoRequestBody = $playbackRequestBody
                HoldDurationSec = $result.HoldDurationSec
                PlaybackInfoMs = $result.PlaybackInfoMs
                StreamOpenMs = $result.StreamOpenMs
                TotalMs = $result.TotalMs
                BytesReceived = $result.BytesReceived
                BitrateKbps = $result.BitrateKbps
                Decision = $result.TranscodeDecision
                PlaybackUrlType = $result.PlaybackUrlType
                PlaybackUrl = $result.PlaybackUrl
                MediaSourcePath = $result.MediaSourcePath
                MediaSourcePathOutsideJellyfin = $result.MediaSourcePathOutsideJellyfin
                JellyfinTranscodingUrl = $result.JellyfinTranscodingUrl
                BypassedJellyfinPipeline = $result.BypassedJellyfinPipeline
                SupportsProbing = $result.SupportsProbing
                AnalyzeDurationMs = $result.AnalyzeDurationMs
                ProbingDetected = $result.ProbingDetected
                FfmpegLaunched = $result.FfmpegLaunched
                SavedStreamPath = $result.SavedStreamPath
                SavedLogPath = $result.SavedLogPath
                SavedMetadataPath = $result.SavedMetadataPath
                SavedTvhPath = $result.SavedTvhPath
            }
            Set-Content -Path $metaPath -Value ($meta | ConvertTo-Json -Depth 8) -Encoding UTF8
            $result.SavedMetadataPath = $metaPath
        } catch {
            Write-Warn "Artifact save failed for $($result.ChannelName): $($_.Exception.Message)"
        }
    }

    return $result
}

# --- Phase 6: Run all tests ------------------------------------------------

function Invoke-AllTests {
    param([array]$Channels, [string]$RunKey = "", [string]$RunArtifactRoot = "")
    $channelCount = if ($Channels) { $Channels.Count } else { 0 }
    Write-Step "PHASE 6: Testing $channelCount channels via Jellyfin (hold ${StreamHoldSec}s each)"
    $results = [System.Collections.ArrayList]::new()
    $idx = 0
    foreach ($ch in $Channels) {
        $idx++
        Write-Host "`n  [$idx/$($Channels.Count)] " -NoNewline -ForegroundColor DarkYellow
        Write-Host "$($ch.ChannelNumber) $($ch.Name) ... " -NoNewline

        $r = Test-ChannelViaJellyfin -TvhChannelId $ch.Id -HoldSec $StreamHoldSec -Timeout $PlaybackInfoTimeoutSec -RunKey $RunKey -RunArtifactRoot $RunArtifactRoot
        if ($r.Success) {
            $color = if ($r.PlaybackInfoMs -le 5000) { "Green" } elseif ($r.PlaybackInfoMs -le 8000) { "Yellow" } else { "Red" }
            $mb = [math]::Round($r.BytesReceived / 1MB, 1)
            Write-Host "OK" -ForegroundColor $color -NoNewline
            Write-Host " | PBI: $($r.PlaybackInfoMs)ms | TTFB: $($r.StreamOpenMs)ms | $($r.TranscodeDecision) | $($r.Container) | ${mb}MB | $($r.BitrateKbps)kbps" -ForegroundColor $color

            # Additional pipeline info line
            $probeStr = if ($r.ProbingDetected) { "YES (active)" } elseif ($r.ProbingRequested) { "requested" } elseif ($r.ProbingDisabled) { "disabled" } else { "no" }
            $ffmpegStr = if ($r.FfmpegLaunched) { "YES" } else { "no" }
            $detailStr = if ($r.DetailedMediaSource) { "TVH-details" } else { "config-hints" }
            $tvhStr = if ($r.TvhSubscriptionCount -gt 0) { "$($r.TvhSubscriptionCount) sub(s), profile=$($r.TvhProfile)" } else { "no subs" }
            Write-Host "       Probing: $probeStr | FFmpeg: $ffmpegStr | Source: $detailStr | URL: $($r.PlaybackUrlType) | TVH: $tvhStr" -ForegroundColor DarkGray
            if ($r.BypassedJellyfinPipeline) { Write-Host "       Hinweis: Stream wurde direkt von TVHeadend geoeffnet (Jellyfin-FFmpeg wurde umgangen)." -ForegroundColor Yellow }
            if ($r.JellyfinLogErrors -and $r.JellyfinLogErrors.Count -gt 0) {
                Write-Host "       Errors: $($r.JellyfinLogErrors.Count)" -ForegroundColor Red
            }
            if ($r.JellyfinLogWarnings -and $r.JellyfinLogWarnings.Count -gt 0) {
                Write-Host "       Warnings: $($r.JellyfinLogWarnings.Count)" -ForegroundColor Yellow
            }
            # Transformation pipeline summary
            $tvhTx = if (-not [string]::IsNullOrWhiteSpace($r.TvhProfile)) { "TVH:$($r.TvhProfile)" } else { "TVH:unknown" }
            if ($r.FfmpegLaunched) {
                $vIn = if ($r.FfmpegInputCodecVideo) { $r.FfmpegInputCodecVideo } else { "?" }
                $vOut = if ($r.FfmpegOutputCodecVideo -and $r.FfmpegOutputCodecVideo -ne 'copy') { $r.FfmpegOutputCodecVideo } else { "copy" }
                $aIn = if ($r.FfmpegInputCodecAudio) { $r.FfmpegInputCodecAudio } else { "?" }
                $aOut = if ($r.FfmpegOutputCodecAudio -and $r.FfmpegOutputCodecAudio -ne 'copy') { $r.FfmpegOutputCodecAudio } else { "copy" }
                Write-Host "       Pipeline: $tvhTx -> Jellyfin FFmpeg [V:${vIn}->${vOut} A:${aIn}->${aOut}] -> Client" -ForegroundColor Cyan
            } else {
                Write-Host "       Pipeline: $tvhTx -> Jellyfin (no FFmpeg) -> Client" -ForegroundColor Cyan
            }
            if ($r.SavedStreamPath) { Write-Host "       Saved stream: $($r.SavedStreamPath)" -ForegroundColor DarkCyan }
        } else { Write-Host "FAILED: $($r.Error)" -ForegroundColor Red }
        [void]$results.Add($r)
        if ($idx -lt $Channels.Count) { Start-Sleep -Seconds $PauseBetweenTestsSec }
    }
    return $results
}

function Write-TestPlanOverview {
    param(
        [array]$Channels,
        [array]$RunConfigs
    )

    $channelCount = if ($Channels) { $Channels.Count } else { 0 }
    $runCount = if ($RunConfigs) { $RunConfigs.Count } else { 0 }
    $totalChannelTests = $channelCount * $runCount

    $secondsPerChannel = $StreamHoldSec + 2
    $secondsPerRun = ($channelCount * $secondsPerChannel) + ([Math]::Max(0, $channelCount - 1) * $PauseBetweenTestsSec)
    $estimatedSeconds = ($runCount * $secondsPerRun) + ([Math]::Max(0, $runCount - 1) * 5)
    $estimatedDuration = [TimeSpan]::FromSeconds([Math]::Max(0, [math]::Round($estimatedSeconds, 0)))

    Write-Step "PRE-RUN: Testplan"
    Write-Host "  Report output: $ReportPath" -ForegroundColor DarkCyan
    Write-Host "  Artifact root: $ArtifactRoot" -ForegroundColor DarkCyan
    Write-Host "  Channel tests total: $totalChannelTests ($channelCount channels x $runCount runs)" -ForegroundColor DarkCyan
    Write-Host "  Hold/Pause: hold=${StreamHoldSec}s, between channels=${PauseBetweenTestsSec}s, between runs=5s" -ForegroundColor DarkCyan
    Write-Host "  Estimated minimum runtime (without build/auth/setup): $($estimatedDuration.ToString())" -ForegroundColor DarkCyan

    Write-Host "";
    Write-Host "  Channels:" -ForegroundColor Cyan
    $maxChannelPreview = [Math]::Min($channelCount, 10)
    for ($i = 0; $i -lt $maxChannelPreview; $i++) {
        $ch = $Channels[$i]
        Write-Host ("    - {0} {1} (TVH Id: {2})" -f $ch.ChannelNumber, $ch.Name, $ch.Id) -ForegroundColor Gray
    }
    if ($channelCount -gt $maxChannelPreview) {
        Write-Host "    ... +$($channelCount - $maxChannelPreview) weitere Kanaele" -ForegroundColor Gray
    }

    Write-Host "";
    Write-Host "  Planned runs:" -ForegroundColor Cyan
    $idx = 0
    foreach ($run in $RunConfigs) {
        $idx++
        $profileToApply = if ($run.Profile -eq "current") { "(original)" } else { $run.Profile }
        $runArtifactRoot = Join-Path $ArtifactRoot (("{0}_{1}_{2}" -f $run.Label.Replace(' ', ''), (Convert-ToSafeName $run.Profile), (Convert-ToSafeName $run.ScenarioKey)))

        $scenarioMode = Get-ScenarioModeDescription -ScenarioKey $run.ScenarioKey

        Write-Host ("    [{0}/{1}] {2}: profile={3}, scenario={4}" -f $idx, $runCount, $run.Label, $profileToApply, $run.ScenarioDisplay) -ForegroundColor Gray
        Write-Host "      Mode: $scenarioMode" -ForegroundColor DarkGray
        Write-Host "      Artifacts: $runArtifactRoot" -ForegroundColor DarkGray
    }
}

function Get-ScenarioModeDescription {
    param([string]$ScenarioKey)

    $scenarioDef = $Script:ScenarioDefinitions[$ScenarioKey]
    if (-not $scenarioDef) {
        return "n/a"
    }

    $idtPart = if ($scenarioDef.Contains('IgnoreDts')) { ", IgnDts=$($scenarioDef.IgnoreDts)" } else { "" }
    $bufPart = if ($scenarioDef.Contains('BufferMs'))  { ", Buf=$($scenarioDef.BufferMs)" } else { "" }
    return "DP=$($scenarioDef.SupportsDirectPlay), DS=$($scenarioDef.SupportsDirectStream), TR=$($scenarioDef.SupportsTranscoding), Probe=$($scenarioDef.SupportsProbing), AnalyzeMs=$($scenarioDef.AnalyzeDurationMs)$idtPart$bufPart"
}

# --- Phase 7: Report (rewritten for multi-scenario) ------------------------

function Export-Report {
    param([hashtable]$AllScenarioResults, [array]$ScenarioOrder)
    Write-Step "PHASE 7: Generating Report"

    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("# Jellyfin LiveTV Pipeline Analysis")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("| Property | Value |")
    [void]$sb.AppendLine("|----------|-------|")
    [void]$sb.AppendLine("| Date | $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') |")
    [void]$sb.AppendLine("| Script Version | $SCRIPT_VERSION |")
    [void]$sb.AppendLine("| Channels per scenario | $MaxChannels |")
    [void]$sb.AppendLine("| Stream hold time | ${StreamHoldSec}s |")
    [void]$sb.AppendLine("| Scenarios tested | $($ScenarioOrder.Count) |")
    [void]$sb.AppendLine("| Streaming Profile | $($Script:OriginalConfig.StreamingProfile) |")
    [void]$sb.AppendLine("| Client profile | $ClientProfile |")
    [void]$sb.AppendLine("| Client identity | $ClientName / $ClientDevice / $ClientVersion |")
    [void]$sb.AppendLine("| Client device id | $ClientDeviceId |")
    [void]$sb.AppendLine("")

    # --- Scenario comparison table ---
    if ($ScenarioOrder.Count -gt 1) {
        [void]$sb.AppendLine("## Scenario Comparison")
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("| Metric |" + (($ScenarioOrder | ForEach-Object { " $_ |" }) -join ""))
        [void]$sb.AppendLine("|--------|" + (($ScenarioOrder | ForEach-Object { "--------|" }) -join ""))

        $metrics = @("Success Rate", "Avg PBI (ms)", "Avg TTFB (ms)", "Probing Active", "FFmpeg Launched", "Detailed TVH Source", "Avg Bitrate (kbps)")
        foreach ($metric in $metrics) {
            $row = "| $metric |"
            foreach ($sKey in $ScenarioOrder) {
                $results = $AllScenarioResults[$sKey]
                $ok = @($results | Where-Object { $_.Success })
                $val = switch ($metric) {
                    "Success Rate" { "$($ok.Count)/$($results.Count)" }
                    "Avg PBI (ms)" {
                        $pbi = @($ok | Where-Object { $_.PlaybackInfoMs -ge 0 } | ForEach-Object { $_.PlaybackInfoMs })
                        if ($pbi.Count -gt 0) { [math]::Round(($pbi | Measure-Object -Average).Average, 1) } else { "N/A" }
                    }
                    "Avg TTFB (ms)" {
                        $ttfb = @($ok | Where-Object { $_.StreamOpenMs -ge 0 } | ForEach-Object { $_.StreamOpenMs })
                        if ($ttfb.Count -gt 0) { [math]::Round(($ttfb | Measure-Object -Average).Average, 1) } else { "N/A" }
                    }
                    "Probing Active"       { "$(@($results | Where-Object { $_.ProbingDetected }).Count)/$($results.Count)" }
                    "FFmpeg Launched"       { "$(@($results | Where-Object { $_.FfmpegLaunched }).Count)/$($results.Count)" }
                    "Detailed TVH Source"  { "$(@($results | Where-Object { $_.DetailedMediaSource }).Count)/$($results.Count)" }
                    "Avg Bitrate (kbps)" {
                        $br = @($ok | Where-Object { $_.BitrateKbps -gt 0 } | ForEach-Object { $_.BitrateKbps })
                        if ($br.Count -gt 0) { [math]::Round(($br | Measure-Object -Average).Average, 0) } else { "N/A" }
                    }
                }
                $row += " $val |"
            }
            [void]$sb.AppendLine($row)
        }
        [void]$sb.AppendLine("")

        # Per-channel comparison
        [void]$sb.AppendLine("### Per-Channel Comparison (TTFB ms)")
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("| Channel |" + (($ScenarioOrder | ForEach-Object { " $_ |" }) -join ""))
        [void]$sb.AppendLine("|---------|" + (($ScenarioOrder | ForEach-Object { "--------|" }) -join ""))

        # Collect unique channel names from first scenario
        $firstResults = $AllScenarioResults[$ScenarioOrder[0]]
        foreach ($r in $firstResults) {
            $chName = "$($r.ChannelNumber) $($r.ChannelName)" -replace [regex]::Escape('|'), '/'
            $row = "| $chName |"
            foreach ($sKey in $ScenarioOrder) {
                $sResults = $AllScenarioResults[$sKey]
                $match = $sResults | Where-Object { $_.ChannelId -eq $r.ChannelId } | Select-Object -First 1
                if ($match -and $match.Success) {
                    $decision = if ($match.TranscodeDecision) { $match.TranscodeDecision } else { "?" }
                    $row += " $($match.StreamOpenMs) ($decision) |"
                } elseif ($match) {
                    $row += " FAIL |"
                } else {
                    $row += " - |"
                }
            }
            [void]$sb.AppendLine($row)
        }
        [void]$sb.AppendLine("")
    }

    # --- Per-scenario detail sections ---
    foreach ($sKey in $ScenarioOrder) {
        $results = $AllScenarioResults[$sKey]
        $scenarioDef = $Script:ScenarioDefinitions[$sKey]
        $scenarioLabel = if ($scenarioDef) { $scenarioDef.Label } else { "Unknown Scenario" }
        $scenarioDesc = if ($scenarioDef) { $scenarioDef.Description } else { "Scenario definition not found" }

        [void]$sb.AppendLine("---")
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("## Scenario: $scenarioLabel")
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("> $scenarioDesc")
        [void]$sb.AppendLine("")

        $ok   = @($results | Where-Object { $_.Success })
        $fail = @($results | Where-Object { -not $_.Success })
        $pbi  = @($ok | Where-Object { $_.PlaybackInfoMs -ge 0 } | ForEach-Object { $_.PlaybackInfoMs })
        $ttfb = @($ok | Where-Object { $_.StreamOpenMs -ge 0 } | ForEach-Object { $_.StreamOpenMs })

        $avgPbi  = if ($pbi.Count -gt 0) { [math]::Round(($pbi | Measure-Object -Average).Average, 1) } else { "N/A" }
        $minPbi  = if ($pbi.Count -gt 0) { ($pbi | Measure-Object -Minimum).Minimum } else { "N/A" }
        $maxPbi  = if ($pbi.Count -gt 0) { ($pbi | Measure-Object -Maximum).Maximum } else { "N/A" }
        $avgTtfb = if ($ttfb.Count -gt 0) { [math]::Round(($ttfb | Measure-Object -Average).Average, 1) } else { "N/A" }

        [void]$sb.AppendLine("| Metric | Value |")
        [void]$sb.AppendLine("|--------|-------|")
        [void]$sb.AppendLine("| Success | $($ok.Count) / $($results.Count) |")
        [void]$sb.AppendLine("| Avg PlaybackInfo | ${avgPbi} ms |")
        [void]$sb.AppendLine("| Min / Max PBI | ${minPbi} / ${maxPbi} ms |")
        [void]$sb.AppendLine("| Avg TTFB | ${avgTtfb} ms |")
        [void]$sb.AppendLine("| Probing active | $(@($results | Where-Object { $_.ProbingDetected }).Count) / $($results.Count) |")
        [void]$sb.AppendLine("| FFmpeg launched | $(@($results | Where-Object { $_.FfmpegLaunched }).Count) / $($results.Count) |")
        [void]$sb.AppendLine("| TVH detailed source | $(@($results | Where-Object { $_.DetailedMediaSource }).Count) / $($results.Count) |")
        [void]$sb.AppendLine("")

        [void]$sb.AppendLine("| # | Channel | PBI (ms) | TTFB (ms) | Decision | Container | Data (MB) | Bitrate (kbps) | Probing | FFmpeg | Source | TVH Subs | Status |")
        [void]$sb.AppendLine("|---|---------|----------|-----------|----------|-----------|-----------|----------------|---------|--------|--------|----------|--------|")

        foreach ($r in $results) {
            $st   = if ($r.Success) { "OK" } else { "FAIL" }
            $n    = ($r.ChannelName) -replace [regex]::Escape('|'), '/'
            $p    = if ($r.PlaybackInfoMs -ge 0) { "$($r.PlaybackInfoMs)" } else { "-" }
            $t    = if ($r.StreamOpenMs -ge 0) { "$($r.StreamOpenMs)" } else { "-" }
            $d    = if ($r.TranscodeDecision) { $r.TranscodeDecision } else { "-" }
            $c    = if ($r.Container) { $r.Container } else { "-" }
            $mb   = if ($r.BytesReceived -gt 0) { [math]::Round($r.BytesReceived / 1MB, 1) } else { "-" }
            $b    = if ($r.BitrateKbps -gt 0) { "$($r.BitrateKbps)" } else { "-" }
            $prb  = if ($r.ProbingDetected) { "ACTIVE" } elseif ($r.ProbingRequested) { "requested" } elseif ($r.ProbingDisabled) { "disabled" } else { "no" }
            $ff   = if ($r.FfmpegLaunched) { "YES" } else { "no" }
            $src  = if ($r.DetailedMediaSource) { "TVH-detail" } else { "hints" }
            $tsub = "$($r.TvhSubscriptionCount)"
            [void]$sb.AppendLine("| $($r.ChannelNumber) | $n | $p | $t | $d | $c | $mb | $b | $prb | $ff | $src | $tsub | $st |")
        }
        [void]$sb.AppendLine("")

        if ($fail.Count -gt 0) {
            [void]$sb.AppendLine("### Failed Channels")
            [void]$sb.AppendLine("")
            foreach ($f in $fail) { [void]$sb.AppendLine("- **[$($f.ChannelNumber)] $($f.ChannelName)**: $($f.Error)") }
            [void]$sb.AppendLine("")
        }

        # Per-channel detail (collapsible per scenario)
        [void]$sb.AppendLine("### Channel Details")
        [void]$sb.AppendLine("")

        foreach ($r in $results) {
            $chLabel = "[$($r.ChannelNumber)] $($r.ChannelName)"

            [void]$sb.AppendLine("<details>")
            [void]$sb.AppendLine("<summary><b>$chLabel</b> — $(if ($r.Success) { 'OK' } else { 'FAIL' }) | TTFB: $($r.StreamOpenMs)ms | $($r.TranscodeDecision)</summary>")
            [void]$sb.AppendLine("")

            [void]$sb.AppendLine("| Property | Value |")
            [void]$sb.AppendLine("|----------|-------|")
            [void]$sb.AppendLine("| PlaybackInfo latency | $($r.PlaybackInfoMs) ms |")
            [void]$sb.AppendLine("| Time to first byte | $($r.StreamOpenMs) ms |")
            [void]$sb.AppendLine("| Total test duration | $($r.TotalMs) ms |")
            [void]$sb.AppendLine("| Data received | $([math]::Round($r.BytesReceived / 1MB, 2)) MB |")
            [void]$sb.AppendLine("| Bitrate | $($r.BitrateKbps) kbps |")
            [void]$sb.AppendLine("| Container | $($r.Container) |")
            [void]$sb.AppendLine("| Media Streams | $($r.MediaStreams) |")
            [void]$sb.AppendLine("| Decision | $($r.TranscodeDecision) |")
            [void]$sb.AppendLine("| Playback URL Type | $($r.PlaybackUrlType) |")
            [void]$sb.AppendLine("| Bypassed Jellyfin pipeline | $($r.BypassedJellyfinPipeline) |")
            [void]$sb.AppendLine("| SupportsProbing (API) | $($r.SupportsProbing) |")
            [void]$sb.AppendLine("| AnalyzeDurationMs (API) | $($r.AnalyzeDurationMs) |")
            [void]$sb.AppendLine("| Probing detected | $($r.ProbingDetected) |")
            [void]$sb.AppendLine("| FFmpeg launched | $($r.FfmpegLaunched) |")
            [void]$sb.AppendLine("| Detailed MediaSource | $($r.DetailedMediaSource) |")
            [void]$sb.AppendLine("| TVH subs | $($r.TvhSubscriptionCount) |")
            [void]$sb.AppendLine("| TVH profile | $($r.TvhProfile) |")
            [void]$sb.AppendLine("| TVH service | $($r.TvhService) |")
            [void]$sb.AppendLine("| TVH errors | $($r.TvhErrors) |")
            [void]$sb.AppendLine("| Saved stream | $($r.SavedStreamPath) |")
            [void]$sb.AppendLine("| Saved logs | $($r.SavedLogPath) |")
            [void]$sb.AppendLine("| Saved metadata | $($r.SavedMetadataPath) |")
            [void]$sb.AppendLine("| Saved TVH snapshot | $($r.SavedTvhPath) |")
            [void]$sb.AppendLine("")

            # ── Transformation Pipeline ──────────────────────────────────
            $tvhTranscoding = ($r.TvhProfile -and $r.TvhProfile -ne 'pass' -and $r.TvhProfile -ne '(Default profile)')
            $jellyfinTranscoding = $r.FfmpegLaunched
            $ffInV  = if ($r.FfmpegInputCodecVideo)  { $r.FfmpegInputCodecVideo }  else { "?" }
            $ffInA  = if ($r.FfmpegInputCodecAudio)  { $r.FfmpegInputCodecAudio }  else { "?" }
            $ffOutV = if ($r.FfmpegOutputCodecVideo)  { $r.FfmpegOutputCodecVideo }  else { "copy" }
            $ffOutA = if ($r.FfmpegOutputCodecAudio)  { $r.FfmpegOutputCodecAudio }  else { "copy" }
            $ffInC  = if ($r.FfmpegInputContainer)  { $r.FfmpegInputContainer }  else { $r.Container }
            $ffOutC = if ($r.FfmpegOutputContainer)  { $r.FfmpegOutputContainer }  else { "?" }

            [void]$sb.AppendLine("#### Transformation Pipeline")
            [void]$sb.AppendLine("")
            [void]$sb.AppendLine('```')

            if ($tvhTranscoding) {
                [void]$sb.AppendLine("DVB Broadcast")
                [void]$sb.AppendLine("  |")
                [void]$sb.AppendLine("  v")
                [void]$sb.AppendLine("[TVHeadend] profile=$($r.TvhProfile) service=$($r.TvhService)")
                [void]$sb.AppendLine("  | TVH transcodes/remuxes to: $($r.Container)")
                [void]$sb.AppendLine("  v")
            } else {
                [void]$sb.AppendLine("DVB Broadcast")
                [void]$sb.AppendLine("  |")
                [void]$sb.AppendLine("  v")
                [void]$sb.AppendLine("[TVHeadend] profile=$($r.TvhProfile) (passthrough)")
                [void]$sb.AppendLine("  | raw stream ($($r.Container))")
                [void]$sb.AppendLine("  v")
            }

            if ($jellyfinTranscoding) {
                $vChange = if ($ffOutV -eq 'copy') { "$ffInV (copy)" } else { "$ffInV -> $ffOutV" }
                $aChange = if ($ffOutA -eq 'copy') { "$ffInA (copy)" } else { "$ffInA -> $ffOutA" }
                [void]$sb.AppendLine("[Jellyfin FFmpeg] $($r.TranscodeDecision)")
                [void]$sb.AppendLine("  | Video: $vChange")
                [void]$sb.AppendLine("  | Audio: $aChange")
                [void]$sb.AppendLine("  | Container: $ffInC -> $ffOutC")
                if ($r.JellyfinTranscodeReasons) {
                    [void]$sb.AppendLine("  | Reasons: $($r.JellyfinTranscodeReasons)")
                }
                [void]$sb.AppendLine("  v")
                [void]$sb.AppendLine("[Client]")
            } else {
                [void]$sb.AppendLine("[Jellyfin] $($r.TranscodeDecision) (no FFmpeg)")
                [void]$sb.AppendLine("  |")
                [void]$sb.AppendLine("  v")
                [void]$sb.AppendLine("[Client]")
            }

            [void]$sb.AppendLine('```')
            [void]$sb.AppendLine("")

            if ($r.FfmpegArgs) {
                [void]$sb.AppendLine("**FFmpeg Arguments:** ``$($r.FfmpegArgs)``")
                [void]$sb.AppendLine("")
            }

            if ($r.JellyfinLogErrors -and $r.JellyfinLogErrors.Count -gt 0) {
                [void]$sb.AppendLine("**Errors:** $($r.JellyfinLogErrors.Count)")
                foreach ($e in $r.JellyfinLogErrors | Select-Object -First 5) {
                    $eSafe = $e -replace [regex]::Escape('|'), '/'
                    [void]$sb.AppendLine("- ``$eSafe``")
                }
                [void]$sb.AppendLine("")
            }

            if ($r.JellyfinRelevantLogs -and $r.JellyfinRelevantLogs.Count -gt 0) {
                [void]$sb.AppendLine("<details>")
                [void]$sb.AppendLine("<summary>Jellyfin Logs ($($r.JellyfinRelevantLogs.Count) relevant lines)</summary>")
                [void]$sb.AppendLine("")
                [void]$sb.AppendLine('```')
                foreach ($logLine in $r.JellyfinRelevantLogs) {
                    $safe = $logLine -replace 'root:admin', '***:***' -replace 'http://[^@]+@', 'http://***@'
                    [void]$sb.AppendLine($safe)
                }
                [void]$sb.AppendLine('```')
                [void]$sb.AppendLine("")
                [void]$sb.AppendLine("</details>")
                [void]$sb.AppendLine("")
            }

            if ($r.JellyfinAllLogs -and $r.JellyfinAllLogs.Count -gt 0) {
                [void]$sb.AppendLine("<details>")
                [void]$sb.AppendLine("<summary>Jellyfin Full Log ($($r.JellyfinAllLogs.Count) lines)</summary>")
                [void]$sb.AppendLine("")
                [void]$sb.AppendLine('```')
                foreach ($logLine in $r.JellyfinAllLogs) {
                    $safe = $logLine -replace 'root:admin', '***:***' -replace 'http://[^@]+@', 'http://***@'
                    [void]$sb.AppendLine($safe)
                }
                [void]$sb.AppendLine('```')
                [void]$sb.AppendLine("")
                [void]$sb.AppendLine("</details>")
                [void]$sb.AppendLine("")
            }

            if ($r.TvhSubscriptions -and $r.TvhSubscriptions.Count -gt 0) {
                [void]$sb.AppendLine("<details>")
                [void]$sb.AppendLine("<summary>TVH Subscriptions ($($r.TvhSubscriptions.Count))</summary>")
                [void]$sb.AppendLine("")
                [void]$sb.AppendLine('```json')
                try { [void]$sb.AppendLine(($r.TvhSubscriptions | ConvertTo-Json -Depth 5 -Compress:$false)) } catch { [void]$sb.AppendLine("(serialization error)") }
                [void]$sb.AppendLine('```')
                [void]$sb.AppendLine("")
                [void]$sb.AppendLine("</details>")
                [void]$sb.AppendLine("")
            }

            if ($r.TvhInputs -and $r.TvhInputs.Count -gt 0) {
                [void]$sb.AppendLine("<details>")
                [void]$sb.AppendLine("<summary>TVH Inputs ($($r.TvhInputs.Count))</summary>")
                [void]$sb.AppendLine("")
                [void]$sb.AppendLine('```json')
                try { [void]$sb.AppendLine(($r.TvhInputs | ConvertTo-Json -Depth 5 -Compress:$false)) } catch { [void]$sb.AppendLine("(serialization error)") }
                [void]$sb.AppendLine('```')
                [void]$sb.AppendLine("")
                [void]$sb.AppendLine("</details>")
                [void]$sb.AppendLine("")
            }

            if ($r.TvhConnections -and $r.TvhConnections.Count -gt 0) {
                [void]$sb.AppendLine("<details>")
                [void]$sb.AppendLine("<summary>TVH Connections ($($r.TvhConnections.Count))</summary>")
                [void]$sb.AppendLine("")
                [void]$sb.AppendLine('```json')
                try { [void]$sb.AppendLine(($r.TvhConnections | ConvertTo-Json -Depth 5 -Compress:$false)) } catch { [void]$sb.AppendLine("(serialization error)") }
                [void]$sb.AppendLine('```')
                [void]$sb.AppendLine("")
                [void]$sb.AppendLine("</details>")
                [void]$sb.AppendLine("")
            }

            [void]$sb.AppendLine("</details>")
            [void]$sb.AppendLine("")
        }
    }

    $dir = Split-Path -Parent $ReportPath
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Set-Content -Path $ReportPath -Value $sb.ToString() -Encoding UTF8
    Write-OK "Report: $ReportPath"
}

# --- Main -------------------------------------------------------------------

try {
    # Parse scenarios
    $scenarioKeys = @()
    if ($Scenarios -eq "matrix") {
        # Generate full matrix from all parameter combinations
        $parsedAnalyze = @($AnalyzeDurationValues.Split(',') | ForEach-Object { [int]$_.Trim() })
        $parsedBuffer = @($BufferMsValues.Split(',') | ForEach-Object { [int]$_.Trim() })
        $parsedIgnoreDts = @($IgnoreDtsValues.Split(',') | ForEach-Object { ($_.Trim().ToLowerInvariant() -eq 'true') -or ($_.Trim() -eq '1') })
        $matrixScenarios = Build-MatrixScenarios -AnalyzeDurations $parsedAnalyze -BufferValues $parsedBuffer -IgnoreDtsChoices $parsedIgnoreDts
        # Replace scenario definitions with the generated matrix
        $Script:ScenarioDefinitions = $matrixScenarios
        $scenarioKeys = @($matrixScenarios.Keys)
    } elseif ($Scenarios -eq "all") {
        $scenarioKeys = @($Script:ScenarioDefinitions.Keys)
    } elseif ($Scenarios -eq "current") {
        Write-Warn "'current' scenario is deprecated. Using 'directplay' as baseline instead."
        $scenarioKeys = @("directplay")
    } else {
        $scenarioKeys = @($Scenarios.Split(',') | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })
    }

    # Parse streaming profiles
    $profileKeys = @()
    if ($StreamingProfiles -eq "current") {
        $profileKeys = @("current")
    } elseif ($StreamingProfiles -eq "all") {
        $profileKeys = @("jellyfin", "pass")
    } else {
        $profileKeys = @($StreamingProfiles.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    }

    Import-WebPlaybackTemplate -Path $PlaybackTemplatePath

    Write-Host ""
    Write-Host ("=" * 70) -ForegroundColor Cyan
    Write-Host "  Jellyfin LiveTV Pipeline Analyzer v$SCRIPT_VERSION" -ForegroundColor Cyan
    Write-Host "  Channels: $MaxChannels | Hold: ${StreamHoldSec}s | Scenarios: $($scenarioKeys.Count) | Profiles: $($profileKeys.Count)" -ForegroundColor Cyan
    Write-Host "  Mode: $Scenarios | $($scenarioKeys.Count) scenario(s) x $($profileKeys.Count) profile(s) = $($scenarioKeys.Count * $profileKeys.Count) runs" -ForegroundColor DarkCyan
    Write-Host "  Profiles: $($profileKeys -join ', ')" -ForegroundColor DarkCyan
    if ($Scenarios -eq "matrix") {
        Write-Host "  Matrix axes: AnalyzeDuration=[$AnalyzeDurationValues] Buffer=[$BufferMsValues] IgnoreDts=[$IgnoreDtsValues]" -ForegroundColor DarkCyan
    }
    Write-Host "  Client: $ClientProfile | $ClientName / $ClientDevice / $ClientVersion | DeviceId=$ClientDeviceId" -ForegroundColor DarkCyan
    if ($Script:WebPlaybackTemplate) { Write-Host "  Playback template: $($Script:WebPlaybackTemplate.SourcePath)" -ForegroundColor DarkCyan }
    Write-Host "  Artifacts: $ArtifactRoot" -ForegroundColor DarkCyan
    Write-Host "  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Cyan
    Write-Host ("=" * 70) -ForegroundColor Cyan

    if ($Script:RequestedStreamHoldSec -lt $MinStreamHoldSec) {
        Write-Warn "Requested StreamHoldSec=$($Script:RequestedStreamHoldSec)s is below minimum ($MinStreamHoldSec)s. Using ${StreamHoldSec}s."
    }

    Ensure-Directory $ArtifactRoot

    Invoke-BuildAndDeploy
    if (-not (Wait-JellyfinReady)) { Write-Err "Jellyfin not ready."; exit 1 }
    if (-not (Connect-Jellyfin))   { Write-Err "Auth failed."; exit 1 }
    if (-not (Initialize-Setup))   { Write-Err "Setup failed."; exit 1 }

    # Save original config for restoration
    if (-not (Save-OriginalConfig)) { Write-Err "Cannot save config."; exit 1 }

    Build-ChannelMap
    $channels = @(Get-TestChannels)
    if (-not $channels -or $channels.Count -eq 0) { Write-Err "No channels."; exit 1 }

    # Build and run scenario x profile matrix
    $runConfigs = [System.Collections.ArrayList]::new()
    $runCounter = 0
    foreach ($profile in $profileKeys) {
        foreach ($sKey in $scenarioKeys) {
            $scenarioDef = $Script:ScenarioDefinitions[$sKey]
            $scenarioDisplay = if ($scenarioDef) { $scenarioDef.Label } else { $sKey }
            $runCounter++
            [void]$runConfigs.Add([PSCustomObject]@{
                Key = "run$runCounter"
                Label = "Run $runCounter"
                Profile = $profile
                ScenarioKey = $sKey
                ScenarioDisplay = $scenarioDisplay
            })
        }
    }

    $allScenarioResults = @{}
    Write-TestPlanOverview -Channels $channels -RunConfigs $runConfigs
    $runIdx = 0
    foreach ($run in $runConfigs) {
        $runIdx++

        Write-Host ""
        Write-Host ("=" * 70) -ForegroundColor Magenta
        Write-Host "  RUN $runIdx/$($runConfigs.Count): $($run.Profile) + $($run.ScenarioDisplay)" -ForegroundColor Magenta
        Write-Host ("=" * 70) -ForegroundColor Magenta

        $profileToApply = if ($run.Profile -eq "current") { $Script:OriginalConfig.StreamingProfile } else { $run.Profile }

        # Always apply scenario config via the tool — no 'current' passthrough
        if (-not (Set-ScenarioConfig -ScenarioKey $run.ScenarioKey -StreamingProfile $profileToApply)) {
            Write-Err "Failed to apply run '$($run.Profile) + $($run.ScenarioKey)'. Skipping."
            continue
        }

        $runArtifactRoot = Join-Path $ArtifactRoot (("{0}_{1}_{2}" -f $run.Label.Replace(' ', ''), (Convert-ToSafeName $run.Profile), (Convert-ToSafeName $run.ScenarioKey)))
        Ensure-Directory $runArtifactRoot

        $results = @(Invoke-AllTests -Channels $channels -RunKey $run.Key -RunArtifactRoot $runArtifactRoot)
        $allScenarioResults[$run.Key] = $results

        # Brief summary
        $ok = @($results | Where-Object { $_.Success })
        $pbi = @($ok | Where-Object { $_.PlaybackInfoMs -ge 0 } | ForEach-Object { $_.PlaybackInfoMs })
        $avgPbi = if ($pbi.Count -gt 0) { [math]::Round(($pbi | Measure-Object -Average).Average, 1) } else { "N/A" }
        Write-Host "  >> $($run.Profile) + $($run.ScenarioDisplay) : $($ok.Count)/$($results.Count) OK | Avg PBI: ${avgPbi}ms" -ForegroundColor Magenta

        # Pause between scenarios
        if ($runIdx -lt $runConfigs.Count) {
            Write-Info "Pausing 5s before next scenario..."
            Start-Sleep -Seconds 5
        }
    }

    # Restore original config
    Restore-OriginalConfig

    Export-MatrixReport -AllResults $allScenarioResults -RunConfigs $runConfigs -Channels $channels

    # Final summary
    Write-Host ""
    Write-Host ("=" * 70) -ForegroundColor Green
    Write-Host "  ANALYSIS COMPLETE" -ForegroundColor Green
    Write-Host ("=" * 70) -ForegroundColor Green
    Write-Host ""
    foreach ($run in $runConfigs) {
        $results = @($allScenarioResults[$run.Key])
        $label = "$($run.Profile) + $($run.ScenarioDisplay)"
        $ok = @($results | Where-Object { $_.Success })
        $pbi = @($ok | Where-Object { $_.PlaybackInfoMs -ge 0 } | ForEach-Object { $_.PlaybackInfoMs })
        $avgPbi = if ($pbi.Count -gt 0) { [math]::Round(($pbi | Measure-Object -Average).Average, 1) } else { "N/A" }
        $ttfb = @($ok | Where-Object { $_.StreamOpenMs -ge 0 } | ForEach-Object { $_.StreamOpenMs })
        $avgTtfb = if ($ttfb.Count -gt 0) { [math]::Round(($ttfb | Measure-Object -Average).Average, 1) } else { "N/A" }
        $probing = @($results | Where-Object { $_.ProbingDetected }).Count
        $ffmpeg = @($results | Where-Object { $_.FfmpegLaunched }).Count
        $color = if ($ok.Count -eq $results.Count) { "Green" } else { "Yellow" }
        Write-Host "  $label" -ForegroundColor $color
        Write-Host "    Success: $($ok.Count)/$($results.Count) | PBI: ${avgPbi}ms | TTFB: ${avgTtfb}ms | Probing: $probing | FFmpeg: $ffmpeg" -ForegroundColor $color
    }
    Write-Host ""
    Write-Host "  Report: $ReportPath" -ForegroundColor White
    Write-Host ""
}
catch {
    # Always try to restore config on error
    if ($Script:OriginalConfig) { try { Restore-OriginalConfig } catch { } }
    Write-Err "Fatal: $($_.Exception.Message)"
    Write-Err $_.ScriptStackTrace
    exit 1
}








