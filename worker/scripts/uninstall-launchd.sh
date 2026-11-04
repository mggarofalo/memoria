#!/usr/bin/env bash
# Stop and remove the memoria-worker launchd agent. Logs stay in place.
set -euo pipefail

label="com.memoria.worker"
launchctl bootout "gui/$(id -u)/${label}" 2>/dev/null || true
rm -f "${HOME}/Library/LaunchAgents/${label}.plist"
echo "Removed ${label}."
