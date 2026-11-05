/**
 * Is the SDK's patch really in the code that will run? `npm ci --ignore-scripts` leaves it
 * out without a word, and a search then freezes on a large document, so the sidecar
 * refuses to start. This looks at the functions rather than any marker. The two scans are
 * judged by how many items they read, which depends on no clock.
 */

interface Fence {
  start: number;
  end: number;
}

interface BreakPoint {
  pos: number;
  score: number;
}

export interface SdkStoreFunctions {
  isInsideCodeFence(pos: number, fences: Fence[]): boolean;
  findBestCutoff(points: BreakPoint[], target: number, window: number, decay: number, fences: Fence[]): number;
  scanBreakPoints(text: string): unknown;
  searchVec(...args: never[]): unknown;
  hybridQuery(...args: never[]): unknown;
  structuredSearch(...args: never[]): unknown;
}

/** A binary search over this many items reads a few dozen. A walk reads them all. */
const PROBE_ITEMS = 4096;
const MOST_READS = 200;

function readsWhile(items: number, build: (i: number, count: () => void) => object, run: (list: never[]) => void): number {
  let reads = 0;
  const list = Array.from({ length: items }, (_, i) => build(i, () => reads++));
  run(list as never[]);
  return reads;
}

function fenceScanIsLinear(sdk: SdkStoreFunctions): boolean {
  const reads = readsWhile(
    PROBE_ITEMS,
    (i, count) => ({
      get start() {
        count();
        return i * 10;
      },
      end: i * 10 + 5,
    }),
    (fences) => sdk.isInsideCodeFence((PROBE_ITEMS - 1) * 10 + 2, fences),
  );
  return reads > MOST_READS;
}

function cutoffScanIsLinear(sdk: SdkStoreFunctions): boolean {
  const reads = readsWhile(
    PROBE_ITEMS,
    (i, count) => ({
      get pos() {
        count();
        return i * 10;
      },
      score: 1,
    }),
    (points) => sdk.findBestCutoff(points, (PROBE_ITEMS - 1) * 10, 20, 0.7, []),
  );
  return reads > MOST_READS;
}

const yields = (fn: unknown): boolean => String(fn).includes("letOthersRun()");

/** Names what is missing. Empty means every fix is in place. */
export function missingSdkFixes(sdk: SdkStoreFunctions): string[] {
  const vec = String(sdk.searchVec);
  const checks: Array<[string, boolean]> = [
    ["isInsideCodeFence reads every fence", fenceScanIsLinear(sdk)],
    ["findBestCutoff reads every break point", cutoffScanIsLinear(sdk)],
    ["scanBreakPoints has no limit", !String(sdk.scanBreakPoints).includes("MAX_BREAK_POINTS")],
    ["searchVec reads the body once per chunk", /content\.doc as body\s+FROM content_vectors/.test(vec)],
    ["hybridQuery does not yield", !yields(sdk.hybridQuery)],
    ["structuredSearch does not yield", !yields(sdk.structuredSearch)],
  ];
  return checks.filter(([, missing]) => missing).map(([name]) => name);
}

export function requireSdkFixes(sdk: SdkStoreFunctions): void {
  const missing = missingSdkFixes(sdk);
  if (missing.length > 0) {
    throw new Error(`The QMD SDK is not patched: ${missing.join("; ")}. Run \`node scripts/patch-qmd.ts\` in the sidecar directory, or install without --ignore-scripts.`);
  }
}
