/** Pure comparison functions for the parity benchmark. No I/O, so they can be tested exactly. */

/** Share of the two lists' top `k` that is common. Two empty lists agree. */
export function topKOverlap(a: readonly string[], b: readonly string[], k: number): number {
  const left = new Set(a.slice(0, k));
  const right = new Set(b.slice(0, k));
  const size = Math.max(left.size, right.size);
  if (size === 0) return 1;
  let shared = 0;
  for (const item of left) if (right.has(item)) shared++;
  return shared / size;
}

/** Average of tied ranks, 1-based. */
function ranks(values: readonly number[]): number[] {
  const order = values.map((value, index) => ({ value, index })).sort((x, y) => x.value - y.value);
  const out = new Array<number>(values.length);
  let i = 0;
  while (i < order.length) {
    let j = i;
    while (j + 1 < order.length && order[j + 1]!.value === order[i]!.value) j++;
    const rank = (i + j) / 2 + 1;
    for (let k = i; k <= j; k++) out[order[k]!.index] = rank;
    i = j + 1;
  }
  return out;
}

/**
 * Spearman rank correlation. Null when it is undefined: under two items, or when
 * one side gives every item the same score.
 */
export function spearman(a: readonly number[], b: readonly number[]): number | null {
  if (a.length !== b.length) throw new Error("spearman needs lists of equal length");
  if (a.length < 2) return null;
  const x = ranks(a);
  const y = ranks(b);
  const mean = (a.length + 1) / 2;
  let covariance = 0;
  let varianceX = 0;
  let varianceY = 0;
  for (let i = 0; i < x.length; i++) {
    const dx = x[i]! - mean;
    const dy = y[i]! - mean;
    covariance += dx * dy;
    varianceX += dx * dx;
    varianceY += dy * dy;
  }
  if (varianceX === 0 || varianceY === 0) return null;
  return covariance / Math.sqrt(varianceX * varianceY);
}

export interface ScoreDelta {
  mean: number;
  max: number;
}

export function scoreDelta(a: readonly number[], b: readonly number[]): ScoreDelta {
  if (a.length !== b.length) throw new Error("scoreDelta needs lists of equal length");
  if (a.length === 0) return { mean: 0, max: 0 };
  let sum = 0;
  let max = 0;
  for (let i = 0; i < a.length; i++) {
    const delta = Math.abs(a[i]! - b[i]!);
    sum += delta;
    max = Math.max(max, delta);
  }
  return { mean: sum / a.length, max };
}

export function cosine(a: readonly number[], b: readonly number[]): number | null {
  if (a.length !== b.length || a.length === 0) return null;
  let dot = 0;
  let left = 0;
  let right = 0;
  for (let i = 0; i < a.length; i++) {
    dot += a[i]! * b[i]!;
    left += a[i]! * a[i]!;
    right += b[i]! * b[i]!;
  }
  if (left === 0 || right === 0) return null;
  return dot / Math.sqrt(left * right);
}

/** Shared share of two sets of sub-queries, compared on type and lower-cased text. */
export function jaccard(a: ReadonlySet<string>, b: ReadonlySet<string>): number {
  const union = new Set([...a, ...b]);
  if (union.size === 0) return 1;
  let shared = 0;
  for (const item of a) if (b.has(item)) shared++;
  return shared / union.size;
}

export function expansionKey(q: { type: string; text: string }): string {
  return `${q.type}:${q.text.trim().toLowerCase()}`;
}

/** Mean of pairwise Jaccard between two groups of runs. With one group, its own distinct pairs. */
export function meanPairwiseJaccard(left: ReadonlyArray<ReadonlySet<string>>, right?: ReadonlyArray<ReadonlySet<string>>): number | null {
  const scores: number[] = [];
  if (right) {
    for (const a of left) for (const b of right) scores.push(jaccard(a, b));
  } else {
    for (let i = 0; i < left.length; i++) for (let j = i + 1; j < left.length; j++) scores.push(jaccard(left[i]!, left[j]!));
  }
  return scores.length === 0 ? null : scores.reduce((sum, s) => sum + s, 0) / scores.length;
}

export interface Summary {
  n: number;
  mean: number | null;
  min: number | null;
  p50: number | null;
  p95: number | null;
  max: number | null;
}

/** Nulls are dropped and counted out of `n`: an undefined correlation is not a zero. */
export function summarize(values: ReadonlyArray<number | null>): Summary {
  const present = values.filter((v): v is number => v !== null).sort((a, b) => a - b);
  if (present.length === 0) return { n: 0, mean: null, min: null, p50: null, p95: null, max: null };
  const at = (q: number) => present[Math.min(present.length - 1, Math.ceil(q * present.length) - 1)]!;
  return {
    n: present.length,
    mean: present.reduce((sum, v) => sum + v, 0) / present.length,
    min: present[0]!,
    p50: at(0.5),
    p95: at(0.95),
    max: present[present.length - 1]!,
  };
}
