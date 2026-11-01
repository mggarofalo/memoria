/**
 * QMD's `get` accepts a line-range suffix on the document reference:
 *   `path.md:100`     → from line 100
 *   `path.md:100:40`  → 40 lines starting at line 100
 *   `#abc123:100:40`  → same, by docid
 * This parser mirrors that behaviour so `/docs/{id}` accepts the same strings
 * the MCP tool does. Explicit query parameters win over the suffix.
 */
export interface LineRange {
  lookup: string;
  fromLine: number | undefined;
  maxLines: number | undefined;
}

export function parseLineRange(reference: string): LineRange {
  const range = reference.match(/:(\d+):(\d+)$/);
  if (range) {
    return {
      lookup: reference.slice(0, -range[0].length),
      fromLine: Math.max(1, parseInt(range[1]!, 10)),
      maxLines: parseInt(range[2]!, 10),
    };
  }
  const from = reference.match(/:(\d+)$/);
  if (from) {
    return {
      lookup: reference.slice(0, -from[0].length),
      fromLine: Math.max(1, parseInt(from[1]!, 10)),
      maxLines: undefined,
    };
  }
  return { lookup: reference, fromLine: undefined, maxLines: undefined };
}
