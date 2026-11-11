// Consistent copies of live SQLite databases, for a backup to read: deploy/backup.yaml runs it. A copy of the file a writer is using can be torn,
// so this uses SQLite's online backup, through better-sqlite3, as `sqlite3 .backup` does. Each source is opened read-only, so nothing is
// created beside it, and each copy is checked before it replaces the last one.
//
// The job queue's file, named with --queue, is different: a running sidecar holds it under an exclusive lock, which nothing else can read
// through. So the sidecar is asked for the copy (POST /queue:snapshot), and writes it beside the file. If the sidecar answers, its answer
// decides: a refusal fails. If it does not answer, the lock decides: a file nobody holds is copied here, as the sidecar is stopped, and a
// file something holds fails, as that is a sidecar this cannot reach. Either failure keeps the last copy and exits 1.
//
// Usage: snapshot-db.mjs <source> <target> [<source> <target> ...] [--queue <source> <target>]
// With --queue, MEMORIA_SIDECAR_URL is the sidecar, and MEMORIA_SIDECAR_SECRET its shared secret.
import { createHash } from "node:crypto";
import { chownSync, existsSync, lstatSync, readFileSync, renameSync, rmSync, statSync, writeFileSync } from "node:fs";
import Database from "better-sqlite3";

/** Long enough for the sidecar to copy a large queue file, which it does in one statement. */
const SNAPSHOT_TIMEOUT_MS = 60_000;

/**
 * A reader of a WAL database writes its -shm index, so it cannot open one on a read-only mount. Opened here as root on a writable one, it can
 * leave -wal or -shm files root's own, which the service that owns the database then cannot write. Give them back to that owner.
 */
function returnToOwner(source) {
  const { uid, gid } = statSync(source);
  for (const side of [`${source}-wal`, `${source}-shm`]) {
    if (existsSync(side) && statSync(side).uid !== uid) chownSync(side, uid, gid);
  }
}

function keeps(target) {
  return existsSync(target) ? "its last copy stays" : "there is no copy of it";
}

/** Checks a copy, switches it to a rollback journal, as it is backed up as one file, and puts it in place. False if it failed the check. */
function install(source, partial, target) {
  let check;
  try {
    const copy = new Database(partial);
    try {
      copy.pragma("journal_mode = DELETE");
      check = copy.pragma("integrity_check", { simple: true });
    } finally {
      copy.close();
    }
  } catch (err) {
    // Not a database at all, which no check gets to say.
    check = err?.message ?? "unreadable";
  }
  if (check !== "ok") {
    console.error(`${source}: its copy failed SQLite's integrity check (${check}), so the last good copy stays`);
    rmSync(partial, { force: true });
    return false;
  }
  renameSync(partial, target);
  return true;
}

/** "copied", "failed", "absent", or "held": another process holds it under an exclusive lock. */
async function copyLive(source, target) {
  if (!existsSync(source)) return "absent";
  const partial = `${target}.partial`;
  rmSync(partial, { force: true });
  const db = new Database(source, { readonly: true, fileMustExist: true });
  try {
    // A database held under an exclusive lock, as a running sidecar holds its job queue, still reads as locked after the driver's 5 s busy
    // wait, which a passing writer would not. The online backup of it would be an empty database that passes the check.
    try {
      db.prepare("SELECT count(*) FROM sqlite_master").get();
    } catch (err) {
      if (err?.code !== "SQLITE_BUSY") throw err;
      return "held";
    }
    await db.backup(partial);
  } finally {
    db.close();
    returnToOwner(source);
  }
  return install(source, partial, target) ? "copied" : "failed";
}

/** The sidecar's answer, or why there was none. Never prints the secret. */
async function askSidecar(url, secret) {
  let response;
  try {
    response = await fetch(new URL("/queue:snapshot", url), { method: "POST", headers: { authorization: `Bearer ${secret}` }, signal: AbortSignal.timeout(SNAPSHOT_TIMEOUT_MS) });
  } catch (err) {
    return { answered: false, why: err?.cause?.code ?? err?.name ?? "no answer" };
  }
  const text = await response.text();
  let body = {};
  try {
    body = JSON.parse(text);
  } catch {
    // Reported by its status below.
  }
  if (!response.ok) return { answered: true, refused: `${response.status} ${body.error ?? "with no error code"}` };
  if (!Number.isInteger(body.bytes) || typeof body.sha256 !== "string") return { answered: true, refused: `${response.status} with no bytes and sha256` };
  return { answered: true, snapshot: body };
}

