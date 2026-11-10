import { execFile } from "node:child_process";
import os from "node:os";

/**
 * What the machine is doing, for `/health` and the `busy` policy. Every probe
 * can fail or not apply on a platform, and that reports null rather than an
 * error: a missing reading must not take a worker out of service.
 */

export type RunCommand = (command: string, args: string[]) => Promise<string>;

/** A probe that has not answered in `timeoutMs` is killed and counts as failed. 0 waits for as long as it takes. */
export const runCommand = (command: string, args: string[], timeoutMs = 3_000): Promise<string> =>
  new Promise((resolve, reject) => {
    execFile(command, args, { timeout: timeoutMs, windowsHide: true }, (err, stdout) => (err ? reject(err) : resolve(stdout)));
  });

export interface VramReading {
  freeMb: number;
  totalMb: number;
  /** `nvidia-smi` is dedicated memory; `unified` is Metal, sharing system memory. */
  source: "nvidia-smi" | "unified";
}

/** Output of `nvidia-smi --query-gpu=memory.free,memory.total --format=csv,noheader,nounits`. */
export function parseNvidiaSmi(output: string): VramReading | null {
  let freeMb = 0;
  let totalMb = 0;
  for (const line of output.trim().split(/\r?\n/)) {
    const [free, total] = line.split(",").map((v) => Number(v.trim()));
    if (free === undefined || total === undefined || !Number.isFinite(free) || !Number.isFinite(total)) return null;
    freeMb += free;
    totalMb += total;
  }
  return totalMb > 0 ? { freeMb, totalMb, source: "nvidia-smi" } : null;
}

/** `pmset -g therm` reports `CPU_Speed_Limit = 100` when unthrottled. */
export function parsePmsetThermal(output: string): number | null {
  const match = /CPU_Speed_Limit\s*=\s*(\d+)/.exec(output);
  return match ? Number(match[1]) : null;
}

export interface ProbeSource {
  platform: NodeJS.Platform;
  run: RunCommand;
  /** One-minute load average. Always 0 on Windows, where this signal is absent. */
  loadAverage(): number;
  cores(): number;
  freeMemoryMb(): number;
  totalMemoryMb(): number;
}

export const systemProbes: ProbeSource = {
  platform: process.platform,
  run: runCommand,
  loadAverage: () => os.loadavg()[0] ?? 0,
  cores: () => os.availableParallelism(),
  freeMemoryMb: () => Math.round(os.freemem() / (1024 * 1024)),
  totalMemoryMb: () => Math.round(os.totalmem() / (1024 * 1024)),
};

export interface MachineState {
  /** Null on Windows, which has no load average. */
  loadPerCore: number | null;
  /** 100 means unthrottled. Null where the OS does not report it. */
  cpuSpeedLimit: number | null;
  vram: VramReading | null;
}

export async function readMachine(source: ProbeSource): Promise<MachineState> {
  const loadPerCore = source.platform === "win32" ? null : source.loadAverage() / Math.max(1, source.cores());
  if (source.platform === "darwin") {
    let cpuSpeedLimit: number | null = null;
    try {
      cpuSpeedLimit = parsePmsetThermal(await source.run("pmset", ["-g", "therm"]));
    } catch {
      cpuSpeedLimit = null;
    }
    return { loadPerCore, cpuSpeedLimit, vram: { freeMb: source.freeMemoryMb(), totalMb: source.totalMemoryMb(), source: "unified" } };
  }
  let vram: VramReading | null = null;
  try {
    vram = parseNvidiaSmi(await source.run("nvidia-smi", ["--query-gpu=memory.free,memory.total", "--format=csv,noheader,nounits"]));
  } catch {
    vram = null;
  }
  return { loadPerCore, cpuSpeedLimit: null, vram };
}

export interface BusyThresholds {
  busyLoadPerCore: number;
  busyVramFreeMb: number;
  busyOnThermal: boolean;
}

/** Why the machine should be left alone right now. Empty means available. */
export function busyReasons(machine: MachineState, thresholds: BusyThresholds): string[] {
  const reasons: string[] = [];
  if (thresholds.busyLoadPerCore > 0 && machine.loadPerCore !== null && machine.loadPerCore > thresholds.busyLoadPerCore) reasons.push("load");
  if (thresholds.busyOnThermal && machine.cpuSpeedLimit !== null && machine.cpuSpeedLimit < 100) reasons.push("thermal");
  if (thresholds.busyVramFreeMb > 0 && machine.vram !== null && machine.vram.freeMb < thresholds.busyVramFreeMb) reasons.push("vram");
  return reasons;
}

/** Holds a reading briefly, since a `/health` poll should not fork a process each time. */
export class CachedMachine {
  readonly #source: ProbeSource;
  readonly #ttlMs: number;
  #at = -Infinity;
  #value: Promise<MachineState> | undefined;

  constructor(source: ProbeSource, ttlMs = 5_000) {
    this.#source = source;
    this.#ttlMs = ttlMs;
  }

  read(): Promise<MachineState> {
    const now = Date.now();
    if (this.#value === undefined || now - this.#at >= this.#ttlMs) {
      this.#at = now;
      this.#value = readMachine(this.#source);
    }
    return this.#value;
  }
}
