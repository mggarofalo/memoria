# The memoria CLI

`memoria` is the command-line client for a Memoria server. It searches the corpus, fetches documents, and bridges MCP clients that can only start a local process.

The corpus holds agent transcripts, which routinely contain credentials, so an API key is worth protecting. This page says what the CLI protects and what it does not.

## Install

Install scripts verify the archive's SHA-256 against the release's `checksums.txt` before unpacking anything. Download the script from a tagged release, read it, then run it. Do not pipe it to a shell: a script you have not read could do anything, and a dropped connection can run half of one.

Linux and macOS:

```sh
curl --proto '=https' -fsSLO https://raw.githubusercontent.com/mggarofalo/memoria/v1.2.3/install.sh
less install.sh
sh install.sh --version v1.2.3
```

Windows:

```powershell
Invoke-WebRequest https://raw.githubusercontent.com/mggarofalo/memoria/v1.2.3/install.ps1 -OutFile install.ps1
Get-Content install.ps1
pwsh -File install.ps1 -Version v1.2.3
```

Replace `v1.2.3` with the release you want. The options are `--prefix DIR` and `-InstallDir DIR`. On Windows, `-NoPathUpdate` leaves your PATH alone.

### Without a script

Each release has these assets:

- `memoria_<version>_<os>_<arch>.tar.gz`, or `.zip` on Windows, where `<os>` is `linux`, `darwin` or `windows`, and `<arch>` is `amd64` or `arm64`.
- `checksums.txt`, in `sha256sum` format.

```sh
sha256sum --check --ignore-missing checksums.txt
gh attestation verify memoria_1.2.3_linux_amd64.tar.gz --repo mggarofalo/memoria
```

The checksum comes from the same release as the archive, so it catches corruption but not someone who can replace both files. The build provenance attestation is the independent check: it ties the archive to the workflow run that built it. `self-update` verifies it for you. The install scripts do not, so run `gh attestation verify` yourself on a first install.

## Update

```sh
memoria self-update --check            # report only
memoria self-update                    # newest stable release
memoria self-update --version v1.2.3   # a specific release
```

`self-update` is a small in-house updater that downloads over https only, redirects included. It unpacks to a randomly named temporary file, limited to 128 MiB, and keeps the old binary's mode. It runs the `version` check with a scrubbed environment, and only to prove the binary works: trust comes from the earlier checks. Before changing anything on disk it verifies the checksum and the build provenance. Then it swaps the binary and runs the new binary's `version` command. If that fails, the previous binary is put back.

Provenance is a GitHub attestation, verified with Sigstore. It must be signed by this repository's `release.yml` workflow for that exact tag, so an attacker who replaces both the archive and `checksums.txt` is caught, and an old signed build cannot pass as a newer release. It does not defend against a compromised release workflow, which can sign whatever it builds. Verifying it needs to reach GitHub and Sigstore's trust root at `tuf-repo-cdn.sigstore.dev`.

It refuses these unless you ask:

| Refusal | Flag |
|---|---|
| Replacing a development build (`dev`), or a binary a package manager or another user appears to own | `--force` |
| Installing an older version | `--allow-downgrade` |
| Installing a prerelease | `--prerelease` |
| Installing without verified provenance (trusts `checksums.txt` alone) | `--checksum-only` |

On Windows a running binary can be renamed but not deleted, so the old copy stays as a hidden `.memoria.exe.old` until the next update removes it.

## Log in

```sh
memoria auth login --api-url https://memoria.example.com
```

The key is read from a prompt with echo off, or from stdin when stdin is not a terminal. It is checked against the server and then stored. The server URL is saved in the profile.

```sh
memoria auth status    # where the key comes from, and whether the server accepts it
memoria auth logout    # remove the stored key; it stays valid on the server until revoked
```

Status output never shows any part of the key, not even a masked form.

### Where the key comes from

The first of these that has a key wins:

