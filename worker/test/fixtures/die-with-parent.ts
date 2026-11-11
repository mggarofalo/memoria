/**
 * Preloaded (`node --import`) into a worker entry point that a test starts, so the entry point never outlives the
 * test's process. Under mutation testing a mutant can break the worker's shutdown, and the runner then kills the
 * test's process. Without this the entry point would run on, and a full mutation run gathered hundreds of them until the
 * runner ran out of memory. Linux and macOS give an orphan a new parent; Windows ends a child with its parent's job.
 * The worker itself never loads this.
 *
 * The starter names itself in DIE_WITH_PARENT_PID, because a parent that exits at once can be gone before this
 * runs, and the parent seen here would then already be the new one.
 */
const parent = Number(process.env.DIE_WITH_PARENT_PID) || process.ppid;
const check = (): void => {
  if (process.ppid !== parent) process.kill(process.pid, "SIGKILL");
};
check();
setInterval(check, 500).unref();
