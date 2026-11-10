/**
 * Fixes scans in the QMD SDK that let one large document freeze or kill the
 * process. They sit inside functions the SDK calls directly, so no wrapper can reach
 * them. Those results do not change: test/hardening.test.ts holds the patched code to the
 * original's output. Drop this when the SDK has the fixes.
 *
 * Also carries the reranker blend fix from upstream tobi/qmd#1032, which does change
 * results, on purpose: test/rerank-blend.test.ts. And it stops the config sync deleting
 * collection records a config does not name, which a missing config did to all of them
 * (upstream tobi/qmd#1048): test/integration-collections.test.ts.
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

// The stretch before the blend, the same in hybridQuery and structuredSearch.
const BEFORE_BLEND = `    const candidateMap = new Map(candidates.map(c => [c.file, {
            displayPath: c.displayPath, title: c.title, body: c.body,
        }]));
    const rrfRankMap = new Map(candidates.map((c, i) => [c.file, i + 1]));
    const blended = reranked.map(r => {
        const rrfRank = rrfRankMap.get(r.file) || candidateLimit;
        let rrfWeight;
        if (rrfRank <= 3)
            rrfWeight = 0.75;
        else if (rrfRank <= 10)
            rrfWeight = 0.60;
        else
            rrfWeight = 0.40;
        const rrfScore = 1 / rrfRank;
`;
const STOCK_BLEND = `        const blendedScore = rrfWeight * rrfScore + (1 - rrfWeight) * r.score;`;
const FIXED_BLEND = `        const blendedScore = r.score;`;
const STOCK_SORT = `    }).sort((a, b) => b.score - a.score);`;
const FIXED_SORT = `    }).sort((a, b) => b.score - a.score || (rrfRankMap.get(a.file) ?? candidateLimit) - (rrfRankMap.get(b.file) ?? candidateLimit));`;
const BLEND_WHY = "The tiered blend is not monotonic in rank: at one reranker score, rank 11 outscores rank 3.";

// The replacement text is what tobi/qmd#1032 compiles to, comments included, so a release
// holding that change as it stands passes through untouched. Any other form of the fix
// matches neither side and stops the install.
const BLEND_EDITS = [
  {
    why: BLEND_WHY,
    from: `    // Step 7: Blend RRF position score with reranker score
    // Position-aware weights: top retrieval results get more protection from reranker disagreement
${BEFORE_BLEND}${STOCK_BLEND}`,
    to: `    // Step 7: Order by reranker score; RRF rank breaks ties.
    // The previous position-aware blend (tiered weight × 1/rank) was not monotonic in rank:
    // at equal reranker score, a candidate at RRF rank 11-40 outscored one at rank 3-10
    // (rank 3 → 0.75/3 + 0.25·s, rank 11 → 0.40/11 + 0.60·s), so strong reranker
    // judgments pushed mid-ranked hits below deep candidates.
${BEFORE_BLEND}${FIXED_BLEND}`,
  },
  {
    why: BLEND_WHY,
    from: `    // Step 6: Blend RRF position score with reranker score
${BEFORE_BLEND}${STOCK_BLEND}`,
    to: `    // Step 6: Blend RRF position score with reranker score
${BEFORE_BLEND}${FIXED_BLEND}`,
  },
  {
    why: "Equal reranker scores must keep their rank-fusion order.",
    from: `${STOCK_SORT}
    // Step 8: Dedup by file (safety net`,
    to: `${FIXED_SORT}
    // Step 8: Dedup by file (safety net`,
  },
  {
    why: "The same, in structured search.",
    from: `${STOCK_SORT}
    // Step 7: Dedup by file`,
    to: `${FIXED_SORT}
    // Step 7: Dedup by file`,
  },
];

// Every qmd CLI command, and createStore with a config, first copies the config into the
// index. A missing file reads as an empty config, so stock QMD then deletes every collection
// record, and the global context, while their documents stay (upstream tobi/qmd#1048). In the
// image the config directory is always empty, because nothing writes a config there.
const CONFIG_SYNC_EDIT = {
  why: "A missing or partial config deletes the index's collection records.",
  from: `    // Delete collections not in config
    const dbCollections = db.prepare(\`SELECT name FROM store_collections\`).all();
    for (const row of dbCollections) {
        if (!configNames.has(row.name)) {
            db.prepare(\`DELETE FROM store_collections WHERE name = ?\`).run(row.name);
        }
    }
    // Sync global context
    if (config.global_context !== undefined) {
        setStoreGlobalContext(db, config.global_context);
    }
    else {
        setStoreGlobalContext(db, undefined);
    }`,
  to: `    // Memoria: a collection the config does not name keeps its record. A missing config reads
    // as naming nothing, so stock QMD deleted them all here. \`qmd collection remove\` still
    // removes a collection. A config that names collections and no global context clears it,
    // which is how \`qmd context rm /\` works; one naming no collection, as a missing file
    // reads, leaves it.
    if (config.global_context !== undefined) {
        setStoreGlobalContext(db, config.global_context);
    }
    else if (configNames.size > 0) {
        setStoreGlobalContext(db, undefined);
    }`,
};

const EDITS = [
  ...BLEND_EDITS,
  CONFIG_SYNC_EDIT,
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
