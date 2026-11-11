/**
 * One call's result, shared by every caller while it runs and for `ttlMs` after it succeeds. For work that
 * costs the event loop each time, such as the index scans behind `/ready`, which an orchestrator polls and the
 * API's anonymous `/ready` passes on. A failure is not kept: the next caller tries again. Ages are on the
 * monotonic clock by default, so a wall clock stepped back cannot keep an answer longer.
 */
export function sharedFor<T>(ttlMs: number, run: () => Promise<T>, now: () => number = () => performance.now()): () => Promise<T> {
  let current: Promise<T> | null = null;
  let freshUntil = 0;
  return () => {
    if (current && now() < freshUntil) return current;
    // Started on a microtask, so even a `run` that throws at once settles through the handlers below.
    const call: Promise<T> = Promise.resolve().then(run).then(
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
    freshUntil = Number.POSITIVE_INFINITY;
    return call;
  };
}
