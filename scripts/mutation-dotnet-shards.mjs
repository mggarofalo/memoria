// The shards of a full .NET mutation run, from stryker/shards.json, with each shard's
// globs expanded so every source file is mutated exactly once.
//   node scripts/mutation-dotnet-shards.mjs                    every shard, as JSON
//   node scripts/mutation-dotnet-shards.mjs --names            one name per line
//   node scripts/mutation-dotnet-shards.mjs --shard <name> --field configs|mutate|slug
// scripts/mutation-dotnet.sh reads the last form when MUTATION_SHARD is set.
//
// Shards of configs on one project share its files: a file that a shard of one config
// names or takes as rest is left out of the other configs' rest shards. That is how
// security-core's shards take Core's redaction files from core's.
import { readFileSync } from "node:fs";

const projectOf = (config) =>
  JSON.parse(readFileSync(new URL(`../stryker/${config}.json`, import.meta.url), "utf8"))["stryker-config"].project;

function expand(shards) {
  const project = new Map(shards.map((s) => [s.config, projectOf(s.config)]));
  const sameProject = (shard) => shards.filter((s) => project.get(s.config) === project.get(shard.config));
  const globs = (shard) => {
    if (shard.files) return shard.files.map((f) => `**/${f}`);
    if (shard.rest === undefined) return [];
    const others = sameProject(shard);
    const named = others.filter((s) => s.files).flatMap((s) => s.files);
    if (shard.rest !== "") {
      return [`**/${shard.rest}/**/*.cs`, ...named.filter((f) => f.startsWith(`${shard.rest}/`)).map((f) => `!**/${f}`)];
    }
    const taken = others.filter((s) => s.rest !== undefined && s.rest !== "").map((s) => s.rest);
    const loose = named.filter((f) => !taken.some((folder) => f.startsWith(`${folder}/`)));
    return ["**/*.cs", ...taken.map((folder) => `!**/${folder}/**/*.cs`), ...loose.map((f) => `!**/${f}`)];
  };
  return shards.map((s) => ({ name: s.name, slug: s.name.replace(/[^a-z0-9]+/gi, "-").toLowerCase(), configs: s.config, mutate: globs(s).join(" ") }));
}

const { shards } = JSON.parse(readFileSync(new URL("../stryker/shards.json", import.meta.url), "utf8"));
const all = expand(shards);
const arg = (flag) => {
  const at = process.argv.indexOf(flag);
  return at < 0 ? undefined : process.argv[at + 1];
};

if (process.argv.includes("--names")) {
  process.stdout.write(all.map((s) => s.name).join("\n") + "\n");
} else if (arg("--shard") !== undefined) {
  const shard = all.find((s) => s.name === arg("--shard"));
  if (!shard) {
    process.stderr.write(`No shard is named "${arg("--shard")}". See stryker/shards.json.\n`);
    process.exit(2);
  }
  process.stdout.write(String(shard[arg("--field") ?? "mutate"] ?? ""));
} else {
  process.stdout.write(JSON.stringify(all));
}
