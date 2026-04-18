#!/bin/sh
# Generates a 24-hour XMLTV EPG file for 5 test channels.
# Runs at container startup to produce current-day timestamps.

set -e

OUTPUT="/srv/epg.xml"
TODAY=$(date -u +%Y%m%d)
TOMORROW=$(date -u -d "@$(($(date -u +%s) + 86400))" +%Y%m%d)

cat > "$OUTPUT" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE tv SYSTEM "xmltv.dtd">
<tv source-info-name="IPTV Simulator" generator-info-name="iptv-sim">
EOF

# Channel definitions
for i in 1 2 3 4 5; do
  cat >> "$OUTPUT" <<EOF
  <channel id="test-ch${i}">
    <display-name>Test Channel ${i}</display-name>
    <icon src="http://iptv-sim/logo${i}.png"/>
  </channel>
EOF
done

# Programme entries: 1-hour slots for 24 hours per channel
GENRES="News Entertainment Sports Documentary Music"
for i in 1 2 3 4 5; do
  GENRE=$(echo "$GENRES" | cut -d' ' -f"$i")
  for h in $(seq 0 23); do
    HOUR=$(printf "%02d" "$h")
    NEXT_HOUR=$(printf "%02d" $(( (h + 1) % 24 )))
    if [ "$h" -eq 23 ]; then
      END_DATE="$TOMORROW"
    else
      END_DATE="$TODAY"
    fi
    cat >> "$OUTPUT" <<EOF
  <programme start="${TODAY}${HOUR}0000 +0000" stop="${END_DATE}${NEXT_HOUR}0000 +0000" channel="test-ch${i}">
    <title lang="en">${GENRE} Show ${HOUR}:00</title>
    <desc lang="en">Test programme on channel ${i} at ${HOUR}:00 UTC. Genre: ${GENRE}.</desc>
    <category lang="en">${GENRE}</category>
  </programme>
EOF
  done
done

echo "</tv>" >> "$OUTPUT"
echo "EPG generated: $OUTPUT"

