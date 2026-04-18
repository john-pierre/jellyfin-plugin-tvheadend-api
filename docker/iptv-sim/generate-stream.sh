#!/bin/sh
# Generates minimal MPEG-TS test streams (~10s each) using ffmpeg.
# Run once during Docker image build to create static test content.

set -e

mkdir -p /srv/stream

for i in 1 2 3 4 5; do
  ffmpeg -y -f lavfi -i "testsrc=duration=10:size=320x240:rate=25" \
         -f lavfi -i "sine=frequency=$((300 + i * 100)):duration=10" \
         -vf "format=yuv420p" \
         -c:v libx264 -preset ultrafast -tune zerolatency \
         -c:a aac -b:a 64k \
         -f mpegts -mpegts_service_id "$i" \
         "/srv/stream/ch${i}.ts"
done

echo "All test streams generated."

