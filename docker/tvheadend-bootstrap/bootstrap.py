#!/usr/bin/env python3
"""bootstrap.py — Automated first-run configuration of TVHeadend for E2E tests.

Runs after TVHeadend is healthy. Configures IPTV network, channels, EPG, users, profiles.

Environment variables:
    TVH_URL      — TVHeadend base URL (default: http://tvheadend:9981)
    IPTV_SIM_URL — IPTV simulator base URL (default: http://iptv-simulator)
    MAX_WAIT     — Max seconds to wait for mux scan (default: 60)
"""

import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

TVH_URL = os.environ.get("TVH_URL", "http://tvheadend:9981")
IPTV_SIM_URL = os.environ.get("IPTV_SIM_URL", "http://iptv-simulator")
MAX_WAIT = int(os.environ.get("MAX_WAIT", "60"))
TVH_USER = "testuser"
TVH_PASS = "testpass"


def log(msg: str) -> None:
    print(f"[bootstrap] {msg}", flush=True)


def _build_auth_url(base_url: str) -> str:
    """Insert credentials into URL: http://host → http://user:pass@host."""
    creds = f"{urllib.parse.quote(TVH_USER, safe='')}:{urllib.parse.quote(TVH_PASS, safe='')}"
    return base_url.replace("http://", f"http://{creds}@", 1)


TVH_AUTH_URL = _build_auth_url(TVH_URL)

# Build an opener that handles both HTTP Basic and Digest Auth.
_password_mgr = urllib.request.HTTPPasswordMgrWithDefaultRealm()
_password_mgr.add_password(None, TVH_URL, TVH_USER, TVH_PASS)
_auth_opener = urllib.request.build_opener(
    urllib.request.HTTPBasicAuthHandler(_password_mgr),
    urllib.request.HTTPDigestAuthHandler(_password_mgr),
)


# ---------------------------------------------------------------------------
# HTTP helpers
# ---------------------------------------------------------------------------

def tvh_get_noauth(endpoint: str) -> str | None:
    """GET without authentication. Returns response body or None on error."""
    try:
        with urllib.request.urlopen(f"{TVH_URL}{endpoint}", timeout=10) as resp:
            return resp.read().decode()
    except Exception:
        return None


def tvh_post_noauth(endpoint: str, data: dict[str, str]) -> str | None:
    """POST form data without authentication. Returns response body or None on error."""
    try:
        body = urllib.parse.urlencode(data).encode()
        req = urllib.request.Request(f"{TVH_URL}{endpoint}", data=body, method="POST")
        with urllib.request.urlopen(req, timeout=10) as resp:
            return resp.read().decode()
    except Exception:
        return None


def tvh_get(endpoint: str) -> str | None:
    """GET with HTTP Basic Auth."""
    url = f"{TVH_URL}{endpoint}"
    try:
        with _auth_opener.open(url, timeout=10) as resp:
            return resp.read().decode()
    except urllib.error.HTTPError as e:
        log(f"  WARN: GET {endpoint} returned HTTP {e.code}")
        return None
    except Exception as e:
        log(f"  WARN: GET {endpoint} failed: {e}")
        return None


def tvh_post(endpoint: str, data: dict[str, str]) -> str | None:
    """POST form data with HTTP Basic Auth."""
    url = f"{TVH_URL}{endpoint}"
    body = urllib.parse.urlencode(data).encode()
    req = urllib.request.Request(url, data=body, method="POST")
    try:
        with _auth_opener.open(req, timeout=10) as resp:
            return resp.read().decode()
    except urllib.error.HTTPError as e:
        log(f"  WARN: POST {endpoint} returned HTTP {e.code}")
        return None
    except Exception as e:
        log(f"  WARN: POST {endpoint} failed: {e}")
        return None


# ---------------------------------------------------------------------------
# Wait for TVHeadend API
# ---------------------------------------------------------------------------

log("Waiting for TVHeadend API...")
for _ in range(30):
    resp = tvh_get_noauth("/api/serverinfo")
    if resp and "sw_version" in resp:
        log("TVHeadend API is ready.")
        break
    time.sleep(2)
else:
    log("ERROR: TVHeadend API did not become ready in time.")
    sys.exit(1)

# ===========================================================================
# 1. Create test user with credentials (before anything else)
# ===========================================================================
log(f"Creating test user ({TVH_USER}/{TVH_PASS})...")

