// Consistent copies of live SQLite databases, for a backup to read: deploy/backup.yaml runs it. A copy of the file a writer is using can be torn,
// so this uses SQLite's online backup, through better-sqlite3, as `sqlite3 .backup` does. Each source is opened read-only, so nothing is
// created beside it, and each copy is checked. The copies replace the last ones only once every one has been made and checked, so a failed
// run leaves the staged set as it was, never half old and half new.
//
// The job queue's file, named with --queue, is different: a running sidecar holds it under an exclusive lock, which nothing else can read
// through. The lock decides. A file nobody holds is copied here, as with the sidecar stopped, and the sidecar is never contacted. A held file
// is the sidecar's, so it is asked for the copy (POST /queue:snapshot), which it writes beside the file. Its refusal, or no answer, fails.
//
// Usage: snapshot-db.mjs <source> <target> [<source> <target> ...] [--queue <source> <target>]
// With --queue, MEMORIA_SIDECAR_URL is the sidecar, and MEMORIA_SIDECAR_SECRET its shared secret.
import { createHash } from "node:crypto";
import { chownSync, closeSync, constants, existsSync, fstatSync, openSync, readFileSync, renameSync, rmSync, statSync, writeFileSync } from "node:fs";
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

/** Checks a copy, and switches it to a rollback journal, as it is backed up as one file. False, with the copy gone, if it failed the check. */
function checked(source, partial) {
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
  if (check === "ok") return true;
  console.error(`${source}: its copy failed SQLite's integrity check (${check})`);
  rmSync(partial, { force: true });
  return false;
}

/**
 * Writes a checked copy to `partial`: "copied", "failed", "absent", or "held", when another process holds it under an exclusive lock. `busyMs`
 * is how long to wait out a lock first: a passing writer lets go within it, a holder does not.
 */
async function copyLive(source, partial, busyMs = 5_000) {
  if (!existsSync(source)) return "absent";
  rmSync(partial, { force: true });
  const db = new Database(source, { readonly: true, fileMustExist: true, timeout: busyMs });
  try {
    // A database held under an exclusive lock, as a running sidecar holds its job queue, still reads as locked after the busy wait. The
    // online backup of it would be an empty database that passes the check.
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
  return checked(source, partial) ? "copied" : "failed";
}

/** The sidecar's answer, or why there was none. Never prints the secret, and follows no redirect, which could take it elsewhere. */
async function askSidecar(url, secret) {
  let response;
  let text;
  try {
    response = await fetch(new URL("/queue:snapshot", url), { method: "POST", headers: { authorization: `Bearer ${secret}` }, redirect: "error", signal: AbortSignal.timeout(SNAPSHOT_TIMEOUT_MS) });
    text = await response.text();
  } catch (err) {
    if (response) return { refused: `${response.status} with a body that could not be read` };
    return { refused: `no answer: ${err?.cause?.code ?? err?.name ?? "unknown"}` };
  }
  let body = {};
  try {
    body = JSON.parse(text);
  } catch {
    // Reported by its status below.
  }
  if (!response.ok) return { refused: `${response.status} ${body.error ?? "with no error code"}` };
  if (!Number.isInteger(body.bytes) || typeof body.sha256 !== "string") return { refused: `${response.status} with no bytes and sha256` };
  return { snapshot: body };
}

/** The copy the sidecar wrote beside its file, if it is the one it answered with. Opened without following a link, so it is that file. */
function readSnapshot(file, snapshot) {
  let fd;
  try {
    fd = openSync(file, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
  } catch (err) {
    return { error: err?.code === "ENOENT" ? "there is no copy where the sidecar writes it" : "what is where the sidecar writes its copy is not a file" };
  }
  try {
    if (!fstatSync(fd).isFile()) return { error: "what is where the sidecar writes its copy is not a file" };
    const bytes = readFileSync(fd);
    const sha256 = createHash("sha256").update(bytes).digest("hex");
    if (bytes.length !== snapshot.bytes || sha256 !== snapshot.sha256) return { error: "the copy changed after the sidecar wrote it, as when another snapshot was taken at the same time" };
    return { bytes };
  } finally {
    closeSync(fd);
  }
}

/** As copyLive, for the queue's file: a held one is copied by the sidecar that holds it. */
async function copyQueue(source, partial, url, secret) {
  // Nothing but the sidecar writes this file, and it holds the lock for as long as it runs, so there is no passing writer to wait out.
  const copied = await copyLive(source, partial, 0);
  if (copied !== "held") return { copied, how: "read directly, as nothing holds the file" };
  const asked = await askSidecar(url, secret);
  if (asked.refused) {
    console.error(`${source}: a process holds the file, and the sidecar did not copy it (${asked.refused})`);
    return { copied: "failed" };
  }
  const file = `${source}.snapshot`;
  const read = readSnapshot(file, asked.snapshot);
  if (read.error) {
    console.error(`${source}: ${read.error}`);
    return { copied: "failed" };
  }
  writeFileSync(partial, read.bytes);
  // Read, so it goes: the copy is the backup's now, and the volume keeps no second one.
  rmSync(file, { force: true });
  return { copied: checked(source, partial) ? "copied" : "failed", how: "copied by the sidecar" };
}

function parse(args) {
  const pairs = [];
  let queue = null;
  for (let i = 0; i < args.length; i += 2) {
    if (args[i] === "--queue") {
      if (queue || i + 2 >= args.length) return null;
      queue = { source: args[i + 1], target: args[i + 2], queue: true };
      i++;
      continue;
    }
    if (i + 1 >= args.length) return null;
    pairs.push({ source: args[i], target: args[i + 1], queue: false });
  }
  return pairs.length > 0 || queue ? [...pairs, ...(queue ? [queue] : [])] : null;
}

const pairs = parse(process.argv.slice(2));
if (!pairs) {
  console.error("usage: snapshot-db.mjs <source> <target> [<source> <target> ...] [--queue <source> <target>]");
  process.exit(2);
}
const { MEMORIA_SIDECAR_URL: url, MEMORIA_SIDECAR_SECRET: secret } = process.env;
if (pairs.some((p) => p.queue) && (!url || !secret)) {
  console.error("--queue needs MEMORIA_SIDECAR_URL and MEMORIA_SIDECAR_SECRET, to ask the sidecar for its copy");
  process.exit(2);
}

const made = [];
/** Targets whose source is gone. Their old copies go too, so what is staged is always from one run. */
const gone = [];
let failed = false;
for (const { source, target, queue } of pairs) {
  const partial = `${target}.partial`;
  const { copied, how } = queue ? await copyQueue(source, partial, url, secret) : { copied: await copyLive(source, partial) };
  if (copied === "copied") made.push({ source, target, partial, how });
  if (copied === "absent") gone.push(target);
  if (copied === "absent") console.log(`skipped ${source}: there is no such database`);
  if (copied === "held") console.error(`${source}: another process holds it, so it was not copied`);
  if (copied === "failed" || copied === "held") failed = true;
}
if (failed) {
  for (const { partial } of made) rmSync(partial, { force: true });
  for (const { target } of pairs) console.error(`${target}: unchanged, so ${keeps(target)}`);
  process.exit(1);
}
for (const { source, target, partial, how } of made) {
  renameSync(partial, target);
  console.log(`${source} -> ${target}${how ? `, ${how}` : ""}`);
}
for (const target of gone) {
  if (!existsSync(target)) continue;
  rmSync(target, { force: true });
  console.log(`${target}: removed, as its database is gone`);
}
process.exit(0);
