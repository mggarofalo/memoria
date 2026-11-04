#!/usr/bin/env bash
# Stop and remove the memoria-worker launchd agent and its token. Logs stay in place.
set -euo pipefail

label="com.memoria.worker"
launchctl bootout "gui/$(id -u)/${label}" 2>/dev/null || true
rm -f "${HOME}/Library/LaunchAgents/${label}.plist"
rm -rf "${HOME}/Library/Application Support/memoria-worker"
echo "Removed ${label}."
