#!/usr/bin/env python3
"""IPTV Simulator — serves M3U playlist, live MPEG-TS streams, XMLTV EPG and channel logos/thumbnails.

Routes:
    GET /health              — Health check
    GET /playlist.m3u        — M3U playlist
    GET /epg.xml             — XMLTV EPG with icons
    GET /logo{n}.png         — Channel logo (generated PNG)
    GET /thumb/{id}/{h}.png  — Programme thumbnail (generated PNG)
    GET /stream/ch{n}.ts     — Live MPEG-TS stream (ffmpeg: clock overlay + tick audio)
"""

import io
import os
import re
import subprocess
import sys
import time
import traceback
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse

from PIL import Image, ImageDraw, ImageFont

HOST = "0.0.0.0"
PORT = int(os.environ.get("PORT", "80"))
BASE_URL = os.environ.get("BASE_URL", "http://iptv-simulator")
FONT_PATH = "/usr/share/fonts/ttf-dejavu/DejaVuSans-Bold.ttf"

# Number of simulated channels. The first five keep their historic names (tests reference
# them); additional channels are generated so the stack can mirror real-world lineups
# (e.g. CHANNEL_COUNT=100 to exercise warmup/zapping at production scale).
CHANNEL_COUNT = max(1, int(os.environ.get("CHANNEL_COUNT", "5")))

_BASE_CHANNELS = [
    {"id": "test-ch1", "name": "Test Channel 1", "group": "News",          "color": (180, 30,  30)},
    {"id": "test-ch2", "name": "Test Channel 2", "group": "Entertainment", "color": (30,  100, 180)},
    {"id": "test-ch3", "name": "Test Channel 3", "group": "Sports",        "color": (30,  150, 60)},
    {"id": "test-ch4", "name": "Test Channel 4", "group": "Documentary",   "color": (160, 90,  0)},
    {"id": "test-ch5", "name": "Test Channel 5", "group": "Music",         "color": (120, 30,  160)},
]

_EXTRA_GROUPS = ["News", "Entertainment", "Sports", "Documentary", "Music", "Movies", "Kids", "Science"]
_EXTRA_COLORS = [
    (180, 30, 30), (30, 100, 180), (30, 150, 60), (160, 90, 0),
    (120, 30, 160), (0, 130, 130), (90, 90, 90), (200, 120, 40),
]

CHANNELS = list(_BASE_CHANNELS[:min(CHANNEL_COUNT, len(_BASE_CHANNELS))])
for _n in range(len(CHANNELS) + 1, CHANNEL_COUNT + 1):
    CHANNELS.append({
        "id": f"test-ch{_n}",
        "name": f"Test Channel {_n}",
        "group": _EXTRA_GROUPS[(_n - 1) % len(_EXTRA_GROUPS)],
        "color": _EXTRA_COLORS[(_n - 1) % len(_EXTRA_COLORS)],
    })


# ---------------------------------------------------------------------------
# Image generators
# ---------------------------------------------------------------------------

def _load_font(size: int) -> ImageFont.FreeTypeFont:
    try:
        return ImageFont.truetype(FONT_PATH, size)
    except Exception:
        return ImageFont.load_default()


