#!/bin/sh
set -eu

PLUGIN_SOURCE_ROOT="/opt/tvheadend-plugin"
PLUGIN_TARGET_ROOT="/config/plugins"
JELLYFIN_URL="http://localhost:8096"

mkdir -p "$PLUGIN_TARGET_ROOT"

# Copy every bundled plugin version to the persistent plugins directory.
# Existing files are overwritten so container updates propagate new DLL/meta.json.
if [ -d "$PLUGIN_SOURCE_ROOT" ]; then
    for dir in "$PLUGIN_SOURCE_ROOT"/*; do
        [ -d "$dir" ] || continue
        plugin_name="$(basename "$dir")"
        mkdir -p "$PLUGIN_TARGET_ROOT/$plugin_name"
        cp -f "$dir"/* "$PLUGIN_TARGET_ROOT/$plugin_name/"
    done
fi

# ── Start Jellyfin in the background ───────────────────────────────────
/jellyfin/jellyfin &
JELLYFIN_PID=$!

# ── Wait for Jellyfin to become healthy ────────────────────────────────
echo "start-jellyfin: waiting for Jellyfin to become ready..."
MAX_HEALTH_WAIT=120
waited=0
while [ "$waited" -lt "$MAX_HEALTH_WAIT" ]; do
    if curl -sf "${JELLYFIN_URL}/health" >/dev/null 2>&1; then
        echo "start-jellyfin: Jellyfin is healthy after ${waited}s."
        break
    fi
    sleep 2
    waited=$((waited + 2))
done

if [ "$waited" -ge "$MAX_HEALTH_WAIT" ]; then
    echo "start-jellyfin: ERROR — Jellyfin did not become healthy within ${MAX_HEALTH_WAIT}s." >&2
    exit 1
fi

# ── Check startup wizard status ────────────────────────────────────────
SYSTEM_INFO=$(curl -sf "${JELLYFIN_URL}/System/Info/Public" 2>/dev/null || echo '{}')
WIZARD_COMPLETED=$(echo "$SYSTEM_INFO" | grep -o '"StartupWizardCompleted" *: *[a-z]*' | grep -o '[a-z]*$' || echo "unknown")

echo "start-jellyfin: StartupWizardCompleted=${WIZARD_COMPLETED}"

if [ "$WIZARD_COMPLETED" != "true" ]; then
    # ── Run the setup wizard programmatically ──────────────────────────
    echo "start-jellyfin: completing startup wizard..."

    # Step 1: POST /Startup/Configuration
    curl -sf -X POST "${JELLYFIN_URL}/Startup/Configuration" \
        -H "Content-Type: application/json" \
        -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' \
        >/dev/null 2>&1 || echo "start-jellyfin: WARN — Startup/Configuration failed (may already be set)."

    # Step 2a: GET /Startup/User — triggers UserManager.InitializeAsync() which
    # creates the default user if none exists yet (required before POST).
    curl -sf "${JELLYFIN_URL}/Startup/User" >/dev/null 2>&1 \
        || echo "start-jellyfin: WARN — GET Startup/User failed (user init may not have run)."

    # Step 2b: POST /Startup/User — rename and set password on the default user.
    curl -sf -X POST "${JELLYFIN_URL}/Startup/User" \
        -H "Content-Type: application/json" \
        -d '{"Name":"admin","Password":"admin123"}' \
        >/dev/null 2>&1 || echo "start-jellyfin: WARN — POST Startup/User failed."

    # Step 3: POST /Startup/Complete
    curl -sf -X POST "${JELLYFIN_URL}/Startup/Complete" \
        >/dev/null 2>&1 || echo "start-jellyfin: WARN — Startup/Complete failed."

    echo "start-jellyfin: startup wizard completed."
else
    # ── Wizard already completed — verify admin user exists ────────────
    echo "start-jellyfin: wizard already completed, verifying admin user..."
    AUTH_RESULT=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${JELLYFIN_URL}/Users/AuthenticateByName" \
        -H "Content-Type: application/json" \
        -H 'X-Emby-Authorization: MediaBrowser Client="StartScript", Device="Docker", DeviceId="setup", Version="1.0"' \
        -d '{"Username":"admin","Pw":"admin123"}' 2>/dev/null || echo "000")

    if [ "$AUTH_RESULT" = "200" ]; then
        echo "start-jellyfin: admin user verified successfully."
    else
        echo "start-jellyfin: WARN — admin auth returned HTTP ${AUTH_RESULT}. Tests may need to create the user."
    fi
fi

# ── Wait forever (Jellyfin is running in the background) ──────────────
echo "start-jellyfin: setup complete, waiting for Jellyfin process (PID ${JELLYFIN_PID})..."
wait "$JELLYFIN_PID"
