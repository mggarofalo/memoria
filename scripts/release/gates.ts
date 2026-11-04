/**
 * Two release gates as pure functions: the release notes carry no tracker
 * references, and the commit passed the checks `main` requires. Required checks
 * come from the ruleset, not a list here, so they cannot fall out of step with it.
 */

/** Older commit subjects hold tracker IDs; the repository is public, so notes never may. */
const TRACKER_REFERENCE = /MEMORIA-[0-9]+/gi;

export interface CheckRun {
  name: string;
  status: string;
  conclusion: string | null;
}

export function trackerReferences(text: string): string[] {
  return text.match(TRACKER_REFERENCE) ?? [];
}

/** The status-check contexts in a `GET /repos/{repo}/rules/branches/{branch}` response. */
export function requiredChecks(rules: unknown): string[] {
  if (!Array.isArray(rules)) return [];
  return rules.flatMap((rule) => (rule.type === "required_status_checks" ? rule.parameters.required_status_checks.map((check: { context: string }) => check.context) : []));
}

/** Each required check that has not finished successfully on this commit, with what it did instead. */
export function unmetChecks(required: readonly string[], runs: readonly CheckRun[]): string[] {
  return required.flatMap((name) => {
    const matching = runs.filter((run) => run.name === name);
    if (matching.length === 0) return [`${name}: no run found`];
    return matching.filter((run) => run.conclusion !== "success").map((run) => `${name}: ${run.conclusion ?? run.status}`);
  });
}
