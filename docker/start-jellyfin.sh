#!/bin/sh
set -eu

PLUGIN_SOURCE_ROOT="/opt/tvheadend-plugin"
PLUGIN_TARGET_ROOT="/config/plugins"

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

exec /jellyfin/jellyfin
