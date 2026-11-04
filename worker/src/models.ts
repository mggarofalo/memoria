import { readdirSync } from "node:fs";
import path from "node:path";

/**
 * Find a model in QMD's cache. QMD stores files as `hf_<owner>_<file>`, so a
 * match is the bare name or that name after an underscore. Partial downloads
 * carry a further suffix and so do not match.
 */
export function findModel(dir: string, file: string, list: (dir: string) => string[] = readdirSync): string {
  let entries: string[];
  try {
    entries = list(dir);
  } catch {
    throw new Error(`Model directory ${dir} cannot be read. Set MEMORIA_WORKER_MODEL_DIR or a per-role model path.`);
  }
  const wanted = file.toLowerCase();
  const match = entries
    .filter((name) => {
      const lower = name.toLowerCase();
      return lower === wanted || lower.endsWith(`_${wanted}`);
    })
    .sort()[0];
  if (match === undefined) throw new Error(`Model ${file} is not in ${dir}. Pull it with QMD, or set a per-role model path.`);
  return path.join(dir, match);
}
