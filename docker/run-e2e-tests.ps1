# run-e2e-tests.ps1 — Brings up the full E2E test stack, runs live integration tests, and tears down.
#
# Usage: .\docker\run-e2e-tests.ps1
# Prerequisites: docker, docker compose, dotnet SDK 8.0+

param(
    [int]$MaxBootstrapWait = 120
)

$ErrorActionPreference = "Stop"
$ScriptDir = $PSScriptRoot
$ComposeFile = Join-Path $ScriptDir "docker-compose.test.yml"
$ProjectRoot = Split-Path -Parent $ScriptDir

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
    Log "Starting test stack..."
    docker compose -f $ComposeFile up -d --build --wait

    Log "Waiting for bootstrap to complete (max ${MaxBootstrapWait}s)..."
    $elapsed = 0
    while ($elapsed -lt $MaxBootstrapWait) {
        $status = docker inspect --format='{{.State.Status}}' tvh-bootstrap 2>$null
        if ($status -eq "exited") {
            $exitCode = docker inspect --format='{{.State.ExitCode}}' tvh-bootstrap 2>$null
            if ($exitCode -eq "0") {
                Log "Bootstrap completed successfully."
                break
            } else {
                Log "ERROR: Bootstrap exited with code $exitCode"
                docker logs tvh-bootstrap 2>&1 | Select-Object -Last 20
                exit 1
            }
        }
        Start-Sleep -Seconds 3
        $elapsed += 3
    }

    if ($elapsed -ge $MaxBootstrapWait) {
        Log "ERROR: Bootstrap did not complete within ${MaxBootstrapWait}s"
        docker logs tvh-bootstrap 2>&1 | Select-Object -Last 20
        exit 1
    }

    # Wait for test recording to complete
    Log "Waiting 20s for test recording to complete..."
    Start-Sleep -Seconds 20

    Log "Running live integration tests..."
    $env:TVHEADEND_LIVE_TESTS = "true"
    $TestProject = Join-Path $ProjectRoot "Jellyfin.Plugin.TvHeadendApi.Tests" "Jellyfin.Plugin.TvHeadendApi.Tests.csproj"
    dotnet test $TestProject `
        -c Release `
        --filter "Category=LiveIntegration" `
        --logger "trx;LogFileName=e2e-results.trx" `
        --results-directory TestResults

    Log "E2E tests complete."
} finally {
    Cleanup
}
