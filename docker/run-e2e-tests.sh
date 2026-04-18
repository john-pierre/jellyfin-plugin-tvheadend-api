#!/bin/sh
# run-e2e-tests.sh — Brings up the full E2E test stack, runs live integration tests, and tears down.
#
# Usage: ./scripts/run-e2e-tests.sh
# Prerequisites: docker, docker compose, dotnet SDK 8.0+

set -e

COMPOSE_FILE="docker/docker-compose.test.yml"
MAX_BOOTSTRAP_WAIT=120

log() { echo "[e2e] $*"; }

cleanup() {
  log "Tearing down test stack..."
  docker compose -f "$COMPOSE_FILE" down --volumes --remove-orphans 2>/dev/null || true
}

trap cleanup EXIT

log "Starting test stack..."
docker compose -f "$COMPOSE_FILE" up -d --build --wait

log "Waiting for bootstrap to complete (max ${MAX_BOOTSTRAP_WAIT}s)..."
elapsed=0
while [ "$elapsed" -lt "$MAX_BOOTSTRAP_WAIT" ]; do
  STATUS=$(docker inspect --format='{{.State.Status}}' tvh-bootstrap 2>/dev/null || echo "missing")
  if [ "$STATUS" = "exited" ]; then
    EXIT_CODE=$(docker inspect --format='{{.State.ExitCode}}' tvh-bootstrap 2>/dev/null || echo "1")
    if [ "$EXIT_CODE" = "0" ]; then
      log "Bootstrap completed successfully."
      break
    else
      log "ERROR: Bootstrap exited with code $EXIT_CODE"
      docker logs tvh-bootstrap 2>&1 | tail -20
      exit 1
    fi
  fi
  sleep 3
  elapsed=$((elapsed + 3))
done

if [ "$elapsed" -ge "$MAX_BOOTSTRAP_WAIT" ]; then
  log "ERROR: Bootstrap did not complete within ${MAX_BOOTSTRAP_WAIT}s"
  docker logs tvh-bootstrap 2>&1 | tail -20
  exit 1
fi

# Wait a few more seconds for TVH to finish recording
log "Waiting 20s for test recording to complete..."
sleep 20

log "Running live integration tests..."
export TVHEADEND_LIVE_TESTS=true
dotnet test "$PROJECT_ROOT/Jellyfin.Plugin.TvHeadendApi.Tests/Jellyfin.Plugin.TvHeadendApi.Tests.csproj" \
  -c Release \
  --filter "Category=LiveIntegration" \
  --logger "trx;LogFileName=e2e-results.trx" \
  --results-directory TestResults

log "E2E tests complete."

