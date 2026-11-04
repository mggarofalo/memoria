/** Cosine between two vectors. Zero when their lengths differ, since that is never the same model. */
export function cosineOf(a: readonly number[], b: readonly number[]): number {
  if (a.length !== b.length) return 0;
  let dot = 0;
  let left = 0;
  let right = 0;
  for (let i = 0; i < a.length; i++) {
    dot += a[i]! * b[i]!;
    left += a[i]! * a[i]!;
    right += b[i]! * b[i]!;
  }
  const norm = Math.sqrt(left * right);
  return norm === 0 ? 0 : dot / norm;
}
