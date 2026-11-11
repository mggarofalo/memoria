import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { test } from "node:test";
import { fixturePath } from "./helpers.ts";

// As when the test process that started it exits before the fixture runs: the fixture's parent is then not its starter.
// Before the fixture checked its starter, it took whatever adopted it for its parent and ran on, and a full mutation
// run gathered these until the runner ran out of memory.
test("the fake llama-server ends itself when its starter is no longer its parent", { timeout: 30_000 }, async (t) => {
  const child = spawn(process.execPath, [fixturePath, "--port", "0"], { env: { ...process.env, FAKE_LLAMA_PARENT_PID: "1" }, stdio: "ignore" });
  t.signal.addEventListener("abort", () => child.kill("SIGKILL"));
  const [code] = (await once(child, "exit")) as [number | null];
  assert.equal(code, 0);
});
