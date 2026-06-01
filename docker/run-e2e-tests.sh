#!/usr/bin/env bash
# run-e2e-tests.sh — Brings up the full E2E test stack, runs live integration tests, tears down.
#
# Linux/CI equivalent of run-e2e-tests.ps1.
# Usage: docker/run-e2e-tests.sh
# Prerequisites: docker, docker compose, dotnet SDK 8.0+
#
# Exit code: propagates the `dotnet test` result so CI actually gates on E2E outcome.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMPOSE_FILE="${SCRIPT_DIR}/docker-compose.test.yaml"
PROJECT_ROOT="$(dirname "${SCRIPT_DIR}")"
PROJECT_NAME="tvh-test"
MAX_BOOTSTRAP_WAIT="${MAX_BOOTSTRAP_WAIT:-120}"
TEST_PROJECT="${PROJECT_ROOT}/Jellyfin.Plugin.TvHeadendApi.Tests/Jellyfin.Plugin.TvHeadendApi.Tests.csproj"

# Fail-closed: any early exit before tests run reports failure.
TEST_EXIT_CODE=1

log() { echo "[e2e] $*"; }

cleanup() {
    log "Tearing down test stack..."
    docker compose -f "$COMPOSE_FILE" -p "$PROJECT_NAME" down --volumes --remove-orphans --timeout 10 >/dev/null 2>&1 || true
}
trap cleanup EXIT

log "Tearing down any previous stack (clean volumes)..."
docker compose -f "$COMPOSE_FILE" -p "$PROJECT_NAME" down --volumes --remove-orphans --timeout 10 >/dev/null 2>&1 || true
# Also clear any containers left under the compose file's own project name (fixed container_name reuse).
docker compose -f "$COMPOSE_FILE" down --volumes --remove-orphans --timeout 10 >/dev/null 2>&1 || true

log "Starting test stack (building + waiting for healthy services)..."
docker compose -f "$COMPOSE_FILE" -p "$PROJECT_NAME" up -d --build --wait

log "Waiting for bootstrap to complete (max ${MAX_BOOTSTRAP_WAIT}s)..."
elapsed=0
while [ "$elapsed" -lt "$MAX_BOOTSTRAP_WAIT" ]; do
    status="$(docker inspect --format='{{.State.Status}}' tvheadend-bootstrap-test 2>/dev/null || echo unknown)"
    if [ "$status" = "exited" ]; then
        code="$(docker inspect --format='{{.State.ExitCode}}' tvheadend-bootstrap-test 2>/dev/null || echo 1)"
        if [ "$code" = "0" ]; then
            log "Bootstrap completed successfully."
            break
        fi
        log "ERROR: Bootstrap exited with code ${code}."
        docker logs tvheadend-bootstrap-test 2>&1 | tail -n 30
        exit 1
    fi
    sleep 3
    elapsed=$((elapsed + 3))
done
if [ "$elapsed" -ge "$MAX_BOOTSTRAP_WAIT" ]; then
    log "ERROR: Bootstrap did not complete within ${MAX_BOOTSTRAP_WAIT}s."
    docker logs tvheadend-bootstrap-test 2>&1 | tail -n 30
    exit 1
fi

log "Running live integration tests..."
# The plugin runs INSIDE the Jellyfin container, so it reaches TVHeadend via the Docker service name.
# Direct-to-TVHeadend service tests fall back to TVHEADEND_URL (localhost) from the host.
export TVHEADEND_LIVE_TESTS=true
export JELLYFIN_URL="http://localhost:18096"
export TVHEADEND_URL="http://localhost:19981"
export TVH_HOST="tvheadend"
export TVH_PORT="9981"
export TVH_USER="testuser"
export TVH_PASS="testpass"

set +e
dotnet test "$TEST_PROJECT" \
    -c Release \
    --filter "Category=LiveIntegration" \
    --logger "trx;LogFileName=e2e-results.trx" \
    --results-directory "${PROJECT_ROOT}/TestResults"
TEST_EXIT_CODE=$?
set -e

if [ "$TEST_EXIT_CODE" -eq 0 ]; then
    log "E2E tests passed."
else
    log "ERROR: E2E tests FAILED (exit code ${TEST_EXIT_CODE})."
fi

exit "$TEST_EXIT_CODE"
