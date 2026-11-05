/**
 * What the checks stand on: a private directory, a real sidecar and API on a
 * throwaway index, and helpers to run the CLI and record results. See README.md.
 */
import { spawn, spawnSync, type ChildProcess } from "node:child_process";
import { randomBytes } from "node:crypto";
import { chmodSync, closeSync, existsSync, mkdirSync, mkdtempSync, openSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import http from "node:http";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");

/** A throwaway test profile. The driver never reads or writes the default profile. */
export const PROFILE = "memoria-e2e-test";

/** Limits are fixed and the windows long, so a 429 arrives on an exact request, however slow the machine. */
export const KEY_RATE_LIMIT = 100;
export const FAILED_AUTH_LIMIT = 20;
const LIMIT_WINDOW = "00:30:00";

const KEY_PATTERN = /mem_[0-9a-f]+_[0-9a-f]+/g;

export interface CliResult {
  code: number;
  out: string;
  err: string;
}

export interface CliOptions {
  key?: string;
  stdin?: string;
  /** Use the saved profile's server rather than MEMORIA_URL. */
  noUrl?: boolean;
}

export class Harness {
  readonly dir = mkdtempSync(path.join(os.tmpdir(), "memoria-e2e-"));
  readonly secrets: string[] = [];
  readonly results: Array<{ name: string; ok: boolean }> = [];
  apiUrl = "";
  sidecarPort = 0;
  private readonly children: ChildProcess[] = [];
  private readonly fds: number[] = [];
  private readonly cli = path.join(this.dir, process.platform === "win32" ? "memoria.exe" : "memoria");
  private readonly sidecarSecret = randomBytes(32).toString("base64url");

  /** Everything a child sees for home and config points inside the private directory. */
  private readonly isolated: NodeJS.ProcessEnv;
  private readonly apiEnv: NodeJS.ProcessEnv;

  constructor() {
    chmodSync(this.dir, 0o700);
    const home = path.join(this.dir, "home");
    mkdirSync(home);
    this.isolated = {
      ...process.env,
      HOME: home,
      USERPROFILE: home,
      XDG_CACHE_HOME: path.join(home, ".cache"),
      XDG_CONFIG_HOME: path.join(this.dir, "cfg"),
      LOCALAPPDATA: path.join(home, "local"),
      APPDATA: path.join(home, "roaming"),
    };
    delete this.isolated["MEMORIA_API_KEY"];
    delete this.isolated["MEMORIA_URL"];
    this.apiEnv = {};
  }

  check(name: string, ok: unknown, detail?: unknown): void {
    const passed = Boolean(ok);
    this.results.push({ name, ok: passed });
    console.log(`${passed ? "PASS" : "FAIL"} ${name}`);
    if (!passed && detail !== undefined) {
      console.log(`     ${this.scrub(typeof detail === "string" ? detail : JSON.stringify(detail)).slice(0, 800)}`);
    }
  }

  /** Remembers a key, so nothing printed from here on can repeat it. */
  remember(key: string): string {
    this.secrets.push(key);
    return key;
  }

  scrub(text: string): string {
    return this.secrets.reduce((all, key) => all.split(key).join("[KEY]"), text).replace(KEY_PATTERN, "[KEY]");
  }

  memoria(args: string[], options: CliOptions = {}): CliResult {
    const env: NodeJS.ProcessEnv = { ...this.isolated };
    if (!options.noUrl) env["MEMORIA_URL"] = this.apiUrl;
    if (options.key) env["MEMORIA_API_KEY"] = options.key;
    const result = spawnSync(this.cli, args, { env, input: options.stdin ?? "", encoding: "utf8", timeout: 120_000, maxBuffer: 64 * 1024 * 1024 });
    return { code: result.status ?? -1, out: result.stdout ?? "", err: result.stderr ?? "" };
  }

  /** The CLI is always built, since it lives in the private directory. */
  buildCli(): void {
    run("go", ["build", "-o", this.cli, "."], path.join(repo, "cli"));
  }

  buildServers(): void {
    run("dotnet", ["build", "src/Memoria.Api", "-c", "Release", "-v", "quiet", "-nologo"], repo);
    run("npm", ["run", "build", "--silent"], path.join(repo, "sidecar"));
  }

  /** The real SDK indexes two collections, so scope has something to separate. */
  seedIndex(): void {
    for (const [collection, file, text] of [
      ["notes", "hello.md", "# Hello\n\nThe zebrafish protocol lives here.\n\x1b]52;c;ZXZpbA==\x07 stored escape\n"],
      ["secret", "hidden.md", "# Hidden\n\nThe zebrafish hidden ledger.\n"],
    ] as const) {
      const folder = path.join(this.dir, "corpus", collection);
      mkdirSync(folder, { recursive: true });
      writeFileSync(path.join(folder, file), text);
    }
    const collections = {
      notes: { path: path.join(this.dir, "corpus", "notes"), pattern: "**/*.md" },
      secret: { path: path.join(this.dir, "corpus", "secret"), pattern: "**/*.md" },
    };
    const code = `const qmd = await import("@tobilu/qmd");
const store = await qmd.createStore({ dbPath: ${JSON.stringify(path.join(this.dir, "index.sqlite"))}, config: { collections: ${JSON.stringify(collections)} } });
await store.update();
await store.close();`;
    const seeded = spawnSync(process.execPath, ["--input-type=module", "-e", code], { cwd: path.join(repo, "sidecar"), env: this.offline(), encoding: "utf8", timeout: 120_000 });
    this.check("seed a throwaway index with the real SDK", seeded.status === 0, seeded.stderr);
  }

  /** Model files in the private cache, which has to stay empty of them. */
  modelFiles(): string[] {
    const cache = path.join(this.isolated["XDG_CACHE_HOME"] ?? "", "qmd", "models");
    return existsSync(cache) ? readdirSync(cache, { recursive: true, encoding: "utf8" }) : [];
  }

  /** Every proxy goes nowhere, as a second line behind the sidecar's remote mode. */
  private offline(): NodeJS.ProcessEnv {
    const nowhere = "http://127.0.0.1:9";
    return { ...this.isolated, HTTP_PROXY: nowhere, HTTPS_PROXY: nowhere, http_proxy: nowhere, https_proxy: nowhere, NO_PROXY: "127.0.0.1", no_proxy: "127.0.0.1", NODE_LLAMA_CPP_SKIP_DOWNLOAD: "true" };
  }

  async startSidecar(): Promise<void> {
    this.sidecarPort = await freePort();
    const env: NodeJS.ProcessEnv = {
      ...this.offline(),
      MEMORIA_QMD_HOST: "127.0.0.1",
      MEMORIA_QMD_PORT: String(this.sidecarPort),
      MEMORIA_QMD_SECRET: this.sidecarSecret,
      MEMORIA_QMD_DB_PATH: path.join(this.dir, "index.sqlite"),
      MEMORIA_QMD_WARMUP: "0",
      MEMORIA_QMD_KEEP_WARM: "0",
      MEMORIA_QMD_LOG_LEVEL: "info",
      // Local mode can load models for a plain query. Here the only worker is an
      // address nothing listens on and there is no fallback, so nothing can.
      MEMORIA_QMD_LLM: "remote",
      MEMORIA_QMD_LLM_FALLBACK: "0",
      MEMORIA_QMD_WORKER_URL: "http://127.0.0.1:9",
    };
    this.launch(process.execPath, ["dist/main.js"], path.join(repo, "sidecar"), env, "sidecar.log");
    this.check("sidecar answers", await waitForHttp(`http://127.0.0.1:${this.sidecarPort}/health`), this.log("sidecar"));
  }

  /** Runs a command against the same control database as the API, with no host started. */
  bootstrap(label: string): { code: number; out: string; err: string } {
    const result = spawnSync("dotnet", [apiDll(), "--bootstrap-admin-key", label], { env: this.apiEnvironment(), encoding: "utf8", timeout: 120_000 });
    return { code: result.status ?? -1, out: result.stdout ?? "", err: result.stderr ?? "" };
  }

  async startApi(): Promise<void> {
    this.launch("dotnet", [apiDll()], repo, this.apiEnvironment(), "api.log");
    this.check("API answers", await waitForHttp(`${this.apiUrl}/health`), this.log("api"));
  }

  async choosePorts(): Promise<void> {
    this.apiUrl = `http://127.0.0.1:${await freePort()}`;
  }

  private apiEnvironment(): NodeJS.ProcessEnv {
    return {
      ...this.isolated,
      ...this.apiEnv,
      ASPNETCORE_URLS: this.apiUrl,
      ASPNETCORE_ENVIRONMENT: "Production",
      DOTNET_NOLOGO: "1",
      Memoria__Sidecar__BaseUrl: `http://127.0.0.1:${this.sidecarPort}`,
      Memoria__Sidecar__Secret: this.sidecarSecret,
      Memoria__Sidecar__ReadyRequiresModels: "false",
      Memoria__Auth__DatabasePath: path.join(this.dir, "control.sqlite"),
      Memoria__Auth__RateLimit__PermitLimit: String(KEY_RATE_LIMIT),
      Memoria__Auth__RateLimit__Window: LIMIT_WINDOW,
      Memoria__Auth__FailedAuth__PermitLimit: String(FAILED_AUTH_LIMIT),
      Memoria__Auth__FailedAuth__Window: LIMIT_WINDOW,
    };
  }

  log(service: "api" | "sidecar"): string {
    return readFileSync(path.join(this.dir, `${service}.log`), "utf8");
  }

  private launch(command: string, args: string[], cwd: string, env: NodeJS.ProcessEnv, logName: string): void {
    const fd = openSync(path.join(this.dir, logName), "a");
    this.fds.push(fd);
    this.children.push(spawn(command, args, { cwd, env, stdio: ["ignore", fd, fd] }));
  }

  /** Sends exactly these path bytes, which fetch and URL would normalize. */
  rawGet(rawPath: string, key: string): Promise<{ status: number; body: string; headers: http.IncomingHttpHeaders }> {
    const { hostname, port } = new URL(this.apiUrl);
    return new Promise((resolve, reject) => {
      const request = http.request({ hostname, port, path: rawPath, method: "GET", headers: { Authorization: `Bearer ${key}` } }, (response) => {
        let body = "";
        response.setEncoding("utf8");
        response.on("data", (chunk: string) => (body += chunk));
        response.on("end", () => resolve({ status: response.statusCode ?? 0, body, headers: response.headers }));
      });
      request.on("error", reject);
      request.end();
    });
  }

  /** Stops everything and removes the private directory, with every key and index in it. */
  async cleanUp(): Promise<void> {
    await Promise.all(this.children.map(stop));
    for (const fd of this.fds) closeSync(fd);
    rmSync(this.dir, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 });
  }
}

function stop(child: ChildProcess): Promise<void> {
  return new Promise((resolve) => {
    if (child.exitCode !== null || child.signalCode !== null) return resolve();
    child.once("exit", () => resolve());
    child.kill();
    setTimeout(() => child.kill("SIGKILL"), 5000).unref();
  });
}

function apiDll(): string {
  return path.join(repo, "src", "Memoria.Api", "bin", "Release", "net10.0", "Memoria.Api.dll");
}

function run(command: string, args: string[], cwd: string): void {
  // npm is a .cmd on Windows, which needs a shell; these arguments are all constants.
  const result = spawnSync(command, args, { cwd, stdio: ["ignore", "inherit", "inherit"], shell: command === "npm" && process.platform === "win32" });
  if (result.status !== 0) throw new Error(`${command} ${args.join(" ")} failed`);
}

function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.on("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      server.close(() => (address && typeof address === "object" ? resolve(address.port) : reject(new Error("no port"))));
    });
  });
}

export function pause(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function waitForHttp(url: string): Promise<boolean> {
  for (let attempt = 0; attempt < 240; attempt++) {
    try {
      await fetch(url);
      return true;
    } catch {
      await pause(250);
    }
  }
  return false;
}
