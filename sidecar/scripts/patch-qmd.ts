/**
 * Fixes scans in the QMD SDK that let one large document freeze or kill the
 * process. They sit inside functions the SDK calls directly, so no wrapper can reach
 * them. The results do not change: test/hardening.test.ts holds the patched code to the
 * original's output. Drop this when the SDK has the fixes.
 *
 * A plain script rather than patch-package, which would add a dozen runtime dependencies
 * to run one replacement. It stops the install if the SDK's text has moved, so a version
 * bump cannot silently lose the fixes. Running it twice changes nothing. The sidecar also
 * checks the functions themselves at start: see src/sdk-check.ts.
 */
import { readFileSync, writeFileSync } from "node:fs";
import { pathToFileURL } from "node:url";

const BINARY_SEARCH = `
// Index of the first item whose key is at least \`target\`, in a sorted list.
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
// A turn for other requests, between steps that read whole documents.
const letOthersRun = () => new Promise((resolve) => setImmediate(resolve));
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
    to: `        await letOthersRun();
        const chunks = await chunkDocumentAsync(cand.body, undefined, undefined, undefined, cand.file, chunkStrategy);`,
  },
  {
    why: "The same, in structured search.",
    from: `        const chunks = await chunkDocumentAsync(cand.body, undefined, undefined, undefined, cand.file, ssChunkStrategy);`,
    to: `        await letOthersRun();
        const chunks = await chunkDocumentAsync(cand.body, undefined, undefined, undefined, cand.file, ssChunkStrategy);`,
  },
  ...[
    "            const ftsResults = store.searchFTS(q.query, 20, collection);",
    "            const vecResults = await store.searchVec(vecQueries[i].text, embedModel, 20, collection, undefined, embedding);",
    "                const ftsResults = store.searchFTS(search.query, 20, coll);",
    "                    const vecResults = await store.searchVec(vecSearches[i].query, embedModel, 20, coll, undefined, embedding);",
  ].map((line) => ({
    why: "Each sub-query reads up to 20 whole bodies, with no turn for anything else.",
    from: line,
    to: `await letOthersRun();\n${line}`,
  })),
  {
    why: "A Map holds about 16.7 million entries, so a large enough document of blank lines threw, failing every search and embed job that reached it.",
    from: `    const seen = new Map(); // pos -> best break point at that pos
    for (const [pattern, score, type] of BREAK_PATTERNS) {
        for (const match of text.matchAll(pattern)) {
            const pos = match.index;
            const existing = seen.get(pos);`,
    to: `    const seen = new Map(); // pos -> best break point at that pos
    // More than any document within the 2 MiB ingest cap can have, so those chunk as before.
    // Past it, a document is cut at the plain chunk size.
    const MAX_BREAK_POINTS = 2_200_000;
    for (const [pattern, score, type] of BREAK_PATTERNS) {
        for (const match of text.matchAll(pattern)) {
            const pos = match.index;
            const existing = seen.get(pos);
            if (!existing && seen.size >= MAX_BREAK_POINTS)
                break;`,
  },
];

export function patchedStore(source: string): string {
  // Git on Windows or a package tool may have changed the line endings.
  const text = source.replace(/\r\n/g, "\n");
  let out = text;
  for (const { from, to, why } of EDITS) {
    if (out.includes(to)) continue;
    if (out.split(from).length !== 2) throw new Error(`The QMD SDK has changed, so this fix cannot be applied: ${why} Review sidecar/scripts/patch-qmd.ts.`);
    out = out.replace(from, () => to);
  }
  return out;
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const file = new URL("../node_modules/@tobilu/qmd/dist/store.js", import.meta.url);
  writeFileSync(file, patchedStore(readFileSync(file, "utf8")));
}
