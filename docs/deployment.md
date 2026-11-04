# Deploying Memoria

Two processes, two hosts, one ingress.

```
laptop / desktop / phone / cloud agents
        │  HTTP (LAN today; HTTPS via the reverse proxy later)
        ▼
  Memoria.Api         Docker, ghcr.io/mggarofalo/memoria, port 8080
        │  plain REST + shared-secret bearer, LAN only
        ▼
  memoria-qmd         Node service beside the GGUF models and index.sqlite
        │
        ▼
  QMD index + markdown corpus
```

## Decision: the image holds the API only

The epic left open whether to ship the API and QMD in one image or compose
them. The image holds `Memoria.Api` alone, and the sidecar runs as its own
service on whichever machine owns the models and index:

- The sidecar needs the GGUF models (about 4 GB resident) and today a GPU.
  That machine is a Windows desktop; the API host is Linux.
- The two move independently. The sidecar is heading to the always-on node
  with the index while model work moves to GPU workers; the API stays put. A
  combined image would have to be rebuilt and redeployed for either.
- The sidecar is never a public surface. Keeping it out of the API's
  container makes "QMD is reachable only through the API" a network fact,
  not a configuration one.

The sidecar runs under NSSM on Windows (`sidecar/scripts/install-service.ps1`)
or as a systemd unit on Linux (`node dist/main.js`). See `sidecar/README.md`.
Install it from its release package; see
[releasing.md](releasing.md#installing-the-sidecar-and-worker).

## Running as a service

`sidecar/scripts/install-service.ps1` and `worker/scripts/install-service.ps1`
share `deploy/windows/MemoriaService.psm1`. **Neither has been run on a real
install.** Both accept `-WhatIf`, which prints every step, needs no elevation
and changes nothing.

| What | Where | Who can use it |
|---|---|---|
| Code, `node.exe`, `nssm.exe` | `%ProgramFiles%\Memoria\<service>` | Only administrators can change it. The script checks this and stops otherwise. |
| Secret | `%ProgramData%\<service>\secret\` | Administrators and the service account only, with inheritance off. The script checks this before starting. |
| Logs | `%ProgramData%\<service>\logs` | Administrators and the service account only. |
| Service identity | `NT SERVICE\<service>` | A virtual account, with no password to manage. Not LocalSystem. |

**Why this shape.** A service running as LocalSystem from a tree a user can write
to hands SYSTEM to any process running as that user at the next restart. A secret
passed through `nssm set AppEnvironmentExtra` appears on a command line and stays
in the registry under the service's key, which local users can generally read.

**What the account can reach.** Grants are added to the paths you name and
nothing else:

| Service | Path | Access | Why |
|---|---|---|---|
| sidecar | `<CacheHome>\qmd`, by default `%USERPROFILE%\.cache\qmd` | Change | The index and its write-ahead log, and the models. |
| sidecar | `<ConfigHome>\qmd`, by default `%USERPROFILE%\.config\qmd` | Read | QMD's collection config. |
| sidecar | each `-CollectionRoot` | Change | `POST` and `DELETE /documents`. Pass all of them; nothing finds them for you. |
| worker | `-ModelDir`, by default `%USERPROFILE%\.cache\qmd\models` | Read | The models. |
| worker | the directory holding `llama-server` | Read | The binary and its DLLs. Refused if a non-administrator can change it. |

The defaults leave data in your own profile, so a process running as you can
still change the models and the index. That cannot reach SYSTEM, but a model file
is parsed by `llama-server` under the service account, so moving the models and
index under `%ProgramData%` is the stronger setup. Pass `-CacheHome` and
`-ModelDir` to do that after copying them.

**Log rotation.** `AppRotateFiles` and `AppRotateOnline` are on with
`AppRotateBytes` at 10 MiB, so a log rotates at that size while the service runs,
as well as at start. Per NSSM's documentation, rotated files are renamed with a
timestamp, online rotation ignores any age limit, and a rename happens only after
the application's next line of output. The documentation describes no deletion of
old files, so plan on pruning them yourself.

**Upgrading an earlier install.** The script replaces the registry environment,
which removes a secret stored there, and moves the service off LocalSystem. Treat
that secret as exposed and rotate it: choose a new one and update
`Memoria__Sidecar__Secret` on the API host. The old install directory is not
touched. Its log and data locations are the ones above unless you passed others.

**NSSM version.** 2.24, the last stable release, fails to start services on
Windows 10 Creators Update or newer, according to nssm.cc, which recommends
2.24-101 or later. The script warns about 2.24. `winget`'s version was not checked.

**macOS.** `worker/scripts/install-launchd.sh` writes the token to a mode 600 file
in a mode 700 directory, and the plist (also mode 600) holds the path. The agent
runs as you, so there is no privilege boundary to design around. launchd does not
rotate logs.

### Not yet confirmed

These need a real install, and none has been done:

- `sc.exe config <service> obj= "NT SERVICE\<service>"` on an NSSM-registered
  service, and NSSM starting its child and writing and rotating logs under that
  account. NSSM's documentation describes `ObjectName` as needing a password and
  does not mention virtual accounts, though a commit in its repository appears to
  add them, which is why the script uses `sc.exe`.
- **GPU access from the virtual account.** CUDA for `llama-server`, and the
  sidecar's in-process `node-llama-cpp` in local mode, have only ever been used in
  an interactive session. `nvidia-smi` under the account is also unproven. If it
  fails, this is the one thing that may force a different account.
- Reading the models and index through a user profile. The account has no rights on
  `C:\Users\<you>` itself and relies on the default "bypass traverse checking"
  right. Anything that walks the parent directories could fail, which `-CacheHome`
  and `-ModelDir` under `%ProgramData%` would avoid.
- That the account's own profile, `C:\Windows\ServiceProfiles\<service>`, is created
  and writable, since `node-llama-cpp` and temp files may use it.
- That `npm ci --omit=dev` under Program Files produces a working native build, and
  that nothing tries to write beside it at runtime.
- The `Log on as a service` right, which virtual accounts normally hold through
  `NT SERVICE\ALL SERVICES`. A group policy can remove it.
- The launchd script on a Mac: `launchctl bootstrap` with the mode 600 plist, the
  `Umask` key's effect on the log files, and the worker reading the token file.

## Collection root permissions

**A local user with write access to a collection root is outside the threat these
checks defend against.** Restrict each root so only the sidecar's account and the
people who administer it can write there, and do not point a collection at a
directory other accounts share.

The sidecar's path checks stop a remote caller from leaving the root, and they
narrow what a local user can do, but they cannot finish the job. Node has no
`openat` or `renameat`, so every filesystem call resolves its path again, and
someone who can swap a directory for a junction can still race those calls. Such a
user can:

- win the gap inside a rename and overwrite a file outside the root, if the
  sidecar's account can write to it;
- leave an empty file outside the root;
- make a delete rename an outside file to a `.memoria-*.tmp` name beside its old
  one, and a later write in that directory may then remove it if it is over an hour
  old and another swap lands at the right moment.

The sidecar's account limits the damage, which is one more reason not to run it
as LocalSystem. `sidecar/README.md` lists the rules a document path must meet.

## The image

`ghcr.io/mggarofalo/memoria` is published by `.github/workflows/docker.yml`.
Every pull request builds it and publishes nothing. `/health` reports the version
the image was stamped with; see [releasing.md](releasing.md).

| Tag | Published by | Moves? |
|---|---|---|
| `<major>.<minor>.<patch>` | a release tag | no |
| `<major>.<minor>` | a release tag | to the newest patch of that minor |
| `latest` | a release tag | to the newest release |
| `edge` | every push to `main` | to the head of `main`, unreleased |
| `sha-<short>` | every push to `main` | no |

Deploy a specific release. `latest` is there for looking around, and `edge` is
for trying unreleased work; neither says what is running.

| Property | Value |
|---|---|
| Base | `mcr.microsoft.com/dotnet/aspnet:10.0`, non-root (`$APP_UID`) |
| Port | 8080 (`ASPNETCORE_URLS=http://0.0.0.0:8080`) |
| Health check | `dotnet Memoria.Api.dll --health-check` probes `/health` |
| Logs | JSON lines on stdout, one object per line |
| Configuration | environment variables; see [configuration.md](configuration.md) |

Build it locally:

```bash
docker build -t memoria-api:local .
docker run --rm -p 8080:8080 \
  -e Memoria__Sidecar__BaseUrl=http://host.docker.internal:8282 \
  -e Memoria__Sidecar__Secret="$MEMORIA_QMD_SECRET" \
  -v memoria-data:/data \
  memoria-api:local
```

The image sets `Memoria__Auth__DatabasePath=/data/control.db`. Without a volume on
`/data` the database dies with the container, and every key with it.

## Compose on the homelab

[`deploy/compose.yaml`](../deploy/compose.yaml) is the production shape:

```bash
MEMORIA_VERSION=0.1.0 \
MEMORIA_SIDECAR_URL=http://192.168.1.20:8282 \
MEMORIA_SIDECAR_SECRET=... \
docker compose -f deploy/compose.yaml up -d
```

`MEMORIA_VERSION` has no default, so an upgrade happens only when you change it.

It publishes port 8080 on `127.0.0.1` by default so only the reverse proxy on
the same host can reach it. Set `MEMORIA_BIND_ADDRESS` to the host's LAN
address to let LAN clients connect directly while the proxy entry does not
exist yet.

Containers reach the sidecar on the desktop over the LAN with no special
Docker networking: bridge networks NAT outbound traffic.

### The control database volume

API keys and the audit log live in a SQLite file at `/data/control.db`, on the
`memoria-data` named volume the compose file declares. **The volume is required.**
Unlike QMD's index, this is durable state that cannot be rebuilt: losing it revokes
every key and loses the audit trail. Keep it on a local disk, because SQLite's WAL
mode does not work over a network share, and include the volume in backups.

### The first admin key

Nothing can call the API until a key exists. Create the first one with a one-off
container on the same volume. It prints the key once on stdout, and exits without
starting a host:

```bash
docker compose -f deploy/compose.yaml run --rm --no-deps memoria-api \
  --bootstrap-admin-key homelab-admin
```

The key is never stored in plaintext and cannot be shown again, so copy it into
your password manager now. The command refuses while an active admin key exists;
pass `--allow-additional-admin` after the label to add another deliberately. If you
lose the only admin key, run the command with that flag, then revoke the lost key
with the new one.

This key has only the `admin` scope and cannot read the corpus. Issue clients their
own scoped keys with it; see [configuration.md](configuration.md#memoriaauth--api-keys-rate-limits-audit).

### Required environment

| Variable | Purpose |
|---|---|
| `MEMORIA_VERSION` | Compose only: the image tag to run, e.g. `0.1.0`. |
| `Memoria__Sidecar__BaseUrl` | Where the sidecar listens, e.g. `http://192.168.1.20:8282`. |
| `Memoria__Sidecar__Secret` | The sidecar's `MEMORIA_QMD_SECRET`. The API refuses to start without it when the sidecar is not on loopback. |
| `Memoria__Auth__DatabasePath` | Where the control database lives. The image sets it to `/data/control.db`. The API refuses to start without it unless `Memoria__Auth__Mode` is explicitly `Disabled`. |

Everything else has a default. Pass secrets through the environment or a
Docker secret; never bake them into the image or commit them.

## Reverse proxy and TLS

`nginx-proxy-manager` already terminates TLS on the homelab with Let's Encrypt
certificates, and `cloudflare-ddns` keeps the dynamic IP current. Memoria
needs one proxy host entry:

| Setting | Value |
|---|---|
| Domain | your chosen hostname |
| Scheme / forward | `http` to `memoria-api:8080` (same compose network) or `127.0.0.1:8080` |
| SSL | Let's Encrypt, Force SSL, HTTP/2 |
| Websockets | not needed; MCP uses Streamable HTTP (POST + SSE responses) |
| Custom config | `proxy_read_timeout 180s;` — reranked searches take seconds, a cold sidecar longer |

`X-Trace-Id` on every response, and `traceId` in every problem-details body,
are the ids to grep for in the API's and the sidecar's logs.

Rate limits and the audit trail key on the API key, so they need nothing from the
proxy. The proxy must terminate TLS: a key on plain HTTP is readable by anyone on
the path. Do not log the `Authorization` header in the proxy's access log.

`/ready` stays anonymous so an orchestrator can probe it, and tells anonymous
callers only `ready` or `not_ready`. See
[configuration.md](configuration.md#anonymous-routes).

## Exposure is gated

Nothing in this repository exposes Memoria to the internet, and nothing
should until **homelab hardening** (patching, firewall review) lands: the host
epic's "patch homelab before public exposure" issue. API keys with collection
scopes are enforced by default, but a key is only as private as the transport,
so TLS through the proxy is part of that gate. `Memoria:Auth:Mode = Disabled` is
for a trusted LAN and must never be set on an exposed host.

Until hardening is done: keep the compose port on `127.0.0.1` or the LAN address,
do not create the proxy host entry, and do not forward a router port.

## Deployment checklist

Run these from the commit you are about to deploy, before starting or
restarting the sidecar or a worker:

```bash
(cd sidecar && npm ci && npm audit --omit=dev --audit-level=high)
(cd worker && npm audit --omit=dev --audit-level=high)
```

Both must exit 0. A critical or high finding stops the deployment until it is
fixed, or until `sidecar/README.md` records it as an accepted risk with an
argument for why it cannot be reached. Do not silence a finding with an audit
exclusion. Findings appear without any change to this repository, because new
advisories are published against versions already in the lockfile, so a result
from an earlier deployment does not carry over.

The audit reads the lockfile, so it covers what `npm ci` installs and nothing
else. It says nothing about a `qmd` CLI installed globally on the host, which
is one more reason never to serve QMD's HTTP transport from one.
`sidecar/README.md` lists the current overrides and what to do with them on a
QMD bump.

Then check the network boundary, below.

## The loopback invariant

QMD itself is never reachable from anywhere. The sidecar is the only process
that opens the index; it binds the model host's LAN address, requires the
shared secret on every route except `/health`, and compares it in constant
time. `Memoria.Api` is the only intended caller. If any path exists from a
client to the sidecar that does not pass through `Memoria.Api`, authentication
has been bypassed entirely. When auditing the deployment, check that:

- the sidecar port (8282) is reachable from the API host and from nowhere
  outside the LAN;
- the API host's firewall allows 8080 only from the proxy (or the LAN until the
  proxy entry exists);
- the sidecar's `MEMORIA_QMD_HOST` is the LAN address, never `0.0.0.0` on a
  machine with a public interface.
