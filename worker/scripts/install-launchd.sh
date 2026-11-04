#!/usr/bin/env bash
# Install memoria-worker as a launchd agent, for a Mac with Metal.
#
#   MEMORIA_WORKER_TOKEN=... ./scripts/install-launchd.sh \
#     --llama-server /opt/homebrew/bin/llama-server --host 192.168.1.30
#
# Or pass --token-file <path> instead of the environment variable.
#
# A user agent rather than a daemon: it runs while you are logged in, under your
# account, so it finds your QMD cache and uses the GPU in your session.
#
# Requires Node 22.18+ and llama-server. The worker has no runtime npm
# dependencies and runs from source. The token is written to a mode 600 file in
# a mode 700 directory, and the worker reads it at startup through
# MEMORIA_WORKER_TOKEN_FILE. The plist holds the file's path, not the token.
#
# launchd does not rotate logs. The log directory is mode 700 and the agent's
# umask makes the logs mode 600. Prune them yourself, or add a newsyslog rule.
#
# --dry-run --root <dir> writes the files under <dir> in place of your home
# directory and loads nothing. It works on any host, which is how this script's
# output is tested without a Mac.
set -euo pipefail

# Set before anything is created, so no file or directory is ever briefly open to others.
umask 077

label="com.memoria.worker"
host="127.0.0.1"
port="8383"
llama_server=""
model_dir=""
log_dir=""
token_file_source=""
dry_run=0
root="${HOME:-}"

while [ $# -gt 0 ]; do
  case "$1" in
    --host) host="$2"; shift 2 ;;
    --port) port="$2"; shift 2 ;;
    --llama-server) llama_server="$2"; shift 2 ;;
    --model-dir) model_dir="$2"; shift 2 ;;
    --log-dir) log_dir="$2"; shift 2 ;;
    --token-file) token_file_source="$2"; shift 2 ;;
    --dry-run) dry_run=1; shift ;;
    --root) root="$2"; shift 2 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

if [ "$dry_run" -eq 0 ] && [ "$(uname -s)" != "Darwin" ]; then
  echo "This script is for macOS." >&2
  exit 1
fi
model_dir="${model_dir:-${root}/.cache/qmd/models}"
log_dir="${log_dir:-${root}/Library/Logs/memoria-worker}"

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
if [ -n "$token_file_source" ]; then
  if [ ! -f "$token_file_source" ]; then
    echo "The token file was not found at $token_file_source." >&2
    exit 1
  fi
  # Command substitution drops the trailing newlines.
  token="$(cat "$token_file_source")"
fi
case "$host" in
  127.*|localhost|::1) ;;
  *)
    if [ -z "$token" ]; then
      echo "A token is required when binding $host. Pass --token-file or set MEMORIA_WORKER_TOKEN." >&2
      exit 1
    fi
    ;;
esac
if [ -n "$token" ] && [ "${#token}" -lt 16 ]; then
  echo "The token must be at least 16 characters." >&2
  exit 1
fi

worker_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
agents_dir="${root}/Library/LaunchAgents"
plist="${agents_dir}/${label}.plist"
secret_dir="${root}/Library/Application Support/memoria-worker"
token_file="${secret_dir}/token"
mkdir -p "$agents_dir" "$log_dir" "$secret_dir"
# mkdir -p leaves an existing directory's mode alone.
chmod 700 "$log_dir" "$secret_dir"

xml() {
  printf '%s' "$1" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g'
}

# Written beside the target and moved into place, which also replaces a wider
# mode an earlier file had or a link planted at this name.
install_private() {
  local target="$1" temp
  temp="$(mktemp "${target}.XXXXXX")"
  cat > "$temp"
  chmod 600 "$temp"
  mv -f "$temp" "$target"
}

token_entry=""
if [ -n "$token" ]; then
  printf '%s\n' "$token" | install_private "$token_file"
  token_entry="    <key>MEMORIA_WORKER_TOKEN_FILE</key><string>$(xml "$token_file")</string>"
else
  rm -f "$token_file"
fi

# Umask 63 is 077 in decimal. It applies to the worker and to the log files launchd opens for it.
install_private "$plist" <<PLIST
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
  <key>Umask</key><integer>63</integer>
  <key>StandardOutPath</key><string>$(xml "${log_dir}/memoria-worker.out.log")</string>
  <key>StandardErrorPath</key><string>$(xml "${log_dir}/memoria-worker.err.log")</string>
</dict>
</plist>
PLIST

if [ "$dry_run" -eq 1 ]; then
  echo "Dry run: wrote ${plist} and loaded nothing."
  exit 0
fi

domain="gui/$(id -u)"
launchctl bootout "${domain}/${label}" 2>/dev/null || true
launchctl bootstrap "$domain" "$plist"
echo "Loaded ${label} on http://${host}:${port} (logs: ${log_dir})"
echo "Check it with: npm run smoke, with MEMORIA_WORKER_URL and MEMORIA_WORKER_TOKEN set"
