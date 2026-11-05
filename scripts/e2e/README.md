# End-to-end checks

This runs the real `memoria` CLI against a real `Memoria.Api` and a real `memoria-qmd` sidecar. It exists because fakes share the tests' assumptions. Its first run found that `memoria get collection/path.md` returned 404 from a real API, while every test against a fake server passed.

```sh
(cd sidecar && npm ci)
node scripts/e2e/run.ts              # builds the CLI, API and sidecar first
node scripts/e2e/run.ts --no-build   # reuses a built API and sidecar
```

It needs Node 22.18+, Go and the .NET SDK. It exits non-zero if any check fails.

## What it touches

- **No real keys.** Every key comes from the throwaway server it starts, and printed output is scrubbed of them.
- **No real index, and no models.** The real QMD SDK builds a small index from two fixture collections. Every proxy points nowhere, so nothing can download a model.
- **One private directory.** A new temp directory, mode 0700, holds the index, control database, logs and config. Home and config variables point inside it for every process, and it is deleted at the end, pass or fail.
- **Loopback only**, on free ports.
- **One exception, the OS keyring.** A file-backend login records its server in the keyring when one is available. This uses the profile `memoria-e2e-test`, holds no key, and is removed by the logout that follows. On a host with no keyring, such as CI, nothing happens.

## What it does not cover

With no models, there is no reranking and no vector search: queries use `--no-rerank` and match one document. It runs plain http, not TLS, and does not exercise a prompt in a real terminal.

## Adding checks

Add a function to `checks.ts`, and call it from `run.ts`, before `rateLimits`. That section must stay last, because it uses up the failed-auth limit for the address.

Keep checks deterministic. The rate limits are fixed and their windows are 30 minutes, so a 429 arrives on an exact request however slow the machine is, and `rateLimits` uses its own key. Other checks can make up to 100 requests with one key before hitting the limit.

To learn what the sidecar was actually asked, read `h.log("sidecar")`, as `traversal` does. Use `h.rawGet` to send exact path bytes, since `fetch` and `URL` normalize them. Pass any new key to `h.remember`, so that it cannot be printed.