| Order | Source | Risk |
|---|---|---|
| 1 | `--api-key` | Lands in shell history, and is visible in process listings while the command runs. The CLI warns every time. |
| 2 | `MEMORIA_API_KEY` | Inherited by every child process. Readable by anything running as you. The CLI warns at a terminal. |
| 3 | OS keyring (login Keychain, Credential Manager, or the Secret Service on Linux) | Encrypted at rest by the OS. Any process running as you may still be able to ask for it, depending on the keyring. |
| 4 | Credentials file | Unencrypted on disk. See below. |

On macOS the keyring is the login Keychain, reached through `/usr/bin/security` so that the binary needs no cgo. The key goes to that tool on stdin, never as a process argument. The entry trusts `/usr/bin/security`, so any process running as you can read the key through that tool without a prompt. The Keychain still encrypts it at rest and locks it with your login keychain. From an SSH session a locked Keychain cannot prompt; run `security unlock-keychain` first.

The config file (`config.json`) never holds a key. It records the active profile, the server URL and which store the key is in. If you add a key to it by hand, the CLI ignores the key, warns you, and removes it on the next save.

The server URL comes from `--api-url`, then `MEMORIA_URL`, then the profile.

A key from the keyring or file is sent only to the server it was saved for. That origin is stored with the key, in the same secret, so replacing the config cannot redirect it. If `--api-url`, `MEMORIA_URL` or the config names another origin, the command refuses. Use a separate `--profile` for a second server.

`auth login` shows the server before it asks for the key. It ignores `MEMORIA_URL`. If the config has a saved URL, login uses it only when the keyring confirms that server; otherwise pass `--api-url`. The prompt for a URL appears only at a terminal with no saved URL. The credentials file cannot vouch for a URL, because it sits in the same directory as the config. On a machine with a keyring, a file login leaves a small record of its server there, which holds no key.

`auth logout` says which server each removed key was for. It keeps a keyring key for a different server than the config names, because a config can be replaced by whoever sets `XDG_CONFIG_HOME` or `HOME`. Pass `--include-keyring` to remove it anyway.

A command using the file backend always states which server the key is for, even with `--quiet`. It refuses if the keyring knows this profile by a different server, since a poisoned config directory looks exactly like that. It also refuses if the session bus was named but failed the checks below, since hiding the keyring is how a poisoned environment would avoid that comparison. Only a keyring that is genuinely absent counts as none. With nothing to compare, a poisoned directory holding its own credentials can't be detected: the attacker's key would go to the attacker's server, along with your search text. So check that line.

On Linux, the CLI talks to the Secret Service only over a session bus that is a `unix:path` socket owned by you, inside your runtime directory, and only if your own account holds the service's name. A `DBUS_SESSION_BUS_ADDRESS` naming anything else is refused, because whoever runs the bus would receive the key. A process already running as you can run its own bus and is outside this check, as it could read your key in other ways too.

Sessions on a `dbus-launch` abstract socket, or a `dbus-run-session` bus in `/tmp`, fail these checks, so they lose the keyring. To use the file backend there, unset `DBUS_SESSION_BUS_ADDRESS` for the command, since a refused bus stops file-backed commands too.

The CLI refuses a `config.json` or credentials file that other accounts can reach, that is a symlink or has a second hard link, or whose directory other accounts can reach.

### The file backend

For a host with no keyring, such as CI:

```sh
printf '%s' "$KEY" | memoria auth login --api-url https://memoria.example.com --backend file
```

A machine with no keyring does not fall back to the file on its own. You choose it, and you get a warning when you do.

**This is weaker than a keyring.** The key sits unencrypted in `credentials.json`, beside the config. There is no passphrase, because a passphrase stored next to the file protects nothing and a prompt cannot work unattended.