access_conf = {
    "enabled": True,
    "username": TVH_USER,
    "prefix": "0.0.0.0/0,::/0",
    "change": [
        "change_rights", "change_chrange", "change_chtags",
        "change_dvr_configs", "change_profiles", "change_conn_limit",
        "change_lang", "change_lang_ui", "change_theme",
        "change_uilevel", "change_xmltv_output", "change_htsp_output",
    ],
    "webui": True,
    "admin": True,
    "streaming": ["basic", "advanced", "htsp"],
    "dvr": ["basic", "htsp", "all", "all_rw", "failed"],
    "comment": "E2E test user",
    "lang": "",
    "themeui": "",
    "langui": "",
    "profile": "",
    "dvr_config": "",
    "channel_min": "0",
    "channel_max": "0",
    "channel_tag_exclude": False,
    "channel_tag": "",
    "xmltv_output_format": 0,
    "htsp_output_format": 0,
    "uilevel": 2,
    "uilevel_nochange": 0,
    "conn_limit_type": 0,
    "conn_limit": 0,
    "htsp_anonymize": False,
}
tvh_post_noauth("/api/access/entry/create", {"conf": json.dumps(access_conf)})

passwd_conf = {"enabled": True, "username": TVH_USER, "password": TVH_PASS}
tvh_post_noauth("/api/passwd/entry/create", {"conf": json.dumps(passwd_conf)})

log("Test user created. All subsequent calls use authenticated API.")

# ===========================================================================
# 2. Delete the default admin user created by tvheadend -C
# ===========================================================================
log("Removing default -C admin access entry...")

grid_resp = tvh_post("/api/access/entry/grid", {"limit": "50", "groupBy": "false", "groupDir": "ASC"})
if grid_resp:
    try:
        grid = json.loads(grid_resp)
        for entry in grid.get("entries", []):
            if entry.get("username") != TVH_USER:
                uuid = entry.get("uuid")
                if uuid:
                    log(f"  Deleting default access entry {uuid}...")
                    tvh_post("/api/idnode/delete", {"uuid": json.dumps([uuid])})
    except json.JSONDecodeError:
        log(f"  WARN: Could not parse access grid response: {grid_resp[:200]}")
else:
    log("  WARN: No response from access/entry/grid")

log("Default admin entries removed.")

# ===========================================================================
# 3. Create IPTV automatic network
# ===========================================================================
log("Creating IPTV automatic network...")

network_conf = {
    "enabled": True,
    "networkname": "Test IPTV",
    "url": f"{IPTV_SIM_URL}/playlist.m3u",
    "bouquet": True,
    "max_streams": 5,
    "channel_number": 1,
    "refetch_period": 60,
    "service_sid": 0,
    "priority": 1,
}
tvh_post("/api/mpegts/network/create", {
    "class": "iptv_auto_network",
    "conf": json.dumps(network_conf),
})

# ===========================================================================
# 4. Wait for mux scan to complete
# ===========================================================================
log(f"Waiting for mux scan (max {MAX_WAIT}s)...")

elapsed = 0
while elapsed < MAX_WAIT:
    resp = tvh_get("/api/mpegts/mux/grid?limit=50")
    total = 0
    scanning = 0
    if resp:
        try:
            data = json.loads(resp)
            total = data.get("total", 0)
            scanning = sum(1 for e in data.get("entries", []) if e.get("scan_state", 0) != 0)
        except json.JSONDecodeError:
            pass
    log(f"  Mux check: total={total}, elapsed={elapsed}s")
    if total >= 1 and scanning == 0:
        log(f"Mux scan complete. {total} muxes found.")
        break
    if scanning > 0:
        log(f"  Still scanning ({scanning} active)...")
    time.sleep(3)
    elapsed += 3

# ===========================================================================
# 5. Verify channels (bouquet auto-mapping or manual fallback)
# ===========================================================================
# The stack can simulate a production-sized lineup (CHANNEL_COUNT on the simulator);
# scanning that many IPTV muxes takes a while, so poll instead of a fixed sleep and
# fail closed when the expected lineup never materializes.
EXPECTED_CHANNELS = int(os.environ.get("EXPECTED_CHANNELS", "5"))

log(f"Checking channels (expecting >= {EXPECTED_CHANNELS}; waiting for mux scan + mapping)...")


def get_channel_total() -> int:
    resp = tvh_get("/api/channel/grid?limit=1")
    if resp:
        try:
            return json.loads(resp).get("total", 0)
        except json.JSONDecodeError:
            pass
    return 0


