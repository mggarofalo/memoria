import { HttpError } from "./errors.ts";

/**
 * Searches hold whole document bodies while they run, so memory grows with how many run
 * at once, and yielding between steps only lets more pile up. This keeps a fixed number
 * running and a short queue, and turns the rest away quickly so a caller can retry.
 */
export class SearchBusy extends HttpError {
  constructor() {
    super(503, "search_busy", "Too many searches are running. Try again shortly.");
  }
}

export interface SearchGateOptions {
  running: number;
  queued: number;
  maxWaitMs: number;
}

interface Waiter {
  admit(): void;
  timer: NodeJS.Timeout;
}

export class SearchGate {
  readonly #options: SearchGateOptions;
  readonly #waiting: Waiter[] = [];
  #running = 0;

  constructor(options: SearchGateOptions) {
    this.#options = options;
  }

  async run<T>(work: () => Promise<T>): Promise<T> {
    await this.#enter();
    try {
      return await work();
    } finally {
      this.#leave();
    }
  }

  #enter(): Promise<void> {
    if (this.#running < this.#options.running) {
      this.#running++;
      return Promise.resolve();
    }
    if (this.#waiting.length >= this.#options.queued) return Promise.reject(new SearchBusy());
    return new Promise((resolve, reject) => {
      const waiter: Waiter = {
        admit: resolve,
        timer: setTimeout(() => {
          this.#waiting.splice(this.#waiting.indexOf(waiter), 1);
          reject(new SearchBusy());
        }, this.#options.maxWaitMs),
      };
      this.#waiting.push(waiter);
    });
  }

  /** The slot passes straight to the next in line, so `running` holds. */
  #leave(): void {
    const next = this.#waiting.shift();
    if (!next) {
      this.#running--;
      return;
    }
    clearTimeout(next.timer);
    next.admit();
  }
}
