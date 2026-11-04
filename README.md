# Memoria

A self-hosted search API over your own accumulated record — agent transcripts, issues, PRs, notes, docs — served as MCP, REST, and CLI, with [QMD](https://github.com/tobi/qmd) as the retrieval engine.

## Why

QMD is an excellent on-device hybrid search engine (BM25 + vector + LLM reranking), but it runs on one machine, binds its HTTP MCP transport to `localhost`, and ships no authentication. Memoria puts a real front door on it: an authenticated, multi-source, always-on service that every client — desktop, laptop, phone, cloud agent sessions — can query against one index.

Memoria does **not** reimplement retrieval. QMD stays the engine; its SQLite index is a derived artifact that can be rebuilt from source at any time.

## Shape

| Component | Stack | Role |
|---|---|---|
| `Memoria.Api` | ASP.NET Core (.NET 10) | REST API, outward MCP server, API-key auth, ingest |
| `Memoria.Core` | .NET 10 class library | Document envelope, search contracts |
| `sidecar/` (`memoria-qmd`) | TypeScript, Node 22, QMD SDK | REST over the QMD SDK; runs with the models and index, LAN-only behind a shared secret |
| `cli/` (`memoria`) | Go + cobra + keyring | Client; credentials in the OS keyring |
| QMD | GGUF models, SQLite | Retrieval engine, never exposed directly |

Container images publish to `ghcr.io/mggarofalo/memoria`.

## Status

`Memoria.Api` serves REST (`/v1/search`, `/v1/docs/{id}`, `/v1/docs:multiGet`, `/v1/collections`, `/v1/status`), an MCP server at `/mcp`, and `/openapi/v1.json` over the `memoria-qmd` sidecar; see [docs/configuration.md](docs/configuration.md). Every `/v1` and MCP route requires an API key scoped to named collections, with per-key rate limits and an audit log. The `memoria` CLI covers login, search, documents, the MCP stdio bridge and self-update; see [docs/cli.md](docs/cli.md). Ingest is in progress. Work is tracked in Plane project `MEMORIA` — see [docs/plane.md](docs/plane.md).

## Development

```bash
dotnet build Memoria.slnx          # build the API
dotnet test Memoria.slnx           # run tests
(cd cli && go build ./...)         # build the CLI
(cd cli && go test ./...)          # test the CLI
(cd sidecar && npm ci && npm test) # test the QMD sidecar
```

See [sidecar/README.md](sidecar/README.md) for running the sidecar against a local QMD index.

See [AGENTS.md](AGENTS.md) for full development guidance.

## Security

Memoria indexes agent transcripts, which routinely contain pasted credentials, `.env` contents, and tokens captured in tool output. Redaction runs at ingest, before anything is indexed. Do not deploy an instance without it, and scope API keys to collections rather than issuing a single all-access key.

## License

MIT — see [LICENSE](LICENSE).
