/**
 * Run as its own process: serves the SDK with one fix undone, as an install that skipped
 * the patch would, and reports what opening a backend does.
 */
import { registerHooks } from "node:module";

registerHooks({
  load(url, context, next) {
    const loaded = next(url, context);
    if (!url.endsWith("/dist/store.js") || !url.includes("qmd")) return loaded;
    const source = String(loaded.source).replace(/const after = firstAtLeast\(fences, pos, \(f\) => f\.start\);[\s\S]*?return f !== undefined && pos > f\.start && pos < f\.end;/, "return fences.some(f => pos > f.start && pos < f.end);");
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
