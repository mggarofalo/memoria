import assert from "node:assert/strict";
import { chmod, mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { test } from "node:test";
import { readSecretFile, resolveSecret } from "../src/secret-file.ts";

const SECRET = "0123456789abcdefXYZ";
const VARIABLE = "MEMORIA_WORKER_TOKEN_FILE";

async function secretFile(content: string, mode = 0o600): Promise<string> {
  const file = path.join(await mkdtemp(path.join(tmpdir(), "memoria-worker-secret-")), "secret.txt");
  await writeFile(file, content, { mode });
  await chmod(file, mode);
  return file;
}

test("a file others can read is refused off Windows, naming the fix and not the content", async () => {
  const file = await secretFile(`${SECRET}\n`, 0o644);
  assert.throws(
    () => readSecretFile(file, VARIABLE, "linux"),
    (err: unknown) => {
      const message = (err as Error).message;
      assert.match(message, /has mode \d{3}/);
      assert.match(message, /chmod 600/);
      assert.ok(!message.includes(SECRET));
      return true;
    },
  );
});

test("Windows reports no ACL through mode bits, so the content is what gets checked there", async () => {
  const file = await secretFile(`${SECRET}\n`, 0o644);
  assert.equal(readSecretFile(file, VARIABLE, "win32"), SECRET);
});

test("a directory and an oversized file are refused", async () => {
  const dir = await mkdtemp(path.join(tmpdir(), "memoria-worker-secret-"));
  assert.throws(() => readSecretFile(dir, VARIABLE, "win32"), /is not a file/);
  const oversized = await secretFile("x".repeat(4097));
  assert.throws(() => readSecretFile(oversized, VARIABLE, "win32"), /larger than 4096/);
});

test("the variable or the file supplies the secret, and setting both is refused", async () => {
  const file = await secretFile(`${SECRET}\n`);
  const env = (over: NodeJS.ProcessEnv): NodeJS.ProcessEnv => over;
  assert.equal(resolveSecret(env({}), "MEMORIA_WORKER_TOKEN", "win32"), undefined);
  assert.equal(resolveSecret(env({ MEMORIA_WORKER_TOKEN: `  ${SECRET} ` }), "MEMORIA_WORKER_TOKEN", "win32"), SECRET);
  assert.equal(resolveSecret(env({ MEMORIA_WORKER_TOKEN_FILE: file }), "MEMORIA_WORKER_TOKEN", "win32"), SECRET);
  assert.throws(() => resolveSecret(env({ MEMORIA_WORKER_TOKEN: SECRET, MEMORIA_WORKER_TOKEN_FILE: file }), "MEMORIA_WORKER_TOKEN", "win32"), /Set only one/);
});
