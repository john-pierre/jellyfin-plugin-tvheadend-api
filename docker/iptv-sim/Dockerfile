# IPTV Simulator — serves M3U, MPEG-TS streams, and XMLTV EPG over HTTP.
# Lightweight: Nginx for serving, ffmpeg only during build for stream generation.

FROM alpine:3.20 AS builder

RUN apk add --no-cache ffmpeg

COPY generate-stream.sh /tmp/generate-stream.sh
RUN chmod +x /tmp/generate-stream.sh && /tmp/generate-stream.sh

# ---

FROM nginx:alpine

# Copy generated streams from builder
COPY --from=builder /srv/stream /srv/stream

# Copy static assets
COPY playlist.m3u /srv/playlist.m3u
COPY nginx.conf /etc/nginx/nginx.conf
COPY generate-epg.sh /docker-entrypoint.d/50-generate-epg.sh

# Create a 1x1 transparent PNG as logo placeholder
RUN printf '\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x01\x00\x00\x00\x01\x08\x06\x00\x00\x00\x1f\x15\xc4\x89\x00\x00\x00\nIDATx\x9cc\x00\x01\x00\x00\x05\x00\x01\r\n\xb4\x00\x00\x00\x00IEND\xaeB`\x82' > /srv/logo.png

RUN chmod +x /docker-entrypoint.d/50-generate-epg.sh

EXPOSE 80

HEALTHCHECK --interval=5s --timeout=3s --retries=3 \
  CMD wget --spider -q http://iptv-sim:80/health || exit 1

