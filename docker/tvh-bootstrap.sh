#!/bin/sh
# tvh-bootstrap.sh — Automated first-run configuration of TVHeadend for E2E tests.
# Runs after TVHeadend is healthy. Configures IPTV network, channels, EPG, users, profiles.
#
# Environment variables:
#   TVH_URL        — TVHeadend base URL (default: http://tvheadend:9981)
#   IPTV_SIM_URL   — IPTV simulator base URL (default: http://iptv-sim:8888)
#   MAX_WAIT       — Max seconds to wait for mux scan (default: 60)

set -e

TVH_URL="${TVH_URL:-http://tvheadend:9981}"
IPTV_SIM_URL="${IPTV_SIM_URL:-http://iptv-sim}"
MAX_WAIT="${MAX_WAIT:-60}"

log() { echo "[tvh-bootstrap] $*"; }

# Helper: POST JSON to TVH API
tvh_post() {
  endpoint="$1"
  shift
  wget -qO- --post-data="$*" "${TVH_URL}${endpoint}" 2>/dev/null || true
}

# Helper: GET from TVH API
tvh_get() {
  wget -qO- "${TVH_URL}${1}" 2>/dev/null
}

# Wait for TVHeadend API to be reachable
log "Waiting for TVHeadend API..."
for _ in $(seq 1 30); do
  if tvh_get "/api/serverinfo" | grep -q "sw_version"; then
    log "TVHeadend API is ready."
    break
  fi
  sleep 2
done

# 1. Create IPTV automatic network
log "Creating IPTV automatic network..."
tvh_post "/api/mpegts/network/create" \
  "class=iptv_auto_network&conf={\"networkname\":\"Test IPTV\",\"url\":\"${IPTV_SIM_URL}/playlist.m3u\",\"bouquet\":false,\"max_streams\":5,\"channel_number\":1,\"refetch_period\":60,\"service_sid\":0,\"priority\":1}"

# 2. Wait for mux scan to complete
log "Waiting for mux scan (max ${MAX_WAIT}s)..."
elapsed=0
while [ "$elapsed" -lt "$MAX_WAIT" ]; do
  # Check if any muxes exist and all are idle (scan_state == 0)
  RESULT=$(tvh_get "/api/mpegts/mux/grid?limit=50")
  TOTAL=$(echo "$RESULT" | grep -o '"total":[0-9]*' | head -1 | cut -d: -f2)
  log "  Mux check: total=${TOTAL:-0}, elapsed=${elapsed}s"
  if [ -n "$TOTAL" ] && [ "$TOTAL" -ge 1 ]; then
    SCANNING=$(echo "$RESULT" | grep -o '"scan_state":[1-9]' | wc -l)
    if [ "$SCANNING" -eq 0 ]; then
      log "Mux scan complete. ${TOTAL} muxes found."
      break
    fi
    log "  Still scanning (${SCANNING} active)..."
  fi
  sleep 3
  elapsed=$((elapsed + 3))
done

# 3. Map all services to channels
log "Mapping services to channels..."
# List available services
SERVICES=$(tvh_get "/api/mpegts/service/grid?limit=50")
SVC_TOTAL=$(echo "$SERVICES" | grep -o '"total":[0-9]*' | head -1 | cut -d: -f2)
log "  Found ${SVC_TOTAL:-0} services to map."

# Use the service/mapper/start with empty services array = map ALL discovered services
# TVHeadend interprets empty array differently per version, so we also try the save endpoint
tvh_post "/api/service/mapper/start" \
  "conf={\"services\":[],\"encrypted\":false,\"merge_same_name\":false,\"check_availability\":false,\"type_tags\":false,\"provider_tags\":false,\"network_tags\":false}"

# Wait and check — if still 0 channels, try alternative approach
sleep 5
CHANNELS_CHECK=$(tvh_get "/api/channel/grid?limit=50")
CH_TOTAL=$(echo "$CHANNELS_CHECK" | grep -o '"total":[0-9]*' | head -1 | cut -d: -f2)

