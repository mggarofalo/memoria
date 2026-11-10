// Prints the .NET mutation matrix as JSON: one entry for a pull request, which mutates
// only what changed, or the shards in stryker/shards.json for a full run. Each entry has
// the shard's name, its config, and the globs scripts/mutation-dotnet.sh passes to Stryker
// as --mutate, with the rest shards expanded so every file is mutated exactly once.
// Usage: node scripts/mutation-dotnet-shards.mjs [--pull-request]
import { readFileSync } from "node:fs";

export function expand(shards) {
  const restFolders = new Set(shards.filter((s) => s.rest).map((s) => `${s.config}/${s.rest}`));
  const named = (config, folder) =>
    shards.filter((s) => s.config === config && s.files).flatMap((s) => s.files).filter((f) => f.startsWith(`${folder}/`));
  const globs = (shard) => {
    if (shard.files) return shard.files.map((f) => `**/${f}`);
    if (shard.rest === undefined) return [];
    if (shard.rest !== "") return [`**/${shard.rest}/**/*.cs`, ...named(shard.config, shard.rest).map((f) => `!**/${f}`)];
    const taken = [...restFolders].filter((k) => k.startsWith(`${shard.config}/`)).map((k) => k.slice(shard.config.length + 1));
    return ["**/*.cs", ...taken.map((folder) => `!**/${folder}/**/*.cs`)];
  };
  return shards.map((s) => ({ name: s.name, configs: s.config, mutate: globs(s).join(" ") }));
}

if (import.meta.url === `file://${process.argv[1]}` || process.argv[1]?.endsWith("mutation-dotnet-shards.mjs")) {
  const pullRequest = process.argv.includes("--pull-request");
  const { shards } = JSON.parse(readFileSync(new URL("../stryker/shards.json", import.meta.url), "utf8"));
  process.stdout.write(JSON.stringify(pullRequest ? [{ name: "", configs: "", mutate: "" }] : expand(shards)));
}
