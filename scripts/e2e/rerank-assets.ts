/**
 * `node scripts/e2e/rerank-assets.ts <dir>`: puts the files rerank-assets.json names in <dir>, each checked
 * against its SHA-256. A file already there with the right hash is kept, which is what makes a CI cache
 * work. A download is tried a few times, as networks hiccup, and then the run stops with a message that
 * names the file and why, so a missing model can never pass for a failing search.
 */
import { createHash } from "node:crypto";
import { createReadStream, createWriteStream, existsSync, mkdirSync, readFileSync, renameSync, rmSync } from "node:fs";
import path from "node:path";
import { Readable } from "node:stream";
import { pipeline } from "node:stream/promises";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

export interface Asset {
  name: string;
  file: string;
  url: string;
  sha256: string;
  bytes: number;
  /** Only fetched on this platform, as `<os>-<arch>`. */
  platform?: string;
  /** A tarball, unpacked beside itself into a folder named for it. */
  extract?: boolean;
}

export const MANIFEST = path.join(import.meta.dirname, "rerank-assets.json");
const ATTEMPTS = 4;
const ATTEMPT_TIMEOUT_MS = 10 * 60_000;

export function loadAssets(manifest = MANIFEST): Asset[] {
  return (JSON.parse(readFileSync(manifest, "utf8")) as { assets: Asset[] }).assets;
}

export async function sha256Of(file: string): Promise<string> {
  const hash = createHash("sha256");
  await pipeline(createReadStream(file), hash);
  return hash.digest("hex");
}

type Fetcher = (url: string, init: RequestInit) => Promise<Response>;
type Sleeper = (ms: number) => Promise<void>;

/** One download into a temporary file beside the target, which is renamed only once its hash matches. */
async function downloadOnce(asset: Asset, target: string, fetcher: Fetcher): Promise<void> {
  const partial = `${target}.partial`;
  rmSync(partial, { force: true });
  try {
    const response = await fetcher(asset.url, { redirect: "follow", signal: AbortSignal.timeout(ATTEMPT_TIMEOUT_MS) });
    if (!response.ok || !response.body) throw new Error(`HTTP ${response.status}`);
    await pipeline(Readable.fromWeb(response.body as import("node:stream/web").ReadableStream), createWriteStream(partial));
    const actual = await sha256Of(partial);
    if (actual !== asset.sha256) throw new Error(`SHA-256 ${actual}, expected ${asset.sha256}`);
    renameSync(partial, target);
  } finally {
    rmSync(partial, { force: true });
  }
}

export async function fetchAsset(asset: Asset, dir: string, fetcher: Fetcher = fetch, sleep: Sleeper = (ms) => new Promise((r) => setTimeout(r, ms))): Promise<"cached" | "downloaded"> {
  mkdirSync(dir, { recursive: true });
  const target = path.join(dir, asset.file);
  if (existsSync(target) && (await sha256Of(target)) === asset.sha256) return "cached";
  rmSync(target, { force: true });
  const failures: string[] = [];
  for (let attempt = 1; attempt <= ATTEMPTS; attempt++) {
    try {
      await downloadOnce(asset, target, fetcher);
      return "downloaded";
    } catch (err) {
      failures.push(`attempt ${attempt}: ${err instanceof Error ? err.message : String(err)}`);
      if (attempt < ATTEMPTS) await sleep(5_000 * 2 ** (attempt - 1));
    }
  }
  throw new Error(`Could not fetch ${asset.name} (${asset.file}) from ${asset.url}: ${failures.join("; ")}. Nothing partial was kept. This is a download failure, not a test failure: run the job again.`);
}

export function platformOf(): string {
  return `${process.platform}-${process.arch}`;
}

/** Unpacks a tarball once, into a folder named for it, and returns that folder. */
export function extracted(dir: string, asset: Asset): string {
  const into = path.join(dir, asset.file.replace(/\.tar\.gz$/, ""));
  if (!existsSync(into)) {
    mkdirSync(`${into}.partial`, { recursive: true });
    const result = spawnSync("tar", ["-xzf", path.join(dir, asset.file), "-C", `${into}.partial`], { encoding: "utf8" });
    if (result.status !== 0) {
      rmSync(`${into}.partial`, { recursive: true, force: true });
      throw new Error(`Could not unpack ${asset.file}: ${result.stderr}`);
    }
    renameSync(`${into}.partial`, into);
  }
  return into;
}

async function main(): Promise<number> {
  const dir = process.argv[2];
  if (!dir) {
    console.error("usage: node scripts/e2e/rerank-assets.ts <dir>");
    return 2;
  }
  for (const asset of loadAssets()) {
    if (asset.platform && asset.platform !== platformOf()) {
      console.log(`skip ${asset.file}: built for ${asset.platform}, this is ${platformOf()}`);
      continue;
    }
    const started = Date.now();
    try {
      const how = await fetchAsset(asset, dir);
      if (asset.extract) extracted(dir, asset);
      console.log(`${how} ${asset.file} (${(asset.bytes / 2 ** 20).toFixed(0)} MiB) in ${((Date.now() - started) / 1000).toFixed(1)} s`);
    } catch (err) {
      console.error(`::error title=Model download failed::${err instanceof Error ? err.message : String(err)}`);
      return 1;
    }
  }
  return 0;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) process.exitCode = await main();
