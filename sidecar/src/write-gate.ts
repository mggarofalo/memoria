/**
 * Lets a write on the main thread's connection in between a scan's groups of writes.
 *
 * Both connections take SQLite's one write lock, and a writer that finds it held waits by
 * polling, sleeping up to 100 ms between tries. The scan thread takes the lock again
 * microseconds after each commit, so a poll almost never lands in the gap: measured on
 * Windows, a write on the main thread failed after a 250 ms timeout while a scan ran, and
 * with the SDK's 120 s timeout it would have held the event loop for the whole scan.
 *
 * So the main thread says when it is writing, in shared memory, and the scan thread, between
 * groups, waits while it is. The main thread's write then waits for at most the group in
 * progress and one poll. Searches write on the main thread: the SDK caches rerank scores and
 * expansions as they run, from statements prepared at that moment, so `guard` wraps the
 * connection itself rather than each caller.
 */
export class WriteGate {
  /** How many writes the main thread has under way. Shared with each scan thread. */
  readonly writers = new Int32Array(new SharedArrayBuffer(4));

  /** Run one write, with the scan thread holding off between its groups until it is done. */
  around<T>(work: () => T): T {
    Atomics.add(this.writers, 0, 1);
    try {
      return work();
    } finally {
      Atomics.sub(this.writers, 0, 1);
      Atomics.notify(this.writers, 0);
    }
  }

  /** Route a better-sqlite3 connection's writes through `around`: each statement that writes, and each `exec`. */
  guard(db: GuardedDb): void {
    const prepare = db.prepare.bind(db);
    const exec = db.exec.bind(db);
    db.prepare = (sql: string) => {
      const statement = prepare(sql);
      if (statement.reader) return statement;
      const run = statement.run.bind(statement);
      statement.run = (...args: unknown[]) => this.around(() => run(...args));
      return statement;
    };
    db.exec = (sql: string) => this.around(() => exec(sql));
  }
}

export interface GuardedStatement {
  readonly reader: boolean;
  run(...args: unknown[]): unknown;
}

export interface GuardedDb {
  prepare(sql: string): GuardedStatement;
  exec(sql: string): unknown;
}

/**
 * On the scan thread, between groups: wait while the main thread is writing, for at most
 * `capMs`, so a main-thread write that is itself stuck behind another process cannot hold
 * the scan for good. Past that, the scan's next group waits for the lock as any writer does.
 */
export function yieldToMainWrites(writers: Int32Array, capMs: number, now: () => number = () => performance.now()): void {
  const deadline = now() + capMs;
  for (;;) {
    const count = Atomics.load(writers, 0);
    const left = deadline - now();
    if (count === 0 || left <= 0) return;
    Atomics.wait(writers, 0, count, left);
  }
}
