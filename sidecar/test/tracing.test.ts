import assert from "node:assert/strict";
import { test } from "node:test";
import { testConfig } from "./helpers.ts";
import { FakeBackend } from "./fake-backend.ts";
import { buildApp } from "../src/app.ts";

test("a request-id header becomes the request id so Memoria.Api traces correlate", async () => {
  const lines: string[] = [];
  const stream = { write: (chunk: string) => void lines.push(String(chunk)) };
  const backend = new FakeBackend({ collections: [], docs: [] });
  const app = await buildApp({ backend, config: testConfig(), logger: { level: "info", stream } });
  try {
    const traceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    await app.inject({ method: "GET", url: "/health", headers: { "request-id": traceId } });
    const parsed = lines.map((line) => JSON.parse(line) as { reqId?: string });
    assert.ok(parsed.some((entry) => entry.reqId === traceId), JSON.stringify(parsed));
  } finally {
    await app.close();
  }
});

test("without a request-id header Fastify still assigns its own id", async () => {
  const lines: string[] = [];
  const stream = { write: (chunk: string) => void lines.push(String(chunk)) };
  const backend = new FakeBackend({ collections: [], docs: [] });
  const app = await buildApp({ backend, config: testConfig(), logger: { level: "info", stream } });
  try {
    await app.inject({ method: "GET", url: "/health" });
    const parsed = lines.map((line) => JSON.parse(line) as { reqId?: string });
    assert.ok(parsed.some((entry) => typeof entry.reqId === "string" && entry.reqId.length > 0));
  } finally {
    await app.close();
  }
});
