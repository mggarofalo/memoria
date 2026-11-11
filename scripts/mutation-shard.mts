/**
 * Splits a package's source files into shards for a dispatched StrykerJS run (mutation.yml) that one job could not finish in time:
 *
 *   node scripts/mutation-shard.mts <package> <shard>/<shards>
 *
 * prints that shard's files, comma-separated, for `stryker run --mutate`. Every file the package's Stryker config
 * mutates lands in exactly one shard, so a new file is covered without editing anything. Files are weighed by their
 * non-blank lines, which track the number of mutants closely, and dealt out heaviest first to the lightest shard.
 */
import { readdirSync, readFileSync, statSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

export interface Weighed {
  file: string;
  weight: number;
}

/** Heaviest first, each to the lightest shard so far; ties go to the lower shard, and files tie on name, so the split is the same on every machine. */
export function assignShards(files: readonly Weighed[], shards: number): string[][] {
  if (!Number.isInteger(shards) || shards < 1) throw new Error(`shards must be a positive whole number, not ${shards}`);
  const out: string[][] = Array.from({ length: shards }, () => []);
  const load: number[] = new Array<number>(shards).fill(0);
  const ordered = [...files].sort((a, b) => b.weight - a.weight || (a.file < b.file ? -1 : a.file > b.file ? 1 : 0));
  for (const { file, weight } of ordered) {
    let lightest = 0;
    for (let i = 1; i < shards; i++) if (load[i]! < load[lightest]!) lightest = i;
    out[lightest]!.push(file);
    load[lightest]! += weight;
  }
  return out.map((group) => group.sort());
}

/** `2/8` means the second of eight. */
export function parseShard(spec: string): { index: number; shards: number } {
  const match = /^(\d+)\/(\d+)$/.exec(spec);
  const index = Number(match?.[1]);
  const shards = Number(match?.[2]);
  if (!match || shards < 1 || index < 1 || index > shards) throw new Error(`a shard is <index>/<count> with 1 <= index <= count, not "${spec}"`);
  return { index, shards };
}

/** The files the package's Stryker config mutates: its `<package>/src/**` pattern, less any `!` exclusions it lists. */
export function mutatedFiles(root: string, pkg: string): string[] {
  const config = JSON.parse(readFileSync(path.join(root, pkg, "stryker.config.json"), "utf8")) as { mutate: string[] };
  const excluded = new Set(config.mutate.filter((p) => p.startsWith("!")).map((p) => p.slice(1)));
  const files: string[] = [];
  const walk = (dir: string): void => {
    for (const name of readdirSync(path.join(root, dir)).sort()) {
      const rel = `${dir}/${name}`;
      if (statSync(path.join(root, rel)).isDirectory()) walk(rel);
      else if (rel.endsWith(".ts") && !excluded.has(rel)) files.push(rel);
    }
  };
  walk(`${pkg}/src`);
  return files;
}

export function weigh(root: string, files: readonly string[]): Weighed[] {
  return files.map((file) => ({ file, weight: readFileSync(path.join(root, file), "utf8").split("\n").filter((line) => line.trim() !== "").length }));
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [pkg, spec] = process.argv.slice(2);
  if (!pkg || !spec) throw new Error("usage: node scripts/mutation-shard.mts <package> <index>/<count>");
  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  const { index, shards } = parseShard(spec);
  const shard = assignShards(weigh(root, mutatedFiles(root, pkg)), shards)[index - 1]!;
  if (shard.length === 0) throw new Error(`shard ${spec} of ${pkg} has no files: use fewer shards`);
  process.stdout.write(shard.join(","));
}
