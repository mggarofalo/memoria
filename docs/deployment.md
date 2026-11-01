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
or as a systemd unit on Linux (`npm run build && node dist/main.js`). See
`sidecar/README.md`.

## The image

`ghcr.io/mggarofalo/memoria` is published by `.github/workflows/docker.yml`:
every pull request builds it, every push to `main` publishes `latest` and
`sha-<short>`, and a `v*` tag publishes `<major>.<minor>.<patch>` and
`<major>.<minor>`.

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
  memoria-api:local
```

## Compose on the homelab

[`deploy/compose.yaml`](../deploy/compose.yaml) is the production shape:

```bash
MEMORIA_SIDECAR_URL=http://192.168.1.20:8282 \
MEMORIA_SIDECAR_SECRET=... \
docker compose -f deploy/compose.yaml up -d
```

It publishes port 8080 on `127.0.0.1` by default so only the reverse proxy on
the same host can reach it. Set `MEMORIA_BIND_ADDRESS` to the host's LAN
address to let LAN clients connect directly while the proxy entry does not
exist yet.

Containers reach the sidecar on the desktop over the LAN with no special
Docker networking: bridge networks NAT outbound traffic.

### Required environment

| Variable | Purpose |
|---|---|
| `Memoria__Sidecar__BaseUrl` | Where the sidecar listens, e.g. `http://192.168.1.20:8282`. |
| `Memoria__Sidecar__Secret` | The sidecar's `MEMORIA_QMD_SECRET`. The API refuses to start without it when the sidecar is not on loopback. |

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

## Exposure is gated

Nothing in this repository exposes Memoria to the internet, and nothing
should until two things land:

1. **Homelab hardening** (patching, firewall review) — the host epic's
   "patch homelab before public exposure" issue.
2. **API keys with collection scopes** — the auth epic. Until then every
   caller is unrestricted, which is acceptable on a trusted LAN and nowhere
   else. `MemoriaScopeAttribute` and `ICallerScope` are already on every
   endpoint so enforcing keys touches no endpoint code.

Until both are done: keep the compose port on `127.0.0.1` or the LAN address,
do not create the proxy host entry, and do not forward a router port.

## The loopback invariant

QMD itself is never reachable from anywhere. The sidecar is the only process
that opens the index; it binds the model host's LAN address, requires the
shared secret on every route except `/health`, and compares it in constant
time. `Memoria.Api` is the only intended caller. If any path exists from a
client to the sidecar that does not pass through `Memoria.Api`, authentication
has been bypassed entirely. When auditing the deployment, check that:

- the sidecar port (8282) is reachable from the API host and from nowhere
  outside the LAN;
- the API host's firewall allows 8080 only from the proxy (or the LAN during
  the pre-auth phase);
- the sidecar's `MEMORIA_QMD_HOST` is the LAN address, never `0.0.0.0` on a
  machine with a public interface.
