/**
 * Fixes scans in the QMD SDK that let one large document freeze or kill the
 * process. They sit inside functions the SDK calls directly, so no wrapper can reach
 * them. The results do not change: test/qmd-patch.test.ts holds the patched code to the
 * original's output. Drop this when the SDK has the fixes.
 *
 * A plain script rather than patch-package, which would add a dozen runtime dependencies
 * to run one replacement. It stops the install if the SDK's text has moved, so a version
 * bump cannot silently lose the fixes. Running it twice changes nothing.
 */
import { readFileSync, writeFileSync } from "node:fs";
import { pathToFileURL } from "node:url";

export const PATCH_MARKER = "memoria-patched";

const BINARY_SEARCH = `
// ${PATCH_MARKER}: index of the first item whose key is at least \`target\`, in a sorted list.
function firstAtLeast(items, target, keyOf) {
    let low = 0;
    let high = items.length;
    while (low < high) {
        const middle = (low + high) >>> 1;
        if (keyOf(items[middle]) < target)
            low = middle + 1;
        else
            high = middle;
    }
    return low;
}
`;

const EDITS = [
  {
    why: "A linear search of every fence, for every break point: fences are sorted and do not overlap.",
    from: `export function isInsideCodeFence(pos, fences) {
    return fences.some(f => pos > f.start && pos < f.end);
}`,
    to: `${BINARY_SEARCH}export function isInsideCodeFence(pos, fences) {
    const after = firstAtLeast(fences, pos, (f) => f.start);
    const f = fences[after - 1];
    return f !== undefined && pos > f.start && pos < f.end;
}`,
  },
  {
    why: "A walk from the start of the document for every chunk: break points are sorted.",
    from: `    for (const bp of breakPoints) {
        if (bp.pos < windowStart)
            continue;
        if (bp.pos > targetCharPos)
            break; // sorted, so we can stop`,
    to: `    for (let i = firstAtLeast(breakPoints, windowStart, (b) => b.pos); i < breakPoints.length; i++) {
        const bp = breakPoints[i];
        if (bp.pos > targetCharPos)
            break; // sorted, so we can stop`,
  },
  {
    why: "The whole body once per matching chunk, though only one row per file is kept.",
    from: `      d.title,
      content.doc as body
    FROM content_vectors cv
    JOIN documents d ON d.hash = cv.hash AND d.active = 1
    JOIN content ON content.hash = d.hash
    WHERE cv.hash || '_' || cv.seq IN (\${placeholders})`,
    to: `      d.title
    FROM content_vectors cv
    JOIN documents d ON d.hash = cv.hash AND d.active = 1
    JOIN content ON content.hash = d.hash
    WHERE cv.hash || '_' || cv.seq IN (\${placeholders})`,
  },
  {
    why: "The body is read here instead, once per file that survived, and only for those.",
    from: `    return Array.from(seen.values())
        .sort((a, b) => a.bestDist - b.bestDist)
        .slice(0, limit)
        .map(({ row, bestDist }) => {
        const collectionName = row.filepath.split('//')[1]?.split('/')[0] || "";`,
    to: `    const bodyOf = db.prepare(\`SELECT doc FROM content WHERE hash = ?\`);
    return Array.from(seen.values())
        .sort((a, b) => a.bestDist - b.bestDist)
        .slice(0, limit)
        .map(({ row, bestDist }) => {
        row.body = bodyOf.get(row.hash)?.doc ?? "";
        const collectionName = row.filepath.split('//')[1]?.split('/')[0] || "";`,
  },
  {
    why: "Every candidate chunked with no turn for anything else, so one search shuts out every other request.",
    from: `        const chunks = await chunkDocumentAsync(cand.body, undefined, undefined, undefined, cand.file, chunkStrategy);`,
    to: `        await new Promise((resolve) => setImmediate(resolve));
        const chunks = await chunkDocumentAsync(cand.body, undefined, undefined, undefined, cand.file, chunkStrategy);`,
  },
  {
    why: "The same, in structured search.",
    from: `        const chunks = await chunkDocumentAsync(cand.body, undefined, undefined, undefined, cand.file, ssChunkStrategy);`,
    to: `        await new Promise((resolve) => setImmediate(resolve));
        const chunks = await chunkDocumentAsync(cand.body, undefined, undefined, undefined, cand.file, ssChunkStrategy);`,
  },
];

export function patchedStore(source: string): string {
  // Git on Windows or a package tool may have changed the line endings.
  const text = source.replace(/\r\n/g, "\n");
  if (text.includes(PATCH_MARKER)) return text;
  let out = text;
  for (const { from, to, why } of EDITS) {
    if (out.split(from).length !== 2) throw new Error(`The QMD SDK has changed, so this fix cannot be applied: ${why} Review sidecar/scripts/patch-qmd.mjs.`);
    out = out.replace(from, () => to);
  }
  return out;
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const file = new URL("../node_modules/@tobilu/qmd/dist/store.js", import.meta.url);
  writeFileSync(file, patchedStore(readFileSync(file, "utf8")));
}
