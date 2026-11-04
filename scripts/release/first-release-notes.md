First tagged release of Memoria, a front door over QMD for searching your own accumulated record.

- `Memoria.Api`: REST v1 and an MCP server, published as `ghcr.io/mggarofalo/memoria`.
- `memoria-qmd` and `memoria-worker`: the search sidecar and the inference worker. Run them from a checkout of this tag.
- `memoria`: the command-line client, attached here for Windows, Linux, and macOS on amd64 and arm64.

Verify a download with `sha256sum -c checksums.txt`, or with `gh attestation verify <file> --repo mggarofalo/memoria`.
