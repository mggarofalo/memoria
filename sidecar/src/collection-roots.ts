import { readdirSync, writeFileSync } from "node:fs";
import path from "node:path";
import { collectionsWithDocuments, collectionsWithoutDocuments, type PurgeDb } from "./purge.ts";

/**
 * A file the sidecar keeps in each collection root that has held a document, so a later
 * scan can tell a root whose documents were all deleted from one whose filesystem is not
 * there. A volume or mount that failed leaves the folder under it, empty and without this
 * file. QMD's pattern reads only `.md` files, so it is never indexed.
 */
export const ROOT_MARKER = ".memoria-root";

const MARKER_TEXT = "This folder is a Memoria collection root. The sidecar keeps this file to tell a folder emptied by deletes from a mount that is missing. Leave it here.\n";

/**
 * What a scan that found no document in a collection can trust about its root:
 * - `missing`: it is not there, or cannot be read, as when a mount went away.
 * - `unmarked`: it is there and readable but has no marker, so it may be the empty folder under a mount that failed.
 * - `marked`: it is there with its marker, so its documents really were deleted.
 */
export type RootState = "missing" | "unmarked" | "marked";

export function rootState(root: string): RootState {
  let names: string[];
  try {
    names = readdirSync(root);
  } catch {
    return "missing";
  }
  return names.includes(ROOT_MARKER) ? "marked" : "unmarked";
}

export interface CollectionRoot {
  name: string;
  path: string;
}

/**
 * After a scan: mark each root that holds documents, and name the collections whose
 * deleted documents must stay, with a warning for each. A collection the scan found
 * with no document keeps them unless its root is marked. A missing or unreadable root
 * is a mount that went away, and an unmarked one may be the folder under a mount that
 * failed, so either coming back would otherwise cost all its vectors a re-embed.
 */
export function collectionsToKeep(db: PurgeDb, roots: readonly CollectionRoot[], warn: (message: string) => void): string[] {
  const where = new Map(roots.map((root) => [root.name, root.path]));
  for (const name of collectionsWithDocuments(db)) {
    const root = where.get(name);
    if (root !== undefined) markRoot(root);
  }
  const keep: string[] = [];
  for (const name of collectionsWithoutDocuments(db)) {
    const root = where.get(name);
    const state = root === undefined ? "missing" : rootState(root);
    if (state === "marked") continue;
    keep.push(name);
    warn(state === "missing"
      ? `Collection ${name}: its folder is missing or cannot be read, so the scan found none of its documents. They are kept in the index, text and vectors included, until the folder is back.`
      : `Collection ${name}: its folder is there but holds no document and no ${ROOT_MARKER}, so it may be the folder under a mount that failed. Its documents are kept in the index until it holds one again. If they really were all deleted, put an empty ${ROOT_MARKER} in the folder and the next scan removes them.`);
  }
  return keep;
}

/** Leave the marker in a root that holds documents. Best effort: a read-only root only loses this safeguard. */
export function markRoot(root: string): void {
  try {
    writeFileSync(path.join(root, ROOT_MARKER), MARKER_TEXT, { flag: "wx" });
  } catch {
    // Already there, or the root cannot be written. Either way nothing else changes.
  }
}
