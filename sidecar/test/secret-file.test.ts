import assert from "node:assert/strict";
import { chmod, mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { test } from "node:test";
import { loadConfig } from "../src/config.ts";
import { readSecretFile, resolveSecret } from "../src/secret-file.ts";
import { loadConfig as loadWorkerConfig } from "../../worker/src/config.ts";
import { tempDir } from "./helpers.ts";

const SECRET = "Xk29vQmB7fLp3ZtRw";
const VAR = "MEMORIA_QMD_SECRET_FILE";

async function secretFile(content: string | Buffer, mode = 0o600): Promise<string> {
  const file = path.join(await tempDir("memoria-secret-"), "secret.txt");
  await writeFile(file, content, { mode });
  await chmod(file, mode);
  return file;
}

/** Windows reports every writable file as 0o666, so tests of content pass "win32" to reach that code everywhere. */
const read = (file: string) => readSecretFile(file, VAR, "win32");

test("the two copies of secret-file.ts are identical", async () => {
  const sidecar = await readFile(new URL("../src/secret-file.ts", import.meta.url), "utf8");
  const worker = await readFile(new URL("../../worker/src/secret-file.ts", import.meta.url), "utf8");
  assert.equal(sidecar, worker);
});

test("trailing newlines, surrounding space and a BOM are not part of the secret", async () => {
  for (const content of [SECRET, `${SECRET}\n`, `${SECRET}\r\n`, `${SECRET}\n\n\n`, `  ${SECRET} \n`, `﻿${SECRET}\n`]) {
    assert.equal(read(await secretFile(content)), SECRET, JSON.stringify(content));
  }
});

test("a file that cannot be a secret is refused without repeating its content", async () => {
  const cases: Array<[string | Buffer, RegExp]> = [
    ["", /is empty/],
    ["  \n\n", /is empty/],
    [`${SECRET}\nsecond-line`, /one line/],
    [`${SECRET}\r\nsecond-line`, /one line/],
    [Buffer.from(`${SECRET}\n`, "utf16le"), /not UTF-8/],
    [Buffer.from([0xff, 0xfe, 0x41, 0x00]), /not UTF-8/],
    ["x".repeat(4097), /larger than 4096/],
  ];
  for (const [content, expected] of cases) {
    const file = await secretFile(content);
    assert.throws(
      () => read(file),
      (err: unknown) => {
        const message = (err as Error).message;
        assert.match(message, expected);
        assert.match(message, new RegExp(VAR));
        assert.ok(!message.includes(SECRET), "the message must not repeat the secret");
        assert.ok(!message.includes("second-line"));
        return true;
      },
    );
  }
});

test("a missing path and a directory are clear errors", async () => {
  const dir = await tempDir("memoria-secret-");
  assert.throws(() => read(path.join(dir, "absent")), /cannot read .*ENOENT/);
  await mkdir(path.join(dir, "folder"));
  assert.throws(() => read(path.join(dir, "folder")), /is not a file/);
});

test("on POSIX a file others can read is refused, with the fix in the message", { skip: process.platform === "win32" ? "POSIX mode bits" : false }, async () => {
  for (const mode of [0o644, 0o640, 0o604, 0o660, 0o666]) {
    const file = await secretFile(SECRET, mode);
    assert.throws(() => readSecretFile(file, VAR), (err: unknown) => {
      const message = (err as Error).message;
      assert.match(message, /chmod 600/);
      assert.ok(message.includes(mode.toString(8)));
      assert.ok(!message.includes(SECRET));
      return true;
    });
  }
  for (const mode of [0o600, 0o400]) assert.equal(readSecretFile(await secretFile(SECRET, mode), VAR), SECRET);
});

test("the POSIX rule is decided by platform, and does not apply on Windows", async () => {
  // Without chmod support the file reads as wide open, which is the case to refuse on POSIX.
  const file = await secretFile(SECRET, 0o666);
  assert.throws(() => readSecretFile(file, VAR, "linux"), /chmod 600/);
  assert.equal(readSecretFile(file, VAR, "win32"), SECRET);
});

test("resolveSecret takes the file or the variable, and refuses both", async () => {
  const file = await secretFile(`${SECRET}\n`);
  assert.equal(resolveSecret({}, "S", "win32"), undefined);
  assert.equal(resolveSecret({ S: "", S_FILE: " " }, "S", "win32"), undefined);
  assert.equal(resolveSecret({ S: ` ${SECRET} ` }, "S", "win32"), SECRET);
  assert.equal(resolveSecret({ S_FILE: file }, "S", "win32"), SECRET);
  assert.throws(() => resolveSecret({ S: SECRET, S_FILE: file }, "S", "win32"), /Set only one of S and S_FILE/);
});

test("the sidecar's secret and worker token can come from files", async () => {
  const file = await secretFile(`${SECRET}\n`);
  const lan = { MEMORIA_QMD_HOST: "192.168.1.20", MEMORIA_QMD_ALLOW_HTTP_ON_PRIVATE_NETWORK: "1" };
  assert.equal(loadConfig({ ...lan, MEMORIA_QMD_SECRET_FILE: file }).secret, SECRET);
  assert.equal(loadConfig({ ...lan, MEMORIA_QMD_SECRET: SECRET }).secret, SECRET);
  assert.throws(() => loadConfig({ ...lan, MEMORIA_QMD_SECRET_FILE: file, MEMORIA_QMD_SECRET: SECRET }), /Set only one/);
  assert.throws(() => loadConfig({ ...lan, MEMORIA_QMD_SECRET_FILE: path.join(path.dirname(file), "absent") }), /MEMORIA_QMD_SECRET_FILE: cannot read/);
  const short = await secretFile("short\n");
  assert.throws(() => loadConfig({ MEMORIA_QMD_SECRET_FILE: short }), /at least 16/);

  const remote = loadConfig({ MEMORIA_QMD_LLM: "remote", MEMORIA_QMD_ALLOW_HTTP_ON_PRIVATE_NETWORK: "1", MEMORIA_QMD_WORKER_URL: "http://192.168.1.20:8383", MEMORIA_QMD_WORKER_TOKEN_FILE: file });
  assert.equal(remote.llm.mode === "remote" ? remote.llm.workers[0]!.token : undefined, SECRET);
  // Local mode does not read the worker settings at all.
  assert.deepEqual(loadConfig({ MEMORIA_QMD_WORKER_TOKEN_FILE: path.join(path.dirname(file), "absent") }).llm, { mode: "local" });
});

test("the worker's token can come from a file", async () => {
  const file = await secretFile(`${SECRET}\n`);
  const lan = { MEMORIA_WORKER_HOST: "192.168.1.20", MEMORIA_WORKER_ALLOW_HTTP_ON_PRIVATE_NETWORK: "1" };
  assert.equal(loadWorkerConfig({ ...lan, MEMORIA_WORKER_TOKEN_FILE: file }).token, SECRET);
  assert.equal(loadWorkerConfig({ ...lan, MEMORIA_WORKER_TOKEN: SECRET }).token, SECRET);
  assert.throws(() => loadWorkerConfig({ ...lan, MEMORIA_WORKER_TOKEN_FILE: file, MEMORIA_WORKER_TOKEN: SECRET }), /Set only one/);
  const short = await secretFile("short\n");
  assert.throws(() => loadWorkerConfig({ MEMORIA_WORKER_TOKEN_FILE: short }), /at least 16/);
});
