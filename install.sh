#!/bin/sh
# install.sh - install the memoria CLI on Linux or macOS.
#
# Download this file and read it before running it. Do not pipe it to a shell.
# See docs/cli.md for the steps, including how to check the attestation.
#
#   sh install.sh [--version vX.Y.Z] [--prefix DIR]
#
# The archive's SHA-256 is checked against the release's checksums.txt before
# anything is unpacked. All downloads are https only, redirects included.

set -eu

REPO="mggarofalo/memoria"
BINARY="memoria"

info() { printf '[memoria] %s\n' "$*" >&2; }
fail() {
    printf '[memoria] ERROR: %s\n' "$*" >&2
    exit 1
}

usage() {
    cat <<'USAGE'
Usage: install.sh [--version vX.Y.Z] [--prefix DIR]

  --version vX.Y.Z  Install this release instead of the latest
  --prefix DIR      Install into DIR (default: ~/.local/bin, or /usr/local/bin as root)
USAGE
}

need() {
    command -v "$1" >/dev/null 2>&1 || fail "required command not found: $1"
}

fetch() {
    curl --proto '=https' --proto-redir '=https' --tlsv1.2 -fsSL "$@"
}

valid_version() {
    case "$1" in
        *[!0-9A-Za-z.v-]*) return 1 ;;
    esac
    printf '%s\n' "$1" | grep -Eq '^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$'
}

VERSION=""
INSTALL_DIR=""
while [ $# -gt 0 ]; do
    case "$1" in
        --version)
            [ $# -ge 2 ] || fail "--version needs a value"
            VERSION="$2"
            shift 2
            ;;
        --version=*)
            VERSION="${1#--version=}"
            shift
            ;;
        --prefix)
            [ $# -ge 2 ] || fail "--prefix needs a value"
            INSTALL_DIR="$2"
            shift 2
            ;;
        --prefix=*)
            INSTALL_DIR="${1#--prefix=}"
            shift
            ;;
        --help | -h)
            usage
            exit 0
            ;;
        *) fail "unknown option: $1" ;;
    esac
done

case "$(uname -s)" in
    Linux*) OS="linux" ;;
    Darwin*) OS="darwin" ;;
    *) fail "unsupported operating system: $(uname -s). On Windows use install.ps1" ;;
esac
case "$(uname -m)" in
    x86_64 | amd64) ARCH="amd64" ;;
    aarch64 | arm64) ARCH="arm64" ;;
    *) fail "unsupported architecture: $(uname -m)" ;;
esac

need curl
need tar
if command -v sha256sum >/dev/null 2>&1; then
    SHA256="sha256sum"
elif command -v shasum >/dev/null 2>&1; then
    SHA256="shasum -a 256"
else
    fail "neither sha256sum nor shasum was found, so the download cannot be verified"
fi

if [ -z "$INSTALL_DIR" ]; then
    if [ "$(id -u)" = "0" ]; then
        INSTALL_DIR="/usr/local/bin"
    else
        INSTALL_DIR="${HOME}/.local/bin"
    fi
fi

if [ -z "$VERSION" ]; then
    info "Finding the latest release..."
    VERSION="$(fetch "https://api.github.com/repos/${REPO}/releases/latest" |
        tr -d '\n' | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')" ||
        fail "could not reach GitHub"
fi
valid_version "$VERSION" || fail "not a release version (expected vX.Y.Z)"

ARCHIVE="${BINARY}_${VERSION#v}_${OS}_${ARCH}.tar.gz"
BASE="https://github.com/${REPO}/releases/download/${VERSION}"

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

info "Downloading ${ARCHIVE}..."
fetch -o "${WORK_DIR}/${ARCHIVE}" "${BASE}/${ARCHIVE}" || fail "could not download ${ARCHIVE}"
fetch -o "${WORK_DIR}/checksums.txt" "${BASE}/checksums.txt" || fail "could not download checksums.txt"

# Match the name exactly: a substring match could find some other asset's line.
EXPECTED="$(awk -v name="$ARCHIVE" '$2 == name || $2 == "*" name { print $1; exit }' "${WORK_DIR}/checksums.txt")"
[ -n "$EXPECTED" ] || fail "${ARCHIVE} is not listed in checksums.txt"
ACTUAL="$(cd "$WORK_DIR" && $SHA256 "$ARCHIVE" | awk '{ print $1 }')"
[ "$EXPECTED" = "$ACTUAL" ] || fail "checksum mismatch for ${ARCHIVE}; nothing was installed"
info "Checksum verified."

# Extract the one expected member, so an archive cannot write anywhere else.
tar -xzf "${WORK_DIR}/${ARCHIVE}" -C "$WORK_DIR" "$BINARY" || fail "the archive has no ${BINARY} binary"
[ -f "${WORK_DIR}/${BINARY}" ] || fail "the archive has no ${BINARY} binary"

mkdir -p "$INSTALL_DIR"
# Copy beside the target and rename, so a running binary is replaced atomically.
STAGED="${INSTALL_DIR}/.${BINARY}.new.$$"
cp "${WORK_DIR}/${BINARY}" "$STAGED"
chmod 0755 "$STAGED"
mv -f "$STAGED" "${INSTALL_DIR}/${BINARY}"
info "Installed ${VERSION} to ${INSTALL_DIR}/${BINARY}"

case ":${PATH}:" in
    *":${INSTALL_DIR}:"*) ;;
    *) info "${INSTALL_DIR} is not on your PATH; add it to your shell profile." ;;
esac
info "Next: memoria auth login --api-url https://your-server"
