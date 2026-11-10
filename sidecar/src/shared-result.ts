/**
 * One call's result, shared by every caller while it runs and for `ttlMs` after it succeeds. For work that
 * costs the event loop each time, such as the index scans behind `/ready`, which an orchestrator polls and the
 * API's anonymous `/ready` passes on. A failure is not kept: the next caller tries again.
 */
export function sharedFor<T>(ttlMs: number, run: () => Promise<T>, now: () => number = Date.now): () => Promise<T> {
  let current: Promise<T> | null = null;
  let freshUntil = 0;
  return () => {
    if (current && now() < freshUntil) return current;
    freshUntil = Number.POSITIVE_INFINITY;
    const call = run().then(
      (value) => {
        if (current === call) freshUntil = now() + ttlMs;
        return value;
      },
      (err: unknown) => {
        if (current === call) {
          current = null;
          freshUntil = 0;
        }
        throw err;
      },
    );
    current = call;
    return call;
  };
}
