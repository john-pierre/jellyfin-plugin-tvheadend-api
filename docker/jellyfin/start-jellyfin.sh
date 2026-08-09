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

# ── Keep the first-run wizard reachable ────────────────────────────────
# Jellyfin 10.11+ writes IsStartupWizardCompleted=true into a brand-new config. FirstTimeSetupHandler
# only authorizes /Startup/* while the wizard is INCOMPLETE, so every headless provisioning call
# below returned 401, no admin user was ever created, and the whole live suite failed to
# authenticate — while looking like an ABI problem. Seeding the flag as false before the very first
# start keeps the wizard reachable on 10.10, 10.11 and 12.x alike. Jellyfin fills in every other
# setting with its defaults and rewrites the file on shutdown.
JF_CONFIG_DIR="/config/config"
JF_SYSTEM_XML="${JF_CONFIG_DIR}/system.xml"
if [ ! -f "$JF_SYSTEM_XML" ]; then
    echo "start-jellyfin: seeding system.xml with IsStartupWizardCompleted=false (first run)."
    mkdir -p "$JF_CONFIG_DIR"
    cat > "$JF_SYSTEM_XML" <<'XML'
<?xml version="1.0" encoding="utf-8"?>
<ServerConfiguration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <IsStartupWizardCompleted>false</IsStartupWizardCompleted>
</ServerConfiguration>
XML
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

# ── Wait for the API surface, not just /health ─────────────────────────
# /health answers 200 while the API pipeline is still coming up, so the wizard calls below used
# to fire against a server that answered 503 and every one of them failed silently.
echo "start-jellyfin: waiting for the API to accept requests..."
api_waited=0
while [ "$api_waited" -lt 120 ]; do
    if curl -sf "${JELLYFIN_URL}/System/Info/Public" 2>/dev/null | grep -q '"Version"'; then
        echo "start-jellyfin: API is responding after ${api_waited}s."
        break
    fi
    sleep 2
    api_waited=$((api_waited + 2))
done

# ── Check startup wizard status ────────────────────────────────────────
SYSTEM_INFO=$(curl -sf "${JELLYFIN_URL}/System/Info/Public" 2>/dev/null || echo '{}')
WIZARD_COMPLETED=$(echo "$SYSTEM_INFO" | grep -o '"StartupWizardCompleted" *: *[a-z]*' | grep -o '[a-z]*$' || echo "unknown")

echo "start-jellyfin: StartupWizardCompleted=${WIZARD_COMPLETED}"

if [ "$WIZARD_COMPLETED" != "true" ]; then
    # ── Run the setup wizard programmatically ──────────────────────────
    # Retried as a unit: individual steps can transiently 503 while the server finishes
    # initializing, and a half-applied wizard leaves no usable admin user.
    echo "start-jellyfin: completing startup wizard..."
    wizard_attempt=1
    while [ "$wizard_attempt" -le 10 ]; do
        # Step 1: locale configuration.
        rc_cfg=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${JELLYFIN_URL}/Startup/Configuration" \
            -H "Content-Type: application/json" \
            -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' 2>/dev/null || echo "000")

        # Step 2a: GET /Startup/User triggers UserManager.InitializeAsync(), which creates the
        # default user when none exists yet — required before the POST below.
        rc_getuser=$(curl -s -o /dev/null -w "%{http_code}" "${JELLYFIN_URL}/Startup/User" 2>/dev/null || echo "000")

        # Step 2b: rename the default user and set its password.
        rc_setuser=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${JELLYFIN_URL}/Startup/User" \
            -H "Content-Type: application/json" \
            -d '{"Name":"admin","Password":"admin123"}' 2>/dev/null || echo "000")

        # Step 3: close the wizard.
        rc_done=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${JELLYFIN_URL}/Startup/Complete" 2>/dev/null || echo "000")

        rc_auth=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${JELLYFIN_URL}/Users/AuthenticateByName" \
            -H "Content-Type: application/json" \
            -H 'Authorization: MediaBrowser Client="StartScript", Device="Docker", DeviceId="setup", Version="1.0"' \
            -d '{"Username":"admin","Pw":"admin123"}' 2>/dev/null || echo "000")

        if [ "$rc_auth" = "200" ]; then
            echo "start-jellyfin: wizard applied on attempt ${wizard_attempt} (cfg=${rc_cfg} getuser=${rc_getuser} setuser=${rc_setuser} complete=${rc_done})."
            break
        fi

        echo "start-jellyfin: wizard attempt ${wizard_attempt} incomplete (cfg=${rc_cfg} getuser=${rc_getuser} setuser=${rc_setuser} complete=${rc_done} auth=${rc_auth}); retrying..."
        wizard_attempt=$((wizard_attempt + 1))
        sleep 3
    done

    # Fail loudly. Every wizard step above tolerates its own failure, so without this check a
    # broken provisioning surfaces only much later as ~78 live tests failing to authenticate.
    VERIFY_AUTH=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${JELLYFIN_URL}/Users/AuthenticateByName" \
        -H "Content-Type: application/json" \
        -H 'Authorization: MediaBrowser Client="StartScript", Device="Docker", DeviceId="setup", Version="1.0"' \
        -d '{"Username":"admin","Pw":"admin123"}' 2>/dev/null || echo "000")
    if [ "$VERIFY_AUTH" = "200" ]; then
        echo "start-jellyfin: admin user provisioned and verified."
    else
        echo "start-jellyfin: ERROR — admin provisioning failed (auth returned HTTP ${VERIFY_AUTH}). Live tests cannot authenticate." >&2
    fi
else
    # ── Wizard already completed — verify admin user exists ────────────
    echo "start-jellyfin: wizard already completed, verifying admin user..."
    AUTH_RESULT=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${JELLYFIN_URL}/Users/AuthenticateByName" \
        -H "Content-Type: application/json" \
        -H 'Authorization: MediaBrowser Client="StartScript", Device="Docker", DeviceId="setup", Version="1.0"' \
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
