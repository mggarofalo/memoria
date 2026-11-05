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
or as a systemd unit on Linux (`node --max-old-space-size=4096 dist/main.js`). See `sidecar/README.md`.
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
  -e Memoria__Ingest__DigestKey="$MEMORIA_INGEST_DIGEST_KEY" \
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
MEMORIA_INGEST_DIGEST_KEY=... \
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
| `Memoria__Ingest__DigestKey` | At least 32 random characters, kept outside the database. Compose reads it from `MEMORIA_INGEST_DIGEST_KEY`. The API refuses to start without it unless `Memoria__Auth__Mode` is explicitly `Disabled`. See [configuration](configuration.md#digests-in-the-ledger) for how to change it. |

Everything else has a default. Pass secrets through the environment or a
Docker secret; never bake them into the image or commit them.

## Nothing may sit between the API and the sidecar

Ingest runs with the API and the sidecar on one Linux host, as two compose services on one bridge network, with the API calling the sidecar by service name and **no published sidecar port**. Run one API instance.

The rule: **nothing may sit between the API and the sidecar.** No reverse proxy, no TLS terminator, no load balancer, no `docker-proxy` from a published port, and no gateway that buffers a request or answers for the sidecar. The reason is that a write is tied to its connection. If the API dies or gives up on a write, its connection closes, and the sidecar drops the write just before it would land. Anything holding its own connection to the sidecar keeps that write alive after the API has gone, and anything that answers 502 and forwards anyway makes the API take a write that may land for one that did not. Then a write can land after a delete has been answered, and only repeating the delete clears it.

Two API instances have the same effect, as the locks that order writes and deletes are in the API's memory.

Reaching the sidecar across a network, as the tunnel below does, still serves search. It weakens ingest: a lost tunnel or a host crash closes nothing until a TCP timeout, so the sidecar cannot tell its caller has gone. On Linux a connection cut off that way still looked open after 75 s.

## Limits accepted for this deployment

- **Run the API with at least a 1 GiB heap** (`DOTNET_GCHeapHardLimit`). Under less, large uploads are refused more often, and running out of memory can kill the process.
- **A write already past its connection check can land after a delete.** The rename follows the check in about 0.1 ms, and at most 69 ms when measured. Repeating the delete clears it.
- **A mount that disappears after a collection opened is not noticed.** A missing directory will not open, but an empty directory that is really an unmounted volume cannot be told from a new one. If that empty directory opens the collection, or an empty remount follows opening, search stops returning the documents, documents written then stay in the other mount, and ingest overwrites or deletes whatever is there once the real volume returns. Mount the volume before first use.
- **Restoring the control database forgets deletes made since the backup.** Deleted content can be sent again and stored. A delete of a newer file still works.
- **A sidecar crash can leave `.memoria-*.tmp` files holding text,** until a later write in that folder removes them after an hour. They are not indexed, but they may hold secret text, so a credential cleanup should check for them.

## Traffic between hosts: WireGuard

Traffic between the API, the sidecar and workers runs over a WireGuard tunnel. The services speak plain http on tunnel addresses, and the tunnel does the encrypting, including of the shared secret and whole transcripts.

The addresses here are examples:

| Host | Tunnel address | Serves | Port |
|---|---|---|---|
| API | `10.0.0.1` | | |
| Sidecar | `10.0.0.2` | `Memoria.Api` | 8282 |
| Worker | `10.0.0.3` | the sidecar | 8383 |

**Bind each service to its tunnel address, and tell it this is intended.** Nothing detects a tunnel, so the setting is what states it. Without it, a non-loopback bind or a plain http worker URL on another machine stops the start, with a message naming the setting.

```sh
# Sidecar. The install script's -AllowHttpOnPrivateNetwork sets the first of these.
MEMORIA_QMD_ALLOW_HTTP_ON_PRIVATE_NETWORK=1
MEMORIA_QMD_HOST=10.0.0.2
MEMORIA_QMD_WORKER_URL=http://10.0.0.3:8383
MEMORIA_QMD_API_ADDRESSES=10.0.0.1

# Worker. Its installers take -AllowHttpOnPrivateNetwork or --allow-http-on-private-network.
MEMORIA_WORKER_ALLOW_HTTP_ON_PRIVATE_NETWORK=1
MEMORIA_WORKER_HOST=10.0.0.3

# API
Memoria__Sidecar__BaseUrl=http://10.0.0.2:8282
```

**Firewall each port to the tunnel,** so nothing reaches it from the LAN even by mistake. For example:

```sh
ufw allow in on wg0 to any port 8282 proto tcp
```

```powershell
New-NetFirewallRule -DisplayName "memoria-qmd" -Direction Inbound -Protocol TCP -LocalPort 8282 -LocalAddress 10.0.0.2 -RemoteAddress 10.0.0.1 -Action Allow
```

**Start order.** The tunnel must be up before a service binds its address, or the service fails to start. Make the service depend on the tunnel, or let it restart until the address exists.

**Upgrading a LAN install:** it stops starting until the setting is added. Move it onto the tunnel first, because the setting does not encrypt anything.

TLS is not the chosen setup.

**Host names.** A request must arrive under an IP address, `localhost`, or a name in `MEMORIA_QMD_ALLOWED_HOSTS`, and any request carrying an `Origin` is refused. That stops a web page reaching the service through DNS rebinding. Tunnel addresses need no listing. A deployment calling by name, such as `host.docker.internal`, must list it or gets a 421 that says so. The worker has the same rule, with `MEMORIA_WORKER_ALLOWED_HOSTS`.

**Secrets.** With no secret set, each run makes its own and nothing else can call it. Placeholders, the old README example and repeated characters are refused.

## Memory and large documents

Run the sidecar with `--max-old-space-size=4096`. The install script sets it; elsewhere use `node --max-old-space-size=4096 dist/main.js`. This is a ceiling, not a target. Node's default on a smaller host can be lower, and a search that reaches the limit kills the process.

`sidecar/scripts/patch-qmd.ts` runs on install and fixes the SDK's scans. Without it, one search over six 21 MB documents froze for 35 s and needed over 1 GB of heap. With it, the same search took 2 to 3 s and fitted in 512 MiB. The SDK's functions are checked at start, and a sidecar on an unpatched SDK refuses to start. `/ready` shows `sdk.patched`.

The same script carries the fix for QMD's reranker blend (upstream tobi/qmd#1032, not yet merged). A reranked hit's score is now the reranker's own score, and equal scores keep their rank-fusion order. That moves `minScore` too: the top-ranked hits no longer get a score floor from their rank. Stored indexes and cached reranker scores stay valid, so nothing needs re-embedding. Run `npm ci` in `sidecar/` when updating to a build with this fix, or the sidecar refuses to start.

`POST /documents` refuses anything over `MEMORIA_QMD_MAX_DOCUMENT_BYTES` (2 MiB) with a 413 `document_too_large`, and writes nothing. The rest of this section is about stalls, meaning time when the sidecar answers nothing else, including `/health`.

- **Within the cap.** A single search stalls for under a second: 675 ms at worst with one sub-query over 2 MiB documents.
- **Files already on disk, at any size.** The cap applies only at ingest, so these stay indexed and searchable, and their stalls are longer. Measured: 16 MiB of blank lines stalled for 6.0 s, and an update scan of a 170 MiB file for 5.8 s. A 21 MB ordinary transcript chunks in about 340 ms.
- **Very large files of blank lines.** Past 2.2 million break points, about 2 MiB of nothing but newlines, the chunker stops collecting more and cuts the rest at its plain chunk size. So such a document searches and embeds, with cruder chunks. A document that still cannot be chunked is set aside, and costs only itself.

**Concurrent searches.** Each search holds whole bodies while it runs, so memory grows with how many run at once. `MEMORIA_QMD_SEARCH_CONCURRENCY` (4) run together, `MEMORIA_QMD_SEARCH_QUEUE` (16) wait for up to `MEMORIA_QMD_SEARCH_MAX_WAIT_MS` (10 s), and the rest get a 503 `search_busy` with `Retry-After`. Memoria.Api passes that on as a 503. Four is chosen against the 4096 MiB heap: 20 searches of 10 sub-queries over 2 MiB documents peaked at 3.1 GB with 4 running, and the same load ran out of heap at 1024 MiB unless cut to 2 running. If you lower the heap, lower this with it.

**Connections.** One address may hold `MEMORIA_QMD_MAX_CONNECTIONS_PER_SOURCE` (64) connections, so a peer with no secret cannot use up the `MEMORIA_QMD_MAX_CONNECTIONS` total (1,024). List Memoria.Api's address in `MEMORIA_QMD_API_ADDRESSES` to give it 512. Reaching the total takes 16 addresses.

**Frontmatter.** When a key repeats, the last one wins, where it used to give no metadata. That spoofs nothing: filters read a document's own frontmatter, whoever can write that can write any value, and on a transcript the renderer writes those keys itself.

**Why not a worker thread.** Running the SDK in one was evaluated and not adopted. With the scans fixed, a search within the cap stalls for under a second. It would not reliably contain an out-of-memory error either: of four allocation patterns tried, three failed only the worker and one killed the whole process. It would also mean making about 25 backend methods asynchronous and copying 21 MB bodies between threads.

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

### Tell the API who its proxy is

**This is required once the proxy host exists.** Failed authentication is limited
per client address. With `Memoria:Auth:TrustedProxies` empty the API treats the
socket peer as the client, and behind a proxy that peer is the proxy, so every caller
shares one budget: 20 bad tokens from anyone would refuse every credential the API
does not already know, for the rest of the minute. Keys it knows keep working, but a
new client's first request can be refused. Two warnings point at this: one at most
hourly when a private address sends `X-Forwarded-For` it is not trusted to send, and
one naming the address when a budget runs out. Neither trusts the header.

1. Find the address the API sees. Give nginx-proxy-manager a fixed address on a
   user-defined Docker network and use that, or use the network's whole subnet if
   nothing untrusted shares it (`docker network inspect <network>`). If the API is
   reached through a published port instead of that network, the peer is usually the
   Docker gateway, not the proxy container. Confirm with `RemoteIp` in the API's log.
2. Set it:

   ```bash
   MEMORIA_TRUSTED_PROXY=172.20.0.2 docker compose -f deploy/compose.yaml up -d
   ```

   More than one entry: `Memoria__Auth__TrustedProxies__0`, `__1`, and so on.
3. The proxy must send this header, and a custom config must not replace it. Check
   that your nginx-proxy-manager version does, in `conf.d/include/proxy.conf`:

   ```nginx
   proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
   ```

   That appends the peer nginx sees to whatever the client sent. The API reads the
   chain from the right and stops at the first address that is not a trusted proxy,
   so anything a client writes on the left is ignored.
4. If Cloudflare or another proxy sits in front of nginx-proxy-manager, what nginx
   appends depends on its `real_ip` settings. Either nginx resolves the real client,
   or you add the outer proxy's ranges to `TrustedProxies`. Otherwise every client
   looks like that proxy.
5. Check it: send a bad token through the proxy, and confirm the `RemoteIp` in the
   API's request log is your address, not the proxy's. Then send it again with
   `X-Forwarded-For: 192.0.2.1` and confirm `RemoteIp` does not change. This check is
   what counts; the steps above only explain it.

Never trust a range that an untrusted client can connect from, and make sure the API's
port cannot be reached except through the proxy. A `/0` range is refused at startup.

This limit protects the API from one address at a time. A flood from many addresses
stays within each one's budget, so limit connections at the proxy as well, for example
with `limit_req` in the proxy host's custom config.

`/ready` stays anonymous so an orchestrator can probe it. It answers 200 while the
sidecar answers, even with no GPU worker up, and tells anonymous callers only the
status word. Point a health check that should fail on degradation at `/ready?strict=true`. See
[configuration.md](configuration.md#anonymous-routes).

## Upgrade order

Upgrade the sidecar before the API. The API sends a collection-scoped key's allowlist
with each document read, and the sidecar's strict body schema makes an older sidecar
refuse scoped batch reads with `400`. An older sidecar also ignores the allowlist on
single-document reads and has no glob grammar of its own, so scoped callers would be
back to QMD's global lookup for as long as the two are mismatched.

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