if [ "${CH_TOTAL:-0}" -eq 0 ]; then
  log "  First mapping attempt returned 0 channels. Trying with explicit service UUIDs..."
  # Extract ONLY the top-level "uuid" field from entries (first occurrence per entry)
  # The grid response has entries like: {"uuid":"xxx","multiplex_uuid":"yyy",...}
  # We split on }, then grab the first uuid from each segment
  SVC_UUIDS=$(echo "$SERVICES" | tr '}' '\n' | grep -o '"uuid":"[^"]*"' | head -"${SVC_TOTAL:-5}" | cut -d'"' -f4 | awk 'BEGIN{printf "["} NR>1{printf ","} {printf "\"%s\"",$0} END{printf "]"}')
  log "  Service UUIDs: ${SVC_UUIDS}"
  tvh_post "/api/service/mapper/start" \
    "conf={\"services\":${SVC_UUIDS},\"encrypted\":false,\"merge_same_name\":false,\"check_availability\":false,\"type_tags\":false,\"provider_tags\":false,\"network_tags\":false}"
  sleep 5
  CHANNELS_CHECK=$(tvh_get "/api/channel/grid?limit=50")
  CH_TOTAL=$(echo "$CHANNELS_CHECK" | grep -o '"total":[0-9]*' | head -1 | cut -d: -f2)
fi

log "  Channels after mapping: ${CH_TOTAL:-0}"

# 4. Configure internal XMLTV grabber
log "Configuring EPG grabber..."
tvh_post "/api/epggrab/config/save" \
  "node={\"channel_rename\":false,\"channel_renumber\":false,\"channel_reicon\":false,\"epgdb_periodicsave\":3600,\"int_initial\":true,\"ota_initial\":false,\"ota_cron\":\"0 */12 * * *\"}"

# Create internal XMLTV grabber module pointing at iptv-sim
tvh_post "/api/epggrab/module/list" ""

# 5. Trigger EPG grab
log "Triggering EPG internal re-run..."
tvh_post "/api/epggrab/internal/rerun" "rerun=1"

# Wait for EPG to populate
sleep 5

# 6. Create test user with credentials
log "Creating test user (testuser/testpass)..."
# Create access entry
tvh_post "/api/access/entry/create" \
  "conf={\"enabled\":true,\"username\":\"testuser\",\"prefix\":\"0.0.0.0/0,::/0\",\"streaming\":[1],\"adv_streaming\":[1],\"dvr\":[1],\"htsp_streaming\":[1],\"profile\":[],\"dvr_config\":[],\"channel_tag\":[],\"comment\":\"E2E test user\"}"

# Create password entry
tvh_post "/api/passwd/entry/create" \
  "conf={\"enabled\":true,\"username\":\"testuser\",\"password\":\"testpass\"}"

# 7. Create a test streaming profile
log "Creating test streaming profile..."
tvh_post "/api/profile/create" \
  "class=profile-mpegts&conf={\"name\":\"test-pass\",\"comment\":\"E2E passthrough profile\",\"enabled\":true,\"default\":false,\"timeout\":5,\"priority\":0}"

# 8. Create a test DVR config and schedule a short recording
log "Creating test DVR config..."
tvh_post "/api/dvr/config/create" \
  "conf={\"name\":\"test-dvr\",\"storage\":\"/recordings\",\"file_permissions\":\"0664\",\"directory_permissions\":\"0775\"}"

# Schedule a 15-second recording on the first channel (starts immediately)
log "Scheduling test recording..."
CHANNELS=$(tvh_get "/api/channel/grid?limit=1")
CH_UUID=$(echo "$CHANNELS" | grep -o '"uuid":"[^"]*"' | head -1 | cut -d'"' -f4)
if [ -n "$CH_UUID" ]; then
  NOW=$(date +%s)
  END=$((NOW + 15))
  tvh_post "/api/dvr/entry/create" \
    "conf={\"channel\":\"${CH_UUID}\",\"start\":${NOW},\"stop\":${END},\"title\":{\"eng\":\"Test Recording\"},\"subtitle\":{\"eng\":\"Automated E2E\"},\"comment\":\"Bootstrap test recording\",\"config_name\":\"test-dvr\"}"
  log "Recording scheduled on channel ${CH_UUID} for 15s."
else
  log "WARNING: No channel found, skipping recording."
fi

log "Bootstrap complete."