- **On Linux and macOS,** the file is mode 0600 in a 0700 directory. The CLI refuses to read a file that is group or world accessible, owned by someone else, or a symlink.
- **On Windows,** file modes mean nothing, so the CLI sets an explicit access list on both the file and the directory that grants only your account and does not inherit from the parent. It refuses a file that grants anyone else access, other than SYSTEM and Administrators.
- **Not protected, anywhere:** processes running as you, administrators and root, backups, and anyone with the disk.

The config directory is `$XDG_CONFIG_HOME/memoria`, or `~/.config/memoria`.

## Use it

```sh
memoria query "api key rotation" --collection claude-sessions --limit 5
memoria query retries --project Receipts --since 2026-01-01 --no-rerank
memoria get claude-sessions/2026-05-01.md --from-line 40 --max-lines 20
memoria multi-get f6feba claude-sessions/b.md
memoria multi-get --pattern 'claude-sessions/2026-05*.md' --format json
memoria collections
memoria status
```

`--format` takes `json`, `table` or `markdown`. `query`, `collections` and `status` default to a table; `get` and `multi-get` default to markdown.

**Table and markdown output is sanitized.** Document text comes from transcripts, which can hold terminal escape sequences that write your clipboard or rewrite your screen. The CLI strips control and format characters, escape sequences, carriage returns, bidirectional overrides and variation selectors from these formats, whether or not stdout is a terminal. In markdown, snippets and multi-get bodies are fenced so a document cannot forge a result heading. Titles and names are escaped so they cannot become images or links. Zero-width joiners are kept between letters of scripts that need them, such as Persian and Hindi, and removed everywhere else, including in emoji.

**`--format json` is the server's response, byte for byte, and is not sanitized.** Treat it as data, and do not print it raw to a terminal. There is one exception: if a response contains the key in use, even JSON-escaped, or the secret half of it alone, that text is replaced with `[REDACTED]`, because the key never reaches stdout. This cannot catch a key that has been split across strings or transformed in some other way.

## Manage keys