def make_logo(channel: dict, size: int = 256) -> bytes:
    """Colored square logo with channel number and name bar."""
    r, g, b = channel["color"]
    img = Image.new("RGB", (size, size), (r, g, b))
    draw = ImageDraw.Draw(img)

    # White circle
    m = size // 8
    draw.ellipse([m, m, size - m, size - m], fill=(255, 255, 255))

    # Channel number centered in circle
    num = channel["id"].removeprefix("test-ch")
    font_big = _load_font(size // 2)
    bb = draw.textbbox((0, 0), num, font=font_big)
    tw, th = bb[2] - bb[0], bb[3] - bb[1]
    draw.text(
        ((size - tw) // 2 - bb[0], (size - th) // 2 - bb[1] - size // 16),
        num, fill=(r, g, b), font=font_big,
    )

    # Bottom label bar
    bar_h = size // 6
    draw.rectangle([0, size - bar_h, size, size], fill=(max(0, r - 40), max(0, g - 40), max(0, b - 40)))
    font_small = _load_font(size // 10)
    label = f"CH {num}"
    bb2 = draw.textbbox((0, 0), label, font=font_small)
    tw2 = bb2[2] - bb2[0]
    draw.text(
        ((size - tw2) // 2, size - bar_h + (bar_h - (bb2[3] - bb2[1])) // 2),
        label, fill=(255, 255, 255), font=font_small,
    )

    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return buf.getvalue()


def make_thumb(channel: dict, hour: int) -> bytes:
    """Programme thumbnail: 320×180, channel color background with text."""
    r, g, b = channel["color"]
    img = Image.new("RGB", (320, 180), (r, g, b))
    draw = ImageDraw.Draw(img)

    # Dark gradient overlay at bottom
    for y in range(90, 180):
        fade = int(150 * (y - 90) / 90)
        draw.line(
            [(0, y), (320, y)],
            fill=(max(0, r - fade), max(0, g - fade), max(0, b - fade)),
        )

    font_title = _load_font(22)
    font_sub = _load_font(14)

    draw.text((14, 14), channel["name"], fill=(255, 255, 255), font=font_title)
    draw.text(
        (14, 44),
        f"{hour:02d}:00 – {(hour + 1) % 24:02d}:00 UTC",
        fill=(220, 220, 220), font=font_sub,
    )
    draw.text((14, 64), channel["group"], fill=(200, 200, 200), font=font_sub)

    # Small channel number badge (top right)
    badge_font = _load_font(28)
    num = channel["id"].removeprefix("test-ch")
    bb = draw.textbbox((0, 0), num, badge_font)
    bw, bh = bb[2] - bb[0] + 16, bb[3] - bb[1] + 10
    draw.rounded_rectangle([320 - bw - 10, 10, 320 - 10, 10 + bh], radius=6,
                           fill=(255, 255, 255, 200))
    draw.text((320 - bw - 10 + 8 - bb[0], 10 + 5 - bb[1]), num,
              fill=(r, g, b), font=badge_font)

    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return buf.getvalue()


def make_index() -> str:
    """Generate an HTML index page listing all available endpoints."""
    now = datetime.now(timezone.utc)
    day_start = now.replace(hour=0, minute=0, second=0, microsecond=0)
    current_hour = now.hour

    rows = []
    for ch in CHANNELS:
        n = ch["id"].removeprefix("test-ch")
        r, g, b = ch["color"]
        rows.append(f"""
        <tr>
          <td><img src="/logo{n}.png" width="48" height="48" style="border-radius:8px"></td>
          <td style="font-weight:bold;color:rgb({r},{g},{b})">{ch["name"]}</td>
          <td>{ch["group"]}</td>
          <td><a href="/stream/ch{n}.ts">ch{n}.ts</a></td>
          <td><a href="/logo{n}.png">logo{n}.png</a></td>
          <td><a href="/thumb/{ch["id"]}/{current_hour}.png">thumb (now)</a></td>
        </tr>""")

    epg_rows = []
    for ch in CHANNELS:
        for h_offset in range(-1, 4):
            slot = day_start + timedelta(hours=current_hour + h_offset)
            title = f"{ch['group']} Show {slot.strftime('%H:%M')}"
            hour_idx = (current_hour + h_offset) % 24
            is_now = h_offset == 0
            style = ' style="background:#ffffcc;font-weight:bold"' if is_now else ""
            epg_rows.append(f"""
            <tr{style}>
              <td>{ch["name"]}</td>
              <td>{slot.strftime("%H:%M")} – {(slot + timedelta(hours=1)).strftime("%H:%M")}</td>
              <td>{title}</td>
              <td><a href="/thumb/{ch["id"]}/{hour_idx}.png"><img src="/thumb/{ch["id"]}/{hour_idx}.png" width="120" height="68" style="border-radius:4px"></a></td>
            </tr>""")

    return f"""<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>IPTV Simulator</title>
<style>
  body {{ font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; margin: 2rem; background: #f5f5f5; color: #333; }}
  h1 {{ color: #222; }} h2 {{ color: #555; margin-top: 2rem; }}
  a {{ color: #0066cc; }}
  table {{ border-collapse: collapse; width: 100%; margin-top: 0.5rem; background: white; border-radius: 8px; overflow: hidden; box-shadow: 0 1px 3px rgba(0,0,0,0.1); }}
  th, td {{ padding: 8px 12px; text-align: left; border-bottom: 1px solid #eee; }}
  th {{ background: #fafafa; font-size: 0.85em; text-transform: uppercase; color: #888; }}
  .links a {{ display: inline-block; margin: 0.3rem 0.8rem 0.3rem 0; padding: 0.4rem 0.8rem; background: #0066cc; color: white; text-decoration: none; border-radius: 4px; font-size: 0.9em; }}
  .links a:hover {{ background: #004c99; }}
  .badge {{ display: inline-block; padding: 2px 8px; border-radius: 4px; font-size: 0.8em; color: white; }}
</style>
</head>
<body>
<h1>📡 IPTV Simulator</h1>
<p>Test IPTV environment for E2E testing. Generated at {now.strftime("%Y-%m-%d %H:%M:%S")} UTC.</p>

<div class="links">
  <a href="/playlist.m3u">📋 Playlist (M3U)</a>
  <a href="/epg.xml">📺 EPG (XMLTV)</a>
  <a href="/health">❤️ Health</a>
</div>

<h2>Channels</h2>
<table>
  <tr><th></th><th>Name</th><th>Group</th><th>Stream</th><th>Logo</th><th>Thumbnail</th></tr>
  {"".join(rows)}
</table>

<h2>EPG Guide (around now)</h2>
<table>
  <tr><th>Channel</th><th>Time (UTC)</th><th>Title</th><th>Thumbnail</th></tr>
  {"".join(epg_rows)}
</table>

<h2>All Thumbnails</h2>
<div style="display:flex;flex-wrap:wrap;gap:8px">
{"".join(
    f'<a href="/thumb/{ch["id"]}/{h}.png" title="{ch["name"]} {h:02d}:00">'
    f'<img src="/thumb/{ch["id"]}/{h}.png" width="96" height="54" style="border-radius:4px;border:2px solid rgb({ch["color"][0]},{ch["color"][1]},{ch["color"][2]})">'
    f'</a>'
    for ch in CHANNELS for h in range(24)
)}
</div>

</body>
</html>"""


# ---------------------------------------------------------------------------
# Playlist & EPG generators
# ---------------------------------------------------------------------------

def make_playlist() -> str:
    lines = ["#EXTM3U"]
    for ch in CHANNELS:
        n = ch["id"].removeprefix("test-ch")
        lines.append(
            f'#EXTINF:-1 tvg-id="{ch["id"]}" tvg-name="{ch["name"]}" '
            f'tvg-logo="{BASE_URL}/logo{n}.png" group-title="{ch["group"]}",{ch["name"]}'
        )
        lines.append(f"{BASE_URL}/stream/ch{n}.ts")
    return "\n".join(lines) + "\n"


def make_epg() -> str:
    now = datetime.now(timezone.utc)
    day_start = now.replace(hour=0, minute=0, second=0, microsecond=0)

    lines = [
        '<?xml version="1.0" encoding="UTF-8"?>',
        '<!DOCTYPE tv SYSTEM "xmltv.dtd">',
        '<tv source-info-name="IPTV Simulator" generator-info-name="iptv-simulator">',
    ]

    for ch in CHANNELS:
        n = ch["id"].removeprefix("test-ch")
        lines += [
            f'  <channel id="{ch["id"]}">',
            f'    <display-name>{ch["name"]}</display-name>',
            f'    <icon src="{BASE_URL}/logo{n}.png"/>',
            f'  </channel>',
        ]

    for ch in CHANNELS:
        for h in range(48):
            start = day_start + timedelta(hours=h)
            stop = start + timedelta(hours=1)
            ts_start = start.strftime("%Y%m%d%H%M%S +0000")
            ts_stop = stop.strftime("%Y%m%d%H%M%S +0000")
            title = f"{ch['group']} Show {start.strftime('%H:%M')}"
            desc = f"Test programme on {ch['name']} starting at {start.strftime('%H:%M')} UTC."
            thumb = f"{BASE_URL}/thumb/{ch['id']}/{h % 24}.png"
            lines += [
                f'  <programme start="{ts_start}" stop="{ts_stop}" channel="{ch["id"]}">',
                f'    <title lang="en">{title}</title>',
                f'    <desc lang="en">{desc}</desc>',
                f'    <category lang="en">{ch["group"]}</category>',
                f'    <icon src="{thumb}"/>',
                f'  </programme>',
            ]

    lines.append("</tv>")
    return "\n".join(lines) + "\n"


# ---------------------------------------------------------------------------
# Live stream via ffmpeg
# ---------------------------------------------------------------------------

def stream_channel(channel: dict, wfile) -> None:
    """Pipe a live MPEG-TS stream to wfile.

    Video: testsrc2 with channel name overlay and running wall-clock.
    Audio: 880 Hz tick for 50 ms every second.
    """
    n = channel["id"].removeprefix("test-ch")
    name = channel["name"].replace("'", "\\'").replace(":", "\\:")
    r, g, b = channel["color"]
    color_hex = f"#{r:02x}{g:02x}{b:02x}"

    vf = (
        # Color bar at top and bottom
        f"drawbox=x=0:y=0:w=iw:h=10:color={color_hex}:t=fill,"
        f"drawbox=x=0:y=ih-10:w=iw:h=10:color={color_hex}:t=fill,"
        # Channel name
        f"drawtext=fontfile={FONT_PATH}"
        f":text='{name}'"
        f":fontsize=72:fontcolor=white:shadowcolor=black:shadowx=3:shadowy=3"
        f":x=(w-text_w)/2:y=100,"
        # Live wall-clock
        f"drawtext=fontfile={FONT_PATH}"
        f":text='%{{localtime\\:%X}}'"
        f":fontsize=96:fontcolor=yellow:shadowcolor=black:shadowx=4:shadowy=4"
        f":x=(w-text_w)/2:y=240"
    )

    # Tick audio: 50 ms burst of 880 Hz sine, repeated every second
    audio_expr = "0.4*sin(2*PI*880*t)*(lt(mod(t\\,1)\\,0.05))"

    cmd = [
        "ffmpeg", "-hide_banner", "-loglevel", "warning",
        "-re",
        "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=25",
        "-f", "lavfi", "-i", f"aevalsrc={audio_expr}:s=44100",
        "-vf", vf,
        "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
        "-b:v", "800k", "-maxrate", "800k", "-bufsize", "400k",
        "-g", "25", "-keyint_min", "25",
        "-c:a", "aac", "-b:a", "64k", "-ar", "44100",
        "-f", "mpegts",
        "-mpegts_flags", "resend_headers",
        "-flush_packets", "1",
        "pipe:1",
    ]

    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    try:
        while True:
            chunk = proc.stdout.read(4096)
            if not chunk:
                break
            wfile.write(chunk)
    except (BrokenPipeError, ConnectionResetError):
        pass
    finally:
        proc.kill()
        stderr_out = proc.stderr.read().decode(errors="replace").strip()
        proc.wait()
        if stderr_out:
            print(f"[iptv-simulator] ffmpeg ch{n} stderr: {stderr_out[:500]}", flush=True)


# ---------------------------------------------------------------------------
# HTTP handler
# ---------------------------------------------------------------------------

class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):  # noqa: D102
        # Suppress default BaseHTTPRequestHandler logging — we do our own
        pass

    def _log(self, level: str, msg: str) -> None:
        ts = datetime.now(timezone.utc).strftime("%H:%M:%S.%f")[:-3]
        print(f"[iptv-simulator] {ts} {level} {self.address_string()} {self.command} {self.path} — {msg}", flush=True)

    def _send(self, code: int, content_type: str, body: bytes) -> None:
        self.send_response(code)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):  # noqa: N802
        t0 = time.monotonic()
        path = urlparse(self.path).path

        try:
            if path == "/" or path == "/index.html":
                body = make_index().encode()
                self._send(200, "text/html", body)
                self._log("INFO", f"200 index ({len(body)} bytes, {self._elapsed(t0)})")

            elif path == "/health":
                self._send(200, "text/plain", b"OK")

            elif path == "/playlist.m3u":
                body = make_playlist().encode()
                self._send(200, "audio/x-mpegurl", body)
                self._log("INFO", f"200 playlist ({len(body)} bytes, {self._elapsed(t0)})")

            elif path == "/epg.xml":
                body = make_epg().encode()
                self._send(200, "application/xml", body)
                self._log("INFO", f"200 epg ({len(body)} bytes, {self._elapsed(t0)})")

            elif m := re.match(r"^/logo(\d+)\.png$", path):
                n = int(m.group(1))
                if 1 <= n <= len(CHANNELS):
                    body = make_logo(CHANNELS[n - 1])
                    self._send(200, "image/png", body)
                    self._log("INFO", f"200 logo{n} ({len(body)} bytes, {self._elapsed(t0)})")
                else:
                    self.send_error(404)
                    self._log("WARN", f"404 logo{n} not found")

            elif m := re.match(r"^/thumb/(test-ch\d)/(\d+)\.png$", path):
                ch = next((c for c in CHANNELS if c["id"] == m.group(1)), None)
                if ch:
                    body = make_thumb(ch, int(m.group(2)) % 24)
                    self._send(200, "image/png", body)
                    self._log("INFO", f"200 thumb ({len(body)} bytes, {self._elapsed(t0)})")
                else:
                    self.send_error(404)
                    self._log("WARN", f"404 thumb channel {m.group(1)} not found")

            elif m := re.match(r"^/stream/ch(\d+)\.ts$", path):
                n = int(m.group(1))
                if 1 <= n <= len(CHANNELS):
                    ch = CHANNELS[n - 1]
                    self._log("INFO", f"200 stream start ch{n} ({ch['name']})")
                    self.send_response(200)
                    self.send_header("Content-Type", "video/mp2t")
                    self.send_header("Connection", "close")
                    self.end_headers()
                    stream_channel(ch, self.wfile)
                    self._log("INFO", f"stream ended ch{n} ({self._elapsed(t0)})")
                else:
                    self.send_error(404)
                    self._log("WARN", f"404 stream ch{n} not found")

            else:
                self.send_error(404)
                self._log("WARN", f"404 unknown path")

        except BrokenPipeError:
            self._log("INFO", f"client disconnected ({self._elapsed(t0)})")
        except Exception:
            self._log("ERROR", f"unhandled exception:\n{traceback.format_exc()}")
            try:
                self.send_error(500)
            except Exception:
                pass

    @staticmethod
    def _elapsed(t0: float) -> str:
        ms = (time.monotonic() - t0) * 1000
        if ms < 1000:
            return f"{ms:.0f}ms"
        return f"{ms / 1000:.1f}s"


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    print(f"[iptv-simulator] Starting on {HOST}:{PORT} — BASE_URL={BASE_URL}", flush=True)
    server = ThreadingHTTPServer((HOST, PORT), Handler)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("[iptv-simulator] Shutting down.", flush=True)
        sys.exit(0)






