/**
 * What the checks stand on: a private directory, a real sidecar and API on a
 * throwaway index, and helpers to run the CLI and record results. See README.md.
 */
import { spawn, spawnSync, type ChildProcess } from "node:child_process";
import { randomBytes } from "node:crypto";
import { chmodSync, closeSync, existsSync, mkdirSync, mkdtempSync, openSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { DatabaseSync, type SQLInputValue } from "node:sqlite";
import http from "node:http";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import type { Plan, ProxyRecord } from "./proxy.ts";
import { freePort } from "../../worker/test/ports.ts";

export const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");

/** A throwaway test profile. The driver never reads or writes the default profile. */
export const PROFILE = "memoria-e2e-test";

/** Limits are fixed and the windows long, so a 429 arrives on an exact request, however slow the machine. */
export const KEY_RATE_LIMIT = 100;
export const FAILED_AUTH_LIMIT = 20;
const LIMIT_WINDOW = "00:30:00";

/** The one peer whose X-Forwarded-For the API believes. Other 127.0.0.x peers are not trusted. */
export const TRUSTED_PEER = "127.0.0.9";

/** All document bodies in one batch response, set low so a few fixture documents reach it. */
export const BATCH_BUDGET = 100_000;
export const BULK_DOCUMENTS = 5;
export const BULK_DOCUMENT_BYTES = 30_000;
/** One past what a batch may select from globs. */
export const MANY_DOCUMENTS = 1001;
export const TOTAL_DOCUMENTS = 2 + BULK_DOCUMENTS + MANY_DOCUMENTS;

const KEY_PATTERN = /mem_[0-9a-f]+_[0-9a-f]+/g;

const REFUSAL_TIMEOUT_MS = 30_000;

export interface RequestOptions {
  path: string;
  method?: string;
  key?: string;
  /** The local address to connect from, which is the peer address the API sees. */
  from?: string;
  headers?: Record<string, string>;
  body?: unknown;
  /** This answer carries a new key by design, so the scan of responses leaves it out. */
  issuing?: boolean;
  /** Another host, instead of the main API. */
  baseUrl?: string;
}

export interface ApiResponse {
  status: number;
  body: string;
  headers: http.IncomingHttpHeaders;
}

export interface ExtraHost {
  url: string;
  log(): string;
}

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

/** What a server started by a check file needs beyond the defaults. */
export interface HarnessOptions {
  /** Empty collections to index, beside the two fixtures. */
  /** Empty, as a collection opened to ingest must be. */
  extraCollections?: readonly string[];
  /** Enabled for ingest with files already in them, to show that is refused. */
  filledCollections?: readonly string[];
  apiEnv?: NodeJS.ProcessEnv;
  sidecarEnv?: NodeJS.ProcessEnv;
  /** Leave out the default fixtures, so a run with real models embeds only what it seeds itself. */
  bareIndex?: boolean;
}

/** Makes a key on this run's server. A key has a budget of requests, so a check that makes many takes its own. */
export type ApiKeyMaker = (label: string, scopes: string[]) => Promise<string>;

/** Collection name to root and pattern, as the SDK takes them. */
export type Collections = Record<string, { path: string; pattern: string }>;

export class Harness {
  readonly dir = mkdtempSync(path.join(os.tmpdir(), "memoria-e2e-"));
  readonly secrets: string[] = [];
  readonly results: Array<{ name: string; ok: boolean }> = [];
  /** Every answer that does not carry a new key by design, for the final scan. */
  readonly responses: ApiResponse[] = [];
  apiUrl = "";
  sidecarPort = 0;
  private proxyPort = 0;
  private controlReader: DatabaseSync | undefined;
  private readonly children: ChildProcess[] = [];
  private readonly fds: number[] = [];
  private readonly cli = path.join(this.dir, process.platform === "win32" ? "memoria.exe" : "memoria");
  protected readonly sidecarSecret = randomBytes(32).toString("base64url");
  /** The API's ingest digest key, and a worker token for layouts that set one: never printed either. */
  protected readonly digestKey = randomBytes(32).toString("base64url");
  protected readonly workerToken = randomBytes(24).toString("base64url");
  /** False where the servers run as deployed, with nothing between them to record what the sidecar was asked. */
  readonly hasProxy: boolean = true;

  /** Everything a child sees for home and config points inside the private directory. */
  private readonly isolated: NodeJS.ProcessEnv;
  protected readonly apiEnv: NodeJS.ProcessEnv;

  protected readonly options: HarnessOptions;
  private api: ChildProcess | undefined;

  constructor(options: HarnessOptions = {}) {
    this.options = options;
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
    this.apiEnv = options.apiEnv ?? {};
  }

  check(name: string, ok: unknown, detail?: unknown): void {
    const passed = Boolean(ok);
    this.results.push({ name, ok: passed });
    console.log(`${passed ? "PASS" : "FAIL"} ${name}`);
    if (!passed && detail !== undefined) {
      console.log(`     ${this.scrub(typeof detail === "string" ? detail : JSON.stringify(detail)).slice(0, 800)}`);
    }
  }

  /**
   * The end of each service's log, for a failed run. They live in the run's directory, or with the compose layout in containers, and cleanUp
   * removes both. CI logs are public, so every secret the run knows of comes out first.
   */
  dumpLogs(maxLines = 200): void {
    const secrets = this.sensitiveValues().filter((value) => value !== "");
    for (const [name, text] of this.serviceLogs()) {
      const lines = this.scrub(secrets.reduce((so, far) => so.split(far).join("[SECRET]"), text)).split("\n");
      console.log(`::group::${name} (last ${Math.min(maxLines, lines.length)} of ${lines.length} lines)`);
      // Indented, so a line of the log cannot begin with "::" and be read as a workflow command.
      console.log(lines.slice(-maxLines).map((line) => `  ${line}`).join("\n"));
      console.log("::endgroup::");
    }
  }

  /** The API's and the sidecar's logs, wherever this layout keeps them, and any other log in the run's directory, such as the proxy's. */
  private serviceLogs(): Array<[string, string]> {
    const logs: Array<[string, string]> = [];
    for (const service of ["api", "sidecar"] as const) {
      try {
        logs.push([service, this.log(service)]);
      } catch {
        // Not started, so nothing to show.
      }
    }
    const others = existsSync(this.dir) ? readdirSync(this.dir).filter((file) => file.endsWith(".log") && file !== "api.log" && file !== "sidecar.log").sort() : [];
    return [...logs, ...others.map((file): [string, string] => [file, readFileSync(path.join(this.dir, file), "utf8")])];
  }

  /** The whole key and its secret part alone, plus the shared secrets: none may be stored or logged. */
  sensitiveValues(): string[] {
    return [...this.secrets, ...this.secrets.map((key) => key.split("_")[2] ?? key), this.sidecarSecret, this.digestKey, this.workerToken];
  }

  /** Remembers a key, so nothing printed from here on can repeat it. */
  remember(key: string): string {
    this.secrets.push(key);
    return key;
  }

  scrub(text: string): string {
    return this.secrets.reduce((all, key) => all.split(key).join("[KEY]"), text).replace(KEY_PATTERN, "[KEY]");
  }

  /** Runs the CLI without waiting, for a check that watches it while it works. */
  startMemoria(args: string[], env: NodeJS.ProcessEnv): ChildProcess {
    return spawn(this.cli, args, { stdio: "ignore", env: { ...this.isolated, ...env } });
  }

  /** Runs the CLI while this process keeps serving, for a check whose server lives here too. */
  memoriaAsync(args: string[], key: string): Promise<CliResult> {
    return new Promise((resolve) => {
      const child = spawn(this.cli, args, { stdio: ["ignore", "pipe", "pipe"], env: { ...this.isolated, MEMORIA_API_KEY: key } });
      let out = "";
      let err = "";
      child.stdout.setEncoding("utf8").on("data", (text: string) => (out += text));
      child.stderr.setEncoding("utf8").on("data", (text: string) => (err += text));
      child.once("close", (code) => resolve({ code: code ?? -1, out, err }));
    });
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
    run("dotnet", ["build", "tools/RedactionCheck", "-c", "Release", "-v", "quiet", "-nologo"], repo);
    run("npm", ["run", "build", "--silent"], path.join(repo, "sidecar"));
  }

  /** The real SDK indexes two collections, so scope has something to separate. */
  seedIndex(): void {
    const fixtures = this.options.bareIndex ? [] : [
      ["notes", "hello.md", "# Hello\n\nThe zebrafish protocol lives here.\n\x1b]52;c;ZXZpbA==\x07 stored escape\n"],
      ["secret", "hidden.md", "# Hidden\n\nThe zebrafish hidden ledger.\n"],
      ...bulkFixtures(),
      ...manyFixtures(),
    ] as const;
    for (const [collection, file, text] of fixtures) {
      const folder = path.join(this.dir, "corpus", collection);
      mkdirSync(folder, { recursive: true });
      writeFileSync(path.join(folder, file), text);
    }
    const names = this.options.bareIndex ? [] : ["notes", "secret", "bulk", "many"];
    const collections: Collections = Object.fromEntries(names.map((name) => [name, { path: path.join(this.dir, "corpus", name), pattern: "**/*.md" }]));
    for (const name of this.options.extraCollections ?? []) {
      const folder = path.join(this.dir, "corpus", name);
      mkdirSync(folder, { recursive: true });
      collections[name] = { path: folder, pattern: "**/*.md" };
    }
    for (const name of this.options.filledCollections ?? []) {
      const folder = path.join(this.dir, "corpus", name);
      mkdirSync(folder, { recursive: true });
      writeFileSync(path.join(folder, "seed.md"), `# Seed\n\nSeed text for ${name}.\n`);
      collections[name] = { path: folder, pattern: "**/*.md" };
    }
    const seeded = this.createIndex(collections);
    this.check("seed a throwaway index with the real SDK", seeded.status === 0, seeded.stderr);
  }

  /** The code that makes the index, for a process that has the SDK. */
  protected indexCode(dbPath: string, collections: Collections): string {
    return `const qmd = await import("@tobilu/qmd");
const store = await qmd.createStore({ dbPath: ${JSON.stringify(dbPath)}, config: { collections: ${JSON.stringify(collections)} } });
await store.update();
await store.close();`;
  }

  protected createIndex(collections: Collections): { status: number | null; stderr: string } {
    return spawnSync(process.execPath, ["--input-type=module", "-e", this.indexCode(path.join(this.dir, "index.sqlite"), collections)], { cwd: path.join(repo, "sidecar"), env: this.offline(), encoding: "utf8", timeout: 120_000 });
  }

  /** Model files in the private cache, which has to stay empty of them. */
  modelFiles(): string[] {
    const cache = path.join(this.isolated["XDG_CACHE_HOME"] ?? "", "qmd", "models");
    return existsSync(cache) ? readdirSync(cache, { recursive: true, encoding: "utf8" }) : [];
  }

  /** For a child that must touch nothing outside the private directory and reach no network. */
  offlineEnvironment(): NodeJS.ProcessEnv {
    return this.offline();
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
      E2E_STALL_FILE: path.join(this.dir, "stall.json"),
      MEMORIA_QMD_DB_PATH: path.join(this.dir, "index.sqlite"),
      MEMORIA_QMD_WARMUP: "0",
      MEMORIA_QMD_KEEP_WARM: "0",
      MEMORIA_QMD_LOG_LEVEL: "info",
      // Local mode can load models for a plain query. Here the only worker is an
      // address nothing listens on and there is no fallback, so nothing can.
      MEMORIA_QMD_LLM: "remote",
      MEMORIA_QMD_LLM_FALLBACK: "0",
      MEMORIA_QMD_WORKER_URL: "http://127.0.0.1:9",
      ...this.options.sidecarEnv,
    };
    this.launch(process.execPath, ["dist/main.js"], path.join(repo, "sidecar"), env, "sidecar.log");
    this.check("sidecar answers", await waitForHttp(`http://127.0.0.1:${this.sidecarPort}/health`), this.log("sidecar"));
  }

  /** Runs a command against the same control database as the API, with no host started. */
  bootstrap(label: string): { code: number; out: string; err: string } {
    const result = spawnSync("dotnet", [apiDll(), "--bootstrap-admin-key", label], { cwd: apiDirectory(), env: this.apiEnvironment(), encoding: "utf8", timeout: 120_000 });
    return { code: result.status ?? -1, out: result.stdout ?? "", err: result.stderr ?? "" };
  }

  async startApi(): Promise<void> {
    this.api = this.launch("dotnet", [apiDll()], apiDirectory(), this.apiEnvironment(), "api.log");
    this.check("API answers", await waitForHttp(`${this.apiUrl}/health`), this.log("api"));
  }

  /** Kills the API outright, with no chance to tidy up, and starts it again on the same database. */
  async restartApi(): Promise<void> {
    const api = this.api;
    if (api) {
      const exited = new Promise((resolve) => api.once("exit", resolve));
      api.kill("SIGKILL");
      await exited;
    }

    await this.startApi();
  }

  async choosePorts(): Promise<void> {
    this.apiUrl = `http://127.0.0.1:${await freePort()}`;
    this.proxyPort = await freePort();
  }

  /** Between the API and the sidecar from here on. See proxy.ts. */
  async startProxy(): Promise<void> {
    this.launch(process.execPath, [path.join(repo, "scripts", "e2e", "proxy.ts"), String(this.proxyPort), String(this.sidecarPort), this.proxyRecordFile()], repo, this.isolated, "proxy.log");
    this.check("recording proxy answers", await waitForHttp(`http://127.0.0.1:${this.proxyPort}/health`));
  }

  private proxyRecordFile(): string {
    return path.join(this.dir, "proxy-records.jsonl");
  }

  /** How many calls the proxy has recorded, to pass to `proxied` later. */
  proxiedCount(): number {
    return this.proxied().length;
  }

  /** What the sidecar was asked, in order, from a count `proxiedCount` returned. */
  proxied(from = 0): ProxyRecord[] {
    const lines = readFileSync(this.proxyRecordFile(), "utf8").split("\n").filter((line) => line !== "");
    return lines.slice(from).map((line) => JSON.parse(line) as ProxyRecord);
  }

  /** What stall-writes.mjs holds writes for from now on. Writes already held stay held. */
  stallWrites(where: { before?: number; after?: number }): void {
    writeFileSync(path.join(this.dir, "stall.json"), JSON.stringify(where));
  }

  async planProxy(plan: Plan): Promise<void> {
    await fetch(`http://127.0.0.1:${this.proxyPort}/__e2e/plan`, { method: "POST", body: JSON.stringify(plan) });
  }

  /**
   * Runs a host that should refuse to start. A host that starts instead is killed, which is not a refusal.
   * One that stops itself on Linux has no exit code, since an unhandled .NET exception ends in SIGABRT.
   */
  refusedHost(overrides: NodeJS.ProcessEnv): Promise<{ refused: boolean; output: string }> {
    return new Promise((resolve) => {
      const child = spawn("dotnet", [apiDll()], { cwd: apiDirectory(), env: this.extraHostEnvironment(overrides, `http://127.0.0.1:0`) });
      let output = "";
      child.stdout.on("data", (chunk: Buffer) => (output += chunk.toString("utf8")));
      child.stderr.on("data", (chunk: Buffer) => (output += chunk.toString("utf8")));
      let started = false;
      const timer = setTimeout(() => {
        started = true;
        child.kill("SIGKILL");
      }, REFUSAL_TIMEOUT_MS);
      child.on("close", (code) => {
        clearTimeout(timer);
        resolve({ refused: !started && code !== 0, output });
      });
    });
  }

  /** A second host with its own settings, for as long as the run lasts. */
  async startExtraHost(name: string, overrides: NodeJS.ProcessEnv, cwd = apiDirectory()): Promise<ExtraHost> {
    const url = `http://127.0.0.1:${await freePort()}`;
    const logName = `${name}.log`;
    this.launch("dotnet", [apiDll()], cwd, this.extraHostEnvironment(overrides, url), logName);
    this.check(`${name} host answers`, await waitForHttp(`${url}/health`));
    return { url, log: () => readFileSync(path.join(this.dir, logName), "utf8") };
  }

  /** The main API's settings with these on top. An undefined value removes a setting. */
  private extraHostEnvironment(overrides: NodeJS.ProcessEnv, url: string): NodeJS.ProcessEnv {
    return { ...this.apiEnvironment(), ASPNETCORE_URLS: url, ...overrides };
  }

  /** A path for a control database the main API does not use. */
  privatePath(name: string): string {
    return path.join(this.dir, name);
  }

  /** Runs a read-only query on the live control database. */
  control<T>(sql: string, ...parameters: SQLInputValue[]): T[] {
    this.controlReader ??= new DatabaseSync(this.controlPath(), { readOnly: true });
    return this.controlReader.prepare(sql).all(...parameters) as T[];
  }

  protected controlPath(): string {
    return path.join(this.dir, "control.sqlite");
  }

  /** Moves the write-ahead log into the main file, which otherwise may not yet hold what a scan of it looks for. */
  checkpointControl(): void {
    const writer = new DatabaseSync(this.controlPath());
    writer.exec("PRAGMA wal_checkpoint(FULL)");
    writer.close();
  }

  /** The raw bytes of the control database and of its write-ahead log, as text that preserves every byte. */
  controlFiles(): { main: string; wal: string } {
    const read = (file: string): string => (existsSync(file) ? readFileSync(file).toString("latin1") : "");
    return { main: read(this.controlPath()), wal: read(`${this.controlPath()}-wal`) };
  }

  protected apiEnvironment(): NodeJS.ProcessEnv {
    return {
      ...this.isolated,
      ...this.apiEnv,
      ASPNETCORE_URLS: this.apiUrl,
      ASPNETCORE_ENVIRONMENT: "Production",
      DOTNET_NOLOGO: "1",
      Memoria__Sidecar__BaseUrl: `http://127.0.0.1:${this.proxyPort}`,
      Memoria__Sidecar__Secret: this.sidecarSecret,
      Memoria__Ingest__DigestKey: this.digestKey,
      Memoria__Sidecar__ReadyRequiresModels: "false",
      Memoria__Search__MaxMultiGetBytes: String(BATCH_BUDGET),
      Memoria__Auth__Mode: "ApiKey",
      Memoria__Auth__DatabasePath: this.controlPath(),
      Memoria__Auth__TrustedProxies__0: TRUSTED_PEER,
      Memoria__Auth__RateLimit__PermitLimit: String(KEY_RATE_LIMIT),
      Memoria__Auth__RateLimit__Window: LIMIT_WINDOW,
      Memoria__Auth__FailedAuth__PermitLimit: String(FAILED_AUTH_LIMIT),
      Memoria__Auth__FailedAuth__Window: LIMIT_WINDOW,
    };
  }

  log(service: "api" | "sidecar"): string {
    return readFileSync(path.join(this.dir, `${service}.log`), "utf8");
  }

  private launch(command: string, args: string[], cwd: string, env: NodeJS.ProcessEnv, logName: string): ChildProcess {
    const fd = openSync(path.join(this.dir, logName), "a");
    this.fds.push(fd);
    const child = spawn(command, args, { cwd, env, stdio: ["ignore", fd, fd] });
    this.children.push(child);
    return child;
  }

  /** Talks to the sidecar directly, as only a check should, for what the API does not offer such as forcing the index up to date. */
  async sidecar(method: string, route: string, body?: unknown): Promise<{ status: number; text: string }> {
    const response = await fetch(`http://127.0.0.1:${this.sidecarPort}${route}`, {
      method,
      headers: { Authorization: `Bearer ${this.sidecarSecret}`, "Content-Type": "application/json" },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    });
    return { status: response.status, text: await response.text() };
  }

  rawGet(rawPath: string, key: string): Promise<ApiResponse> {
    return this.request({ path: rawPath, key });
  }

  /** Sends exactly these path bytes, which fetch and URL would normalize, and keeps the answer for the final scan. */
  request(options: RequestOptions): Promise<ApiResponse> {
    const { hostname, port } = new URL(options.baseUrl ?? this.apiUrl);
    const payload = options.body === undefined ? undefined : JSON.stringify(options.body);
    const headers = { ...options.headers, ...(options.key ? { Authorization: `Bearer ${options.key}` } : {}), ...(payload ? { "Content-Type": "application/json" } : {}) };
    return new Promise((resolve, reject) => {
      const request = http.request({ hostname, port, path: options.path, method: options.method ?? "GET", headers, localAddress: options.from }, (response) => {
        let body = "";
        response.setEncoding("utf8");
        response.on("data", (chunk: string) => (body += chunk));
        response.on("end", () => {
          const answer = { status: response.statusCode ?? 0, body, headers: response.headers };
          if (!options.issuing) this.responses.push(answer);
          resolve(answer);
        });
      });
      request.on("error", reject);
      request.end(payload);
    });
  }

  /** Stops everything and removes the private directory, with every key and index in it. */
  async cleanUp(): Promise<void> {
    this.controlReader?.close();
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

/** Exactly `BULK_DOCUMENT_BYTES` each, and different, so each is its own document. */
function bulkFixtures(): Array<readonly [string, string, string]> {
  return Array.from({ length: BULK_DOCUMENTS }, (_, index) => {
    const head = `# Bulk ${index}\n\n`;
    return ["bulk", `doc-${index}.md`, head + "x".repeat(BULK_DOCUMENT_BYTES - head.length)] as const;
  });
}

function manyFixtures(): Array<readonly [string, string, string]> {
  return Array.from({ length: MANY_DOCUMENTS }, (_, index) => ["many", `doc-${index}.md`, `# Many ${index}\n`] as const);
}

/** A host's content root is its working directory, so this is what makes it read the shipped appsettings.json. */
function apiDirectory(): string {
  return path.join(repo, "src", "Memoria.Api", "bin", "Release", "net10.0");
}

/** The local redaction report's tool. It is not part of the solution, so it builds on its own. */
export function redactionCheckDll(): string {
  return path.join(repo, "tools", "RedactionCheck", "bin", "Release", "net10.0", "RedactionCheck.dll");
}

function apiDll(): string {
  return path.join(apiDirectory(), "Memoria.Api.dll");
}

function run(command: string, args: string[], cwd: string): void {
  // npm is a .cmd on Windows, which needs a shell; these arguments are all constants.
  const result = spawnSync(command, args, { cwd, stdio: ["ignore", "inherit", "inherit"], shell: command === "npm" && process.platform === "win32" });
  if (result.status !== 0) throw new Error(`${command} ${args.join(" ")} failed`);
}

// Ports come from the worker tests' helper, which never hands out one that fetch refuses to dial.
export { freePort };

export function pause(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

export async function waitForHttp(url: string): Promise<boolean> {
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