These need a key with the `admin` scope. The first one comes from running the server with `--bootstrap-admin-key`; see [deployment](deployment.md#the-first-admin-key).

```sh
memoria keys create --label laptop --scope read --collection claude-sessions
memoria keys create --label backup --scope read --all-collections
memoria keys list
memoria keys rotate <id>
memoria keys revoke <id>
```

`create` and `rotate` write the new key's plaintext to stdout, once, and everything else to stderr, so you can capture it: `KEY=$(memoria keys create ...)`. It can't be recovered later. With `--format json`, stdout is the server's response, which includes the plaintext.

A read or ingest key must name its collections. Reaching every collection, including any added later, takes `--all-collections`, and nothing falls back to it.

**Scopes do not imply one another.** An admin key manages keys but can't search, by design: a day-to-day client should never carry key-management power. To search, issue a separate read key and log in with it under its own profile:

```sh
memoria keys create --label reader --scope read --collection claude-sessions
memoria auth login --profile reader --api-url https://memoria.example.com
```

`revoke` refuses the key the command is using unless you pass `--force`, since that can lock you out. `rotate` on that key warns you to log in again with the new one.

## Sync local folders

`sources` keeps local folders of Markdown in the corpus. It needs a key with the `ingest` scope and the target collection in its list. The server redacts everything it stores.

```sh
memoria sources add notes --path ~/notes --collection notes
memoria sources list
memoria sources sync notes           # the first run only reports what it would send
memoria sources sync notes --apply   # sends, and from now on a plain sync sends too
```

- **The first run for a source on a server is a dry run.** It needs no key and no network, and it records nothing. `--apply` sends. `--dry-run` reports at any time.
- **Only what changed is sent.** Content is compared by hash, so touching a file sends nothing. The cursor lives in `sources-state/` beside the config, holds paths and hashes and no text, and is kept per server, so another server starts with its own first run.
- **It is safe to stop.** A run stops at the first sign the server cannot be reached, and at a `401` or `403`, and saves its place. Running it again continues, and sending a file twice does no harm. With nothing to send, it works offline and with no key. A server that asks a client to wait up to 2 minutes is waited for, and a longer wait stops the run with exit code 6.
- **A file the server refuses does not stop the others.** It is named on stderr, tried again next time, and the run exits 1. This happens for a file too large to store, or when the server holds a newer version or has deleted the document. A deleted document stays deleted unless the file changes afterward.
- **It never deletes anything on the server.** A file gone from the folder counts as `MISSING LOCALLY`, because an unmounted drive looks just like every file deleted. To remove a document, use `DELETE /v1/documents`.
- **Only Markdown is sent.** Files ending in `.md` are sent. Hidden files and folders are left out without comment. Links are never followed, and those ending in `.md` are named as left out, along with files that are not UTF-8 or are over 16 MiB.

A document's identity is the source's name and the file's path within the folder. Renaming a source or moving a file stores it again under a new identity, and the old copy stays until you delete it. The file's modification time orders versions, so a file edited on another machine with an older time is refused unless this machine's cursor has seen it.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Other failure |
| 2 | Bad usage, including a 400 from the server |
| 3 | No usable key, or the server said 401 or 403 |
| 4 | Not found, including a `multi-get` where any reference failed or nothing was returned |
| 5 | Server unavailable: 502, 503, 504, a timeout, or no connection |
| 6 | Rate limited (429). The message includes `Retry-After` when the server sends it |
| 7 | Refused for transport safety: plain http to a hostname or public address, a cross-origin redirect, a stored key aimed at another server, or a login whose server nothing confirms |

## Plain http

A bearer key sent over plain http can be read by anyone on the path.

| Server address | Behavior |
|---|---|
| https | Allowed. TLS 1.2 or later |
| Private IP literal (RFC 1918, ULA) | Allowed with a warning |
| Loopback, including `localhost` (which must resolve to a loopback address) | Allowed quietly |
| A hostname over http | Refused, unless you pass `--allow-insecure-http` or set `MEMORIA_ALLOW_INSECURE_HTTP=1` |
| Anything else over http | Refused, with the same override |

Link-local addresses count as public, and so do the cloud metadata addresses `169.254.169.254` and `fd00:ec2::254`. Hosts must be plain ASCII: use the `xn--` form of an internationalized name. Plain http never goes through a configured proxy, which would otherwise see the key.

A redirect to a different origin is never followed. A different origin includes a subdomain, another port, and a move from https to http. Go's HTTP client keeps the `Authorization` header in the subdomain and downgrade cases, so the CLI attaches the header itself, only to requests for the configured origin.

## MCP bridge

```sh
memoria mcp
```

For MCP clients that can only start a local process. It reads JSON-RPC from stdin, one message per line, forwards each to the server's `/mcp` endpoint with the stored key, and writes replies to stdout. Stdout carries protocol frames and nothing else; logs go to stderr.

Missing credentials and upstream failures come back as JSON-RPC errors rather than the process exiting, so the client can show why. The error's `data` has `kind`, `status`, `retryAfterSeconds` and `traceId`.

Messages from the client are limited to 4 MiB (`--max-message-bytes`), and frames from the server to 32 MiB (`--max-response-bytes`).

## Development

Commands live in `cli/cmd`, one file each, as a `newXCmd(*app)` factory listed in `commandFactories`. New commands get credentials, the guarded HTTP client, output formats and exit codes from `app`. `internal/transport` is the only code that puts a key on the wire.

Release builds are cgo-free on every platform. Stamp the version with `-ldflags "-X github.com/mggarofalo/memoria/cli/cmd.Version=<version>"`.

`go test ./...` covers the install scripts when `sh` and `pwsh` are available, and skips them otherwise. `MEMORIA_TEST_REAL_KEYRING=1 go test ./internal/auth` also exercises the real OS keyring, under a test-only service name and with a throwaway value.
