import assert from "node:assert/strict";
import { test } from "node:test";
import { buildApp } from "../src/app.ts";
import { sharedFor } from "../src/shared-result.ts";
import { FakeBackend } from "./fake-backend.ts";
import { authHeaders, testConfig } from "./helpers.ts";

test("callers share one call while it runs and for its time to live, then call again", async () => {
  let clock = 0;
  let calls = 0;
  let finish!: (value: number) => void;
  const shared = sharedFor(1_000, () => {
    calls++;
    return new Promise<number>((resolve) => (finish = resolve));
  }, () => clock);
  const first = shared();
  const second = shared();
  assert.equal(first, second);
  await settled();
  finish(7);
  assert.equal(await first, 7);
  clock = 999;
  assert.equal(await shared(), 7);
  assert.equal(calls, 1);
  clock = 1_000;
  const third = shared();
  await settled();
  finish(8);
  assert.equal(await third, 8);
  assert.equal(calls, 2);
});

/** The call starts on a microtask: let it. */
const settled = (): Promise<void> => new Promise((resolve) => setImmediate(resolve));

test("a call that throws at once, after one that succeeded, is a failure not kept, and never serves the old answer forever", async () => {
  let clock = 0;
  let calls = 0;
  const shared = sharedFor(1_000, () => {
    calls++;
    if (calls === 2) throw new Error("thrown at once");
    return Promise.resolve(calls);
  }, () => clock);
  assert.equal(await shared(), 1);
  clock = 1_000;
  await assert.rejects(shared(), /thrown at once/);
  assert.equal(await shared(), 3);
});

test("a failure is shared by those waiting on it, and not kept for the next caller", async () => {
  let calls = 0;
  const shared = sharedFor(60_000, async () => {
    calls++;
    if (calls === 1) throw new Error("index busy");
    return "ok";
  });
  const [first, second] = [shared(), shared()];
  await assert.rejects(first, /index busy/);
  await assert.rejects(second, /index busy/);
  assert.equal(await shared(), "ok");
  assert.equal(calls, 2);
});

test("many /ready at once scan the index once", async () => {
  const backend = new FakeBackend({ collections: [], docs: [] });
  let scans = 0;
  const getStatus = backend.getStatus.bind(backend);
  backend.getStatus = async () => {
    scans++;
    return getStatus();
  };
  const app = await buildApp({ backend, config: { ...testConfig(), warmup: false }, logger: false });
  try {
    const answers = await Promise.all(Array.from({ length: 20 }, () => app.inject({ method: "GET", url: "/ready", headers: authHeaders })));
    assert.ok(answers.every((answer) => answer.statusCode === 200));
    assert.equal(scans, 1);
  } finally {
    await app.close();
  }
});
