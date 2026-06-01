# run-e2e-tests.ps1 — Brings up the full E2E test stack, runs live integration tests, and tears down.
#
# Usage: .\docker\run-e2e-tests.ps1
# Prerequisites: docker, docker compose, dotnet SDK 8.0+

param(
    [int]$MaxBootstrapWait = 120
)

$ErrorActionPreference = "Stop"
$ScriptDir = $PSScriptRoot
$ComposeFile = Join-Path $ScriptDir "docker-compose.test.yaml"
$ProjectRoot = Split-Path -Parent $ScriptDir

# Fail-closed: if the stack never gets to running tests, the script must exit non-zero.
$script:TestExitCode = 1

function Log($msg) { Write-Host "[e2e] $msg" -ForegroundColor Cyan }

function Cleanup {
    Log "Tearing down test stack..."
    try {
        & docker compose -f $ComposeFile down --volumes --remove-orphans --timeout 10 2>&1 | Out-Null
    } catch {
        # Ignore errors during cleanup
    }
}

try {
    Log "Tearing down any previous stack (clean volumes)..."
    try {
        & docker compose -f $ComposeFile down --volumes --remove-orphans --timeout 10 2>&1 | Out-Null
    } catch { }

    Log "Starting test stack (building + waiting for all services)..."
    docker compose -f $ComposeFile up -d --build --wait

    Log "Waiting for bootstrap to complete (max ${MaxBootstrapWait}s)..."
    $elapsed = 0
    while ($elapsed -lt $MaxBootstrapWait) {
        $status = docker inspect --format='{{.State.Status}}' tvheadend-bootstrap-test 2>$null
        if ($status -eq "exited") {
            $exitCode = docker inspect --format='{{.State.ExitCode}}' tvheadend-bootstrap-test 2>$null
            if ($exitCode -eq "0") {
                Log "Bootstrap completed successfully."
                break
            } else {
                Log "ERROR: Bootstrap exited with code $exitCode"
                docker logs tvheadend-bootstrap-test 2>&1 | Select-Object -Last 30
                exit 1
            }
        }
        Start-Sleep -Seconds 3
        $elapsed += 3
    }

    if ($elapsed -ge $MaxBootstrapWait) {
        Log "ERROR: Bootstrap did not complete within ${MaxBootstrapWait}s"
        docker logs tvheadend-bootstrap-test 2>&1 | Select-Object -Last 30
        exit 1
    }

    Log "Running live integration tests..."
    $env:TVHEADEND_LIVE_TESTS = "true"
    $env:JELLYFIN_URL = "http://localhost:18096"
    # TVH_HOST/PORT: The hostname Jellyfin uses to reach TVHeadend.
    # Jellyfin runs INSIDE the Docker network, so use the Docker service name + internal port.
    $env:TVH_HOST = "tvheadend"
    $env:TVH_PORT = "9981"
    $TestProject = Join-Path (Join-Path $ProjectRoot "Jellyfin.Plugin.TvHeadendApi.Tests") "Jellyfin.Plugin.TvHeadendApi.Tests.csproj"
    dotnet test $TestProject `
        -c Release `
        --filter "Category=LiveIntegration" `
        --logger "trx;LogFileName=e2e-results.trx" `
        --results-directory (Join-Path $ProjectRoot "TestResults")
    $script:TestExitCode = $LASTEXITCODE

    if ($script:TestExitCode -eq 0) {
        Log "E2E tests passed."
    } else {
        Log "ERROR: E2E tests FAILED (exit code $($script:TestExitCode))."
    }
} finally {
    Cleanup
}

# Propagate the test result so CI actually gates on it.
if ($script:TestExitCode -ne 0) {
    exit $script:TestExitCode
}
