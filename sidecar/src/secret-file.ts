import { readFileSync, statSync, type Stats } from "node:fs";

/**
 * Read a shared secret at startup from a file named by `<NAME>_FILE`, or from
 * `<NAME>` itself for development.
 *
 * sidecar/src/secret-file.ts and worker/src/secret-file.ts are the same file,
 * because the two packages share no code. A test holds them identical.
 *
 * Errors name the variable and the path and never hold the file's content.
 */

const MAX_SECRET_FILE_BYTES = 4096;

function statOrThrow(filePath: string, variable: string): Stats {
  try {
    return statSync(filePath);
  } catch (err) {
    throw new Error(`${variable}: cannot read "${filePath}" (${(err as NodeJS.ErrnoException).code ?? "unknown error"})`);
  }
}

/**
 * Refused rather than warned about, as ssh does with a private key. A secret
 * others can read is already exposed, and a warning in a service log is one
 * nobody reads, while a service that will not start gets looked at. Group
 * access counts: a group is more people than this needs.
 *
 * Windows reports no ACL through these bits, so nothing can be checked here.
 * The install script sets the ACL and verifies it.
 */
function assertPrivate(stats: Stats, filePath: string, variable: string, platform: NodeJS.Platform): void {
  if (platform !== "win32" && (stats.mode & 0o077) !== 0) {
    const mode = (stats.mode & 0o777).toString(8).padStart(3, "0");
    throw new Error(`${variable}: "${filePath}" has mode ${mode}, so other users may be able to read it. Run: chmod 600 "${filePath}"`);
  }
}

/** One line of UTF-8, with surrounding whitespace removed. An editor's trailing newline or a BOM is harmless. */
function parseSecret(raw: string, filePath: string, variable: string): string {
  // Windows PowerShell's `>` writes UTF-16, which reads back with NULs or replacement characters.
  if (raw.includes("\0") || raw.includes("�")) throw new Error(`${variable}: "${filePath}" is not UTF-8`);
  const secret = raw.trim();
  if (secret === "") throw new Error(`${variable}: "${filePath}" is empty`);
  if (/[\r\n]/.test(secret)) throw new Error(`${variable}: "${filePath}" must hold one line`);
  return secret;
}

export function readSecretFile(filePath: string, variable: string, platform: NodeJS.Platform = process.platform): string {
  const stats = statOrThrow(filePath, variable);
  if (!stats.isFile()) throw new Error(`${variable}: "${filePath}" is not a file`);
  assertPrivate(stats, filePath, variable, platform);
  if (stats.size > MAX_SECRET_FILE_BYTES) throw new Error(`${variable}: "${filePath}" is larger than ${MAX_SECRET_FILE_BYTES} bytes, so it is not a secret file`);
  let raw: string;
  try {
    raw = readFileSync(filePath, "utf8");
  } catch (err) {
    throw new Error(`${variable}: cannot read "${filePath}" (${(err as NodeJS.ErrnoException).code ?? "unknown error"})`);
  }
  return parseSecret(raw, filePath, variable);
}

/**
 * The secret from `<name>_FILE` or `<name>`, or undefined when neither is set.
 * Both set is refused: with one quietly winning, a stale value could stay in
 * use after a rotation.
 */
export function resolveSecret(env: NodeJS.ProcessEnv, name: string, platform: NodeJS.Platform = process.platform): string | undefined {
  const fileVariable = `${name}_FILE`;
  const filePath = env[fileVariable]?.trim() || undefined;
  const direct = env[name]?.trim() || undefined;
  if (filePath === undefined) return direct;
  if (direct !== undefined) throw new Error(`Set only one of ${name} and ${fileVariable}`);
  return readSecretFile(filePath, fileVariable, platform);
}
