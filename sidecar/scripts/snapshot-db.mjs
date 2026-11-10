// Consistent copies of live SQLite databases, for a backup to read: deploy/backup.yaml runs it. A copy of the file a writer is using can be torn,
// so this uses SQLite's online backup, through better-sqlite3, as `sqlite3 .backup` does. Each source is opened read-only, so nothing is
// created beside it, and each copy is checked before it replaces the last one.
// Usage: snapshot-db.mjs <source> <target> [<source> <target> ...]
import { chownSync, existsSync, renameSync, rmSync, statSync } from "node:fs";
import Database from "better-sqlite3";

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

const args = process.argv.slice(2);
if (args.length === 0 || args.length % 2 !== 0) {
  console.error("usage: snapshot-db.mjs <source> <target> [<source> <target> ...]");
  process.exit(2);
}

let failed = false;
for (let i = 0; i < args.length; i += 2) {
  const source = args[i];
  const target = args[i + 1];
  if (!existsSync(source)) {
    console.log(`skipped ${source}: there is no such database`);
    continue;
  }
  const partial = `${target}.partial`;
  rmSync(partial, { force: true });
  const db = new Database(source, { readonly: true, fileMustExist: true });
  try {
    await db.backup(partial);
  } finally {
    db.close();
    returnToOwner(source);
  }
  // The copy inherits WAL mode, and is backed up as one file, so it is switched to a rollback journal: nothing beside it to lose.
  const copy = new Database(partial);
  copy.pragma("journal_mode = DELETE");
  const check = copy.pragma("integrity_check", { simple: true });
  copy.close();
  if (check !== "ok") {
    console.error(`${source}: its copy failed SQLite's integrity check (${check}), so the last good copy stays`);
    rmSync(partial, { force: true });
    failed = true;
    continue;
  }
  renameSync(partial, target);
  console.log(`${source} -> ${target}`);
}
process.exit(failed ? 1 : 0);
