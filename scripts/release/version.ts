/**
 * The release version as a pure function of tags and commit messages. The same
 * history always gives the same answer; docs/releasing.md states the rules.
 */

export interface Semver {
  major: number;
  minor: number;
  patch: number;
}

export type Bump = "major" | "minor" | "patch" | "none";

/** The first tag is fixed, whatever came before it. */
export const FIRST_VERSION: Semver = { major: 0, minor: 1, patch: 0 };

const STRICT_TAG = /^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;
const SUBJECT = /^([a-z]+)(\([^)\n]*\))?(!)?: \S/i;
const BREAKING_FOOTER = /^BREAKING[ -]CHANGE: /m;
const BUMP_ORDER: readonly Bump[] = ["none", "patch", "minor", "major"];

export function parseTag(tag: string): Semver | undefined {
  const match = STRICT_TAG.exec(tag);
  return match ? { major: Number(match[1]), minor: Number(match[2]), patch: Number(match[3]) } : undefined;
}

export function formatVersion(version: Semver): string {
  return `${version.major}.${version.minor}.${version.patch}`;
}

export function compareVersions(a: Semver, b: Semver): number {
  return a.major - b.major || a.minor - b.minor || a.patch - b.patch;
}

/** The highest strict `vX.Y.Z` tag; anything else in the list is not a release. */
export function latestTag(tags: readonly string[]): string | undefined {
  let best: { tag: string; version: Semver } | undefined;
  for (const tag of tags) {
    const version = parseTag(tag);
    if (version && (!best || compareVersions(version, best.version) > 0)) best = { tag, version };
  }
  return best?.tag;
}

/** What one full commit message asks for. */
export function classifyCommit(message: string): Bump {
  if (BREAKING_FOOTER.test(message)) return "major";
  const subject = SUBJECT.exec(message);
  if (!subject) return "none";
  if (subject[3]) return "major";
  const type = (subject[1] as string).toLowerCase();
  if (type === "feat") return "minor";
  return type === "fix" || type === "perf" ? "patch" : "none";
}

export function highestBump(messages: readonly string[]): Bump {
  return messages.reduce<Bump>((best, message) => {
    const bump = classifyCommit(message);
    return BUMP_ORDER.indexOf(bump) > BUMP_ORDER.indexOf(best) ? bump : best;
  }, "none");
}

/** While 0.x, a breaking change is a minor bump: 1.0.0 is a decision, not an accident of a commit footer. */
export function applyBump(current: Semver, bump: Exclude<Bump, "none">): Semver {
  const effective = bump === "major" && current.major === 0 ? "minor" : bump;
  if (effective === "major") return { major: current.major + 1, minor: 0, patch: 0 };
  if (effective === "minor") return { major: current.major, minor: current.minor + 1, patch: 0 };
  return { major: current.major, minor: current.minor, patch: current.patch + 1 };
}

/** The next version, or undefined when there is nothing to release. */
export function nextVersion(lastTag: string | undefined, messages: readonly string[]): string | undefined {
  const bump = highestBump(messages);
  if (bump === "none") return undefined;
  const last = lastTag === undefined ? undefined : parseTag(lastTag);
  return formatVersion(last ? applyBump(last, bump) : FIRST_VERSION);
}

/** Why `tag` is not the release this history calls for, or undefined when it is. */
export function tagProblem(tag: string, lastTag: string | undefined, messages: readonly string[]): string | undefined {
  // Leaving 0.x is the one deliberate exception to the computed answer.
  if (tag === "v1.0.0" && lastTag !== undefined && parseTag(lastTag)?.major === 0) return undefined;
  const next = nextVersion(lastTag, messages);
  if (next === undefined) return `no releasable commits since ${lastTag ?? "the start of history"}`;
  return tag === `v${next}` ? undefined : `the commits since ${lastTag ?? "the start of history"} call for v${next}, not ${tag}`;
}
