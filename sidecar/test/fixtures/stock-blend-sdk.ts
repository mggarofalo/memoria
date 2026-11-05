/**
 * Run as its own process: serves the SDK with the rerank blend back to stock, as a version
 * bump that lost the fix would, and reports what opening a backend does.
 */
import { registerHooks } from "node:module";

registerHooks({
  load(url, context, next) {
    const loaded = next(url, context);
    if (!url.endsWith("/dist/store.js") || !url.includes("qmd")) return loaded;
    const source = String(loaded.source).replaceAll("const blendedScore = r.score;", "const blendedScore = rrfWeight * rrfScore + (1 - rrfWeight) * r.score;");
    return { ...loaded, source };
  },
});

const { openBackend } = await import("../../src/store.ts");
try {
  await openBackend({ dbPath: "/never/opened/because/the/check/comes/first.sqlite", keepWarm: false });
  console.log("started");
} catch (err) {
  console.log(`refused: ${(err as Error).message}`);
}
process.exit(0);
