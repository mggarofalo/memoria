#!/usr/bin/env bash
# Install memoria-worker as a launchd agent, for a Mac with Metal.
#
#   MEMORIA_WORKER_TOKEN=... ./scripts/install-launchd.sh \
#     --llama-server /opt/homebrew/bin/llama-server --host 192.168.1.30
#
# A user agent rather than a daemon: it runs while you are logged in, under your
# account, so it finds your QMD cache and uses the GPU in your session.
#
# Requires Node 22.18+ and llama-server. The worker has no runtime npm
# dependencies and runs from source. The token ends up in the agent's plist,
# which this script makes readable by you alone.
set -euo pipefail

label="com.memoria.worker"
host="127.0.0.1"
port="8383"
llama_server=""
model_dir="${HOME}/.cache/qmd/models"
log_dir="${HOME}/Library/Logs/memoria-worker"

while [ $# -gt 0 ]; do
  case "$1" in
    --host) host="$2"; shift 2 ;;
    --port) port="$2"; shift 2 ;;
    --llama-server) llama_server="$2"; shift 2 ;;
    --model-dir) model_dir="$2"; shift 2 ;;
    --log-dir) log_dir="$2"; shift 2 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

if [ "$(uname -s)" != "Darwin" ]; then
  echo "This script is for macOS." >&2
  exit 1
fi
if [ -z "$llama_server" ]; then
  llama_server="$(command -v llama-server || true)"
fi
if [ -z "$llama_server" ] || [ ! -x "$llama_server" ]; then
  echo "llama-server was not found. Pass --llama-server /path/to/llama-server." >&2
  exit 1
fi
node_bin="$(command -v node || true)"
if [ -z "$node_bin" ]; then
  echo "node is not on PATH." >&2
  exit 1
fi
token="${MEMORIA_WORKER_TOKEN:-}"
case "$host" in
  127.*|localhost|::1) ;;
  *)
    if [ -z "$token" ]; then
      echo "MEMORIA_WORKER_TOKEN is required when binding $host." >&2
      exit 1
    fi
    ;;
esac

worker_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
agents_dir="${HOME}/Library/LaunchAgents"
plist="${agents_dir}/${label}.plist"
mkdir -p "$agents_dir" "$log_dir"

xml() {
  printf '%s' "$1" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g'
}

token_entry=""
if [ -n "$token" ]; then
  token_entry="    <key>MEMORIA_WORKER_TOKEN</key><string>$(xml "$token")</string>"
fi

# Created unreadable first, so the token is never briefly visible to others.
umask 077
cat > "$plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>${label}</string>
  <key>ProgramArguments</key>
  <array>
    <string>$(xml "$node_bin")</string>
    <string>$(xml "${worker_dir}/src/main.ts")</string>
  </array>
  <key>WorkingDirectory</key><string>$(xml "$worker_dir")</string>
  <key>EnvironmentVariables</key>
  <dict>
    <key>MEMORIA_WORKER_HOST</key><string>$(xml "$host")</string>
    <key>MEMORIA_WORKER_PORT</key><string>$(xml "$port")</string>
    <key>MEMORIA_WORKER_LLAMA_SERVER</key><string>$(xml "$llama_server")</string>
    <key>MEMORIA_WORKER_MODEL_DIR</key><string>$(xml "$model_dir")</string>
${token_entry}
  </dict>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>ThrottleInterval</key><integer>5</integer>
  <key>ExitTimeOut</key><integer>15</integer>
  <key>ProcessType</key><string>Background</string>
  <key>StandardOutPath</key><string>$(xml "${log_dir}/memoria-worker.out.log")</string>
  <key>StandardErrorPath</key><string>$(xml "${log_dir}/memoria-worker.err.log")</string>
</dict>
</plist>
PLIST
chmod 600 "$plist"

domain="gui/$(id -u)"
launchctl bootout "${domain}/${label}" 2>/dev/null || true
launchctl bootstrap "$domain" "$plist"
echo "Loaded ${label} on http://${host}:${port} (logs: ${log_dir})"
echo "Check it with: npm run smoke, with MEMORIA_WORKER_URL and MEMORIA_WORKER_TOKEN set"
