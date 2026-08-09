# IPTV Simulator — Python HTTP server serving M3U, live MPEG-TS streams,
# XMLTV EPG and dynamically generated channel logos/thumbnails.
#
# Streams are generated live by ffmpeg:
#   - Video: channel name + running wall-clock overlay on testsrc2
#   - Audio: 880 Hz tick for 50 ms every second

FROM python:3.12-alpine

# ffmpeg for live stream generation; DejaVu fonts for drawtext + Pillow
RUN apk add --no-cache ffmpeg ttf-dejavu && \
    pip install --no-cache-dir Pillow

COPY server.py /server.py

EXPOSE 80

HEALTHCHECK --interval=5s --timeout=3s --retries=5 \
    CMD python3 -c "import urllib.request; urllib.request.urlopen('http://localhost:80/health', timeout=2)" || exit 1

CMD ["python3", "-u", "/server.py"]