def get_service_uuids() -> list[str]:
    resp = tvh_get("/api/mpegts/service/grid?limit=500")
    if resp:
        try:
            return [e["uuid"] for e in json.loads(resp).get("entries", []) if "uuid" in e]
        except json.JSONDecodeError:
            log("  WARN: Could not parse service grid response.")
    return []


scan_deadline = time.time() + max(MAX_WAIT, 240)
ch_total = 0
mapped_uuids: set[str] = set()
while time.time() < scan_deadline:
    ch_total = get_channel_total()
    if ch_total >= EXPECTED_CHANNELS:
        break

    svc_uuids = [u for u in get_service_uuids() if u not in mapped_uuids]
    if svc_uuids:
        log(f"  Mapping {len(svc_uuids)} newly discovered services (channels so far: {ch_total})...")
        mapper_node = {
            "services": svc_uuids,
            "encrypted": False,
            "merge_same_name": False,
            "check_availability": False,
            "type_tags": False,
            "provider_tags": False,
            "network_tags": False,
        }
        tvh_post("/api/service/mapper/save", {"node": json.dumps(mapper_node)})
        mapped_uuids.update(svc_uuids)

    time.sleep(5)

ch_total = get_channel_total()
log(f"  Channels available: {ch_total}")
if ch_total < EXPECTED_CHANNELS:
    log(f"  ERROR: only {ch_total}/{EXPECTED_CHANNELS} channels mapped — failing bootstrap (fail-closed).")
    sys.exit(1)

# ===========================================================================
# 6. Configure XMLTV URL grabber for EPG
# ===========================================================================
log("Configuring EPG grabber...")

epg_config = {
    "channel_rename": False,
    "channel_renumber": False,
    "channel_reicon": False,
    "epgdb_periodicsave": 3600,
    # Internal grabber cron: every minute. The XMLTV import needs two grab
    # passes (pass 1 registers/links channels, pass 2 imports events); the
    # TVHeadend default cron (twice daily) would leave the EPG empty for the
    # whole test run.
    "cron": "* * * * *",
    "int_initial": True,
    "ota_initial": False,
    "ota_cron": "0 */12 * * *",
}
tvh_post("/api/epggrab/config/save", {"node": json.dumps(epg_config)})

# Try to find and enable the XMLTV URL grabber module.
# The /api/epggrab/module/list endpoint returns {"entries": [{"key": ..., "val": ...}]}
# We need to load the full module config via /api/idnode/load to get the UUID.
modules_resp = tvh_get("/api/epggrab/module/list")
url_grabber_uuid = None
if modules_resp:
    log(f"  Module list response (first 800 chars): {modules_resp[:800]}")
    try:
        modules = json.loads(modules_resp)
        entries = modules.get("entries", modules) if isinstance(modules, dict) else modules
        for entry in entries:
            # Check all string fields for "XMLTV" + "URL"
            for field in ("title", "val", "name", "key"):
                val = str(entry.get(field, ""))
                if "xmltv" in val.lower() and "url" in val.lower():
                    url_grabber_uuid = entry.get("uuid") or entry.get("key") or entry.get("id")
                    log(f"  Found XMLTV URL grabber: field={field}, val={val}, uuid={url_grabber_uuid}")
                    break
            if url_grabber_uuid:
                break
    except json.JSONDecodeError:
        log("  WARN: Could not parse module list response")
else:
    log("  WARN: No response from epggrab/module/list")

# If module list didn't yield a UUID, try the grid endpoint
if not url_grabber_uuid:
    log("  Trying /api/epggrab/module/grid to find XMLTV URL grabber...")
    grid_resp = tvh_post("/api/epggrab/module/grid", {"limit": "100"})
    if grid_resp:
        log(f"  Module grid response (first 800 chars): {grid_resp[:800]}")
        try:
            grid = json.loads(grid_resp)
            for entry in grid.get("entries", []):
                title = entry.get("title", "") or entry.get("name", "")
                if "xmltv" in title.lower() and "url" in title.lower():
                    url_grabber_uuid = entry.get("uuid")
                    log(f"  Found via grid: title={title}, uuid={url_grabber_uuid}")
                    break
        except json.JSONDecodeError:
            log("  WARN: Could not parse module grid response")