/** The copy the sidecar wrote beside its file, if it is the one it answered with. */
function readSnapshot(file, snapshot) {
  let stat;
  try {
    stat = lstatSync(file);
  } catch {
    return { error: "there is no copy where the sidecar writes it" };
  }
  if (!stat.isFile()) return { error: "what is where the sidecar writes its copy is not a file" };
  const bytes = readFileSync(file);
  const sha256 = createHash("sha256").update(bytes).digest("hex");
  if (bytes.length !== snapshot.bytes || sha256 !== snapshot.sha256) return { error: "the copy changed after the sidecar wrote it, as when another snapshot was taken at the same time" };
  return { bytes };
}

async function copyQueue(source, target, url, secret) {
  const asked = await askSidecar(url, secret);
  if (!asked.answered) {
    const copied = await copyLive(source, target);
    if (copied === "held") {
      console.error(`${source}: the sidecar did not answer (${asked.why}), and a process holds the file, so it was not copied and ${keeps(target)}`);
      return false;
    }
    if (copied === "copied") console.log(`${source} -> ${target}, read directly: the sidecar did not answer (${asked.why}), and nothing holds the file`);
    if (copied === "absent") console.log(`skipped ${source}: the sidecar did not answer (${asked.why}), and there is no such database`);
    return copied !== "failed";
  }
  if (asked.refused) {
    console.error(`${source}: the sidecar refused to copy it (${asked.refused}), so ${keeps(target)}`);
    return false;
  }
  const file = `${source}.snapshot`;
  const read = readSnapshot(file, asked.snapshot);
  if (read.error) {
    console.error(`${source}: ${read.error}, so ${keeps(target)}`);
    return false;
  }
  const partial = `${target}.partial`;
  writeFileSync(partial, read.bytes);
  // Read, so it goes: the copy is the backup's now, and the volume keeps no second one.
  rmSync(file, { force: true });
  if (!install(source, partial, target)) return false;
  console.log(`${source} -> ${target}, copied by the sidecar`);
  return true;
}

function parse(args) {
  const pairs = [];
  let queue = null;
  for (let i = 0; i < args.length; i += 2) {
    if (args[i] === "--queue") {
      if (queue || i + 2 >= args.length) return null;
      queue = { source: args[i + 1], target: args[i + 2] };
      i++;
      continue;
    }
    if (i + 1 >= args.length) return null;
    pairs.push({ source: args[i], target: args[i + 1] });
  }
  return pairs.length > 0 || queue ? { pairs, queue } : null;
}

const parsed = parse(process.argv.slice(2));
if (!parsed) {
  console.error("usage: snapshot-db.mjs <source> <target> [<source> <target> ...] [--queue <source> <target>]");
  process.exit(2);
}
const { MEMORIA_SIDECAR_URL: url, MEMORIA_SIDECAR_SECRET: secret } = process.env;
if (parsed.queue && (!url || !secret)) {
  console.error("--queue needs MEMORIA_SIDECAR_URL and MEMORIA_SIDECAR_SECRET, to ask the sidecar for its copy");
  process.exit(2);
}

let failed = false;
for (const { source, target } of parsed.pairs) {
  const copied = await copyLive(source, target);
  if (copied === "copied") console.log(`${source} -> ${target}`);
  if (copied === "absent") console.log(`skipped ${source}: there is no such database`);
  if (copied === "held") console.error(`${source}: another process holds it, so it was not copied and ${keeps(target)}`);
  if (copied === "failed" || copied === "held") failed = true;
}
if (parsed.queue && !(await copyQueue(parsed.queue.source, parsed.queue.target, url, secret))) failed = true;
process.exit(failed ? 1 : 0);
