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
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse

from PIL import Image, ImageDraw, ImageFont

HOST = "0.0.0.0"
PORT = int(os.environ.get("PORT", "80"))
BASE_URL = os.environ.get("BASE_URL", "http://iptv-sim")
FONT_PATH = "/usr/share/fonts/ttf-dejavu/DejaVuSans-Bold.ttf"

CHANNELS = [
    {"id": "test-ch1", "name": "Test Channel 1", "group": "News",          "color": (180, 30,  30)},
    {"id": "test-ch2", "name": "Test Channel 2", "group": "Entertainment", "color": (30,  100, 180)},
    {"id": "test-ch3", "name": "Test Channel 3", "group": "Sports",        "color": (30,  150, 60)},
    {"id": "test-ch4", "name": "Test Channel 4", "group": "Documentary",   "color": (160, 90,  0)},
    {"id": "test-ch5", "name": "Test Channel 5", "group": "Music",         "color": (120, 30,  160)},
]


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
    num = channel["id"][-1]
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
    num = channel["id"][-1]
    bb = draw.textbbox((0, 0), num, badge_font)
    bw, bh = bb[2] - bb[0] + 16, bb[3] - bb[1] + 10
    draw.rounded_rectangle([320 - bw - 10, 10, 320 - 10, 10 + bh], radius=6,
                           fill=(255, 255, 255, 200))
    draw.text((320 - bw - 10 + 8 - bb[0], 10 + 5 - bb[1]), num,
              fill=(r, g, b), font=badge_font)

    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return buf.getvalue()


# ---------------------------------------------------------------------------
# Playlist & EPG generators
# ---------------------------------------------------------------------------

def make_playlist() -> str:
    lines = ["#EXTM3U"]
    for ch in CHANNELS:
        n = ch["id"][-1]
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
        '<tv source-info-name="IPTV Simulator" generator-info-name="iptv-sim">',
    ]

    for ch in CHANNELS:
        n = ch["id"][-1]
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
    n = channel["id"][-1]
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
        f":text='%{{localtime\\:%H\\:%M\\:%S}}'"
        f":fontsize=96:fontcolor=yellow:shadowcolor=black:shadowx=4:shadowy=4"
        f":x=(w-text_w)/2:y=240"
    )

    # Tick audio: 50 ms burst of 880 Hz sine, repeated every second
    audio_expr = "0.4*sin(2*PI*880*t)*(lt(mod(t\\,1)\\,0.05))"

    cmd = [
        "ffmpeg", "-hide_banner", "-loglevel", "error",
        "-re",
        "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=25",
        "-f", "lavfi", "-i", f"aevalsrc={audio_expr}:s=44100",
        "-vf", vf,
        "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
        "-g", "50", "-keyint_min", "50",
        "-c:a", "aac", "-b:a", "64k", "-ar", "44100",
        "-f", "mpegts", "pipe:1",
    ]

    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    try:
        while True:
            chunk = proc.stdout.read(65536)
            if not chunk:
                break
            wfile.write(chunk)
            wfile.flush()
    except (BrokenPipeError, ConnectionResetError):
        pass
    finally:
        proc.kill()
        proc.wait()


# ---------------------------------------------------------------------------
# HTTP handler
# ---------------------------------------------------------------------------

class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):  # noqa: D102
        print(f"[iptv-sim] {self.address_string()} — {fmt % args}", flush=True)

    def _send(self, code: int, content_type: str, body: bytes) -> None:
        self.send_response(code)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):  # noqa: N802
        path = urlparse(self.path).path

        if path == "/health":
            self._send(200, "text/plain", b"OK")

        elif path == "/playlist.m3u":
            self._send(200, "audio/x-mpegurl", make_playlist().encode())

        elif path == "/epg.xml":
            self._send(200, "application/xml", make_epg().encode())

        elif m := re.match(r"^/logo(\d+)\.png$", path):
            n = int(m.group(1))
            if 1 <= n <= len(CHANNELS):
                self._send(200, "image/png", make_logo(CHANNELS[n - 1]))
            else:
                self.send_error(404)

        elif m := re.match(r"^/thumb/(test-ch\d)/(\d+)\.png$", path):
            ch = next((c for c in CHANNELS if c["id"] == m.group(1)), None)
            if ch:
                self._send(200, "image/png", make_thumb(ch, int(m.group(2)) % 24))
            else:
                self.send_error(404)

        elif m := re.match(r"^/stream/ch(\d+)\.ts$", path):
            n = int(m.group(1))
            if 1 <= n <= len(CHANNELS):
                self.send_response(200)
                self.send_header("Content-Type", "video/mp2t")
                self.send_header("Transfer-Encoding", "chunked")
                self.end_headers()
                stream_channel(CHANNELS[n - 1], self.wfile)
            else:
                self.send_error(404)

        else:
            self.send_error(404)


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    print(f"[iptv-sim] Starting on {HOST}:{PORT} — BASE_URL={BASE_URL}", flush=True)
    server = ThreadingHTTPServer((HOST, PORT), Handler)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("[iptv-sim] Shutting down.", flush=True)
        sys.exit(0)