if url_grabber_uuid:
    log(f"  Enabling XMLTV URL grabber ({url_grabber_uuid}) with args={IPTV_SIM_URL}/epg.xml")
    grabber_node = {
        "uuid": url_grabber_uuid,
        "enabled": True,
        "dn_chnum": 0,
        "args": f"{IPTV_SIM_URL}/epg.xml",
    }
    result = tvh_post("/api/idnode/save", {"node": json.dumps(grabber_node)})
    log(f"  Save result: {result[:200] if result else 'empty'}")
else:
    log("  WARNING: XMLTV URL grabber module not found via any method.")

# ===========================================================================
# 8. Create a test streaming profile
# ===========================================================================
log("Creating test streaming profile...")

profile_conf = {
    "name": "test-pass",
    "comment": "E2E passthrough profile",
    "enabled": True,
    "default": False,
    "timeout": 5,
    "priority": 0,
}
tvh_post("/api/profile/create", {
    "class": "profile-mpegts",
    "conf": json.dumps(profile_conf),
})

# ===========================================================================
# 9. Create a test DVR config and schedule a short recording
# ===========================================================================
log("Creating test DVR config...")

dvr_conf = {
    "name": "test-dvr",
    "storage": "/recordings",
    "file_permissions": "0664",
    "directory_permissions": "0775",
}
tvh_post("/api/dvr/config/create", {"conf": json.dumps(dvr_conf)})

# Schedule a short manual recording so DVR timer/recording e2e tests always have
# data: it shows as an upcoming timer immediately, records for 3 minutes starting
# 2 minutes after bootstrap, and then persists as a completed recording.
log("Scheduling test recording...")
rec_channel = None
ch_resp = tvh_get("/api/channel/grid?limit=1")
if ch_resp:
    try:
        ch_entries = json.loads(ch_resp).get("entries", [])
        if ch_entries:
            rec_channel = ch_entries[0].get("uuid")
    except json.JSONDecodeError:
        pass

if not rec_channel:
    log("  ERROR: No channel available to schedule the test recording — failing bootstrap.")
    sys.exit(1)

now_epoch = int(time.time())
rec_conf = {
    "enabled": True,
    "start": now_epoch + 120,
    "stop": now_epoch + 300,
    "channel": rec_channel,
    "title": {"eng": "E2E Scheduled Recording"},
    "comment": "Created by bootstrap for DVR e2e tests",
}
rec_result = tvh_post("/api/dvr/entry/create", {"conf": json.dumps(rec_conf)})
if not rec_result or "uuid" not in rec_result:
    log(f"  ERROR: DVR entry creation failed (response: {rec_result!r}) — failing bootstrap.")
    sys.exit(1)
log(f"  Scheduled test recording on channel {rec_channel}.")

# ===========================================================================
# 10. Wait for EPG data (fail-closed)
# ===========================================================================
# The XMLTV URL grabber needs at least two grab passes: the first pass discovers
# and auto-maps the XMLTV channels to TVHeadend channels, and a subsequent pass
# imports the actual programme events onto those mapped channels. The internal
# grabber cron is set to every minute (see epg_config above), so two passes
# complete within ~2-3 minutes. /api/epggrab/internal/rerun only takes effect
# once the first scheduled grab has already run, so it merely accelerates
# later passes. This wait runs last so profile/DVR setup overlaps with it.
log("Waiting for EPG events (two grab passes needed)...")

EPG_MAX_WAIT = max(MAX_WAIT, 300)
epg_elapsed = 0
epg_total = 0
last_rerun = -1000
while epg_elapsed < EPG_MAX_WAIT:
    if epg_elapsed - last_rerun >= 20:
        tvh_post("/api/epggrab/internal/rerun", {"rerun": "1"})
        last_rerun = epg_elapsed

    epg_resp = tvh_get("/api/epg/events/grid?limit=1")
    if epg_resp:
        try:
            epg_total = json.loads(epg_resp).get("totalCount", 0)
        except json.JSONDecodeError:
            epg_total = 0
    log(f"  EPG check: events={epg_total}, elapsed={epg_elapsed}s")
    if epg_total >= 1:
        log(f"  EPG loaded: {epg_total} events.")
        break
    time.sleep(5)
    epg_elapsed += 5

if epg_total < 1:
    log(f"  ERROR: EPG still empty after {EPG_MAX_WAIT}s — failing bootstrap (fail-closed).")
    sys.exit(1)

log("Bootstrap complete.")

