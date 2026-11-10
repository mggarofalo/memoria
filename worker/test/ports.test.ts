import assert from "node:assert/strict";
import { createServer } from "node:http";
import { test } from "node:test";
import { FETCH_BAD_PORTS, freePort, freePorts, listenFetchable } from "./ports.ts";

test("every port on the list is one this runtime's fetch refuses", async () => {
  for (const port of FETCH_BAD_PORTS) {
    await assert.rejects(fetch(`http://127.0.0.1:${port}/`), (err: Error) => (err.cause as Error | undefined)?.message === "bad port", String(port));
  }
});

test("a port fetch refuses is given back and another chosen", async () => {
  const offered = [6000, 6697, 40_000];
  const closed: number[] = [];
  let current = 0;
  const server = {
    listen: (_port: number, _host: string, callback: () => void) => {
      current = offered.shift()!;
      callback();
    },
    address: () => ({ port: current, address: "127.0.0.1", family: "IPv4" }),
    close: (callback: (err?: Error) => void) => {
      closed.push(current);
      callback();
    },
    once: () => {},
    removeListener: () => {},
  };
  assert.equal(await listenFetchable(server), 40_000);
  assert.deepEqual(closed, [6000, 6697]);
});

test("a server on the chosen port answers fetch, and a failed listen is an error", async () => {
  const server = createServer((_, response) => response.end("ok"));
  const port = await listenFetchable(server);
  try {
    assert.equal(await (await fetch(`http://127.0.0.1:${port}/`)).text(), "ok");
    await assert.rejects(listenFetchable(createServer(), "203.0.113.1"));
  } finally {
    await new Promise((resolve) => server.close(resolve));
  }
  assert.ok(!FETCH_BAD_PORTS.has(await freePort()));
});

test("ports chosen together are all different, and all free again afterwards", async () => {
  // Asked one at a time, Linux repeated a port in about 1 of 2,000 triples. Held together, they cannot repeat.
  for (let round = 0; round < 500; round++) {
    const ports = await freePorts(3);
    assert.equal(new Set(ports).size, 3, ports.join(", "));
  }
  for (const port of await freePorts(3)) {
    const server = createServer();
    await new Promise<void>((resolve) => server.listen(port, "127.0.0.1", resolve));
    assert.equal((server.address() as { port: number }).port, port);
    await new Promise((resolve) => server.close(resolve));
  }
});
