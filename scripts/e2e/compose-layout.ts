/**
 * The suite's ingest server, run as deployed: `deploy/compose.yaml` as it ships, on images built from this checkout, with nothing between the
 * API and the sidecar. A test-only override adds what checks read from outside: the corpus, the index, the control database and the stall file
 * live in the run's private directory, the API's port is the run's own, and both containers run as this user so that directory stays private.
 * The checks that need the recording proxy say NOT RUN here; the process layout runs them.
 */
import { spawnSync } from "node:child_process";
import { writeFileSync } from "node:fs";
import path from "node:path";
import { Harness, repo, waitForHttp, type Collections } from "./harness.ts";

const PROJECT = "sc-e2e";
const SIDECAR = "sc-e2e-qmd";
const API = "sc-e2e-api";
const VERSION = "0.0.0-e2e-compose";
/** Inside both containers: the run's private directory, so checks read what the services wrote. */
const STATE = "/e2e-state";

export class ComposeHarness extends Harness {
  override readonly hasProxy = false;
  #apiPort = 0;
  #built = false;

  /** Both images from this checkout, once per run. */
  buildImages(): void {
    if (this.#built) return;
    this.#built = true;
    docker(["build", "--quiet", "-t", `ghcr.io/mggarofalo/memoria:${VERSION}`, "--build-arg", `MEMORIA_VERSION=${VERSION}`, "."]);
    docker(["build", "--quiet", "-t", `ghcr.io/mggarofalo/memoria-qmd:${VERSION}`, "--build-arg", `MEMORIA_VERSION=${VERSION}`, "sidecar"]);
  }

  /** The index is made inside the sidecar's image, so its roots are the paths the container sees. */
  protected override createIndex(collections: Collections): { status: number | null; stderr: string } {
    this.buildImages();
    this.#writeCompose();
    const corpus = path.join(this.dir, "corpus");
    const inside: Collections = Object.fromEntries(Object.entries(collections).map(([name, c]) => [name, { ...c, path: `/corpus/${path.relative(corpus, c.path).split(path.sep).join("/")}` }]));
    const result = this.#compose(["run", "--rm", "--no-deps", "--entrypoint", "node", "memoria-qmd", "--input-type=module", "-e", this.indexCode(`${STATE}/index.sqlite`, inside)], true);
    return { status: result.status, stderr: result.stderr };
  }

  override async choosePorts(): Promise<void> {
    await super.choosePorts();
    this.#apiPort = Number(new URL(this.apiUrl).port);
    this.#writeCompose();
  }

  override async startSidecar(): Promise<void> {
    this.#compose(["up", "-d", "memoria-qmd"]);
    const healthy = await until(() => this.#inSidecar("GET", "/health").status === 200, 90);
    this.check("sidecar answers", healthy, this.log("sidecar"));
  }

  /** There is none: that is the point of this layout. */
  override async startProxy(): Promise<void> {}

  override bootstrap(label: string): { code: number; out: string; err: string } {
    const result = this.#compose(["run", "--rm", "--no-deps", "memoria-api", "--bootstrap-admin-key", label], true);
    return { code: result.status ?? -1, out: result.stdout, err: result.stderr };
  }

  override async startApi(): Promise<void> {
    this.#compose(["up", "-d", "memoria-api"]);
    this.check("API answers", await waitForHttp(`${this.apiUrl}/health`), this.log("api"));
  }

  override async restartApi(): Promise<void> {
    docker(["kill", API]);
    this.#compose(["up", "-d", "memoria-api"]);
    if (!(await waitForHttp(`${this.apiUrl}/health`))) throw new Error("the API did not come back after a kill");
  }

  override log(service: "api" | "sidecar"): string {
    const result = spawnSync("docker", ["logs", service === "api" ? API : SIDECAR], { encoding: "utf8", maxBuffer: 256 * 1024 * 1024 });
    return `${result.stdout}${result.stderr}`;
  }

  override async sidecar(method: string, route: string, body?: unknown): Promise<{ status: number; text: string }> {
    return this.#inSidecar(method, route, body);
  }

  override modelFiles(): string[] {
    const found = spawnSync("docker", ["exec", SIDECAR, "sh", "-c", "find / -xdev -name '*.gguf' 2>/dev/null; ls /index/qmd/models 2>/dev/null"], { encoding: "utf8" });
    return found.stdout.split("\n").filter((line) => line !== "");
  }

  override async cleanUp(): Promise<void> {
    this.#compose(["down", "-v"], true);
    docker(["image", "rm", `ghcr.io/mggarofalo/memoria:${VERSION}`, `ghcr.io/mggarofalo/memoria-qmd:${VERSION}`], true);
    await super.cleanUp();
  }

  /**
   * From inside the sidecar's container, as nothing outside its network can reach it. Each call is a `docker exec`, so a failure of that,
   * rather than an answer from the sidecar, is tried again, and named if it persists.
   */
  #inSidecar(method: string, route: string, body?: unknown): { status: number; text: string } {
    const script = "const [method, route, body] = process.argv.slice(1);"
      + "fetch('http://127.0.0.1:8282' + route, { method, headers: { authorization: 'Bearer ' + process.env.MEMORIA_QMD_SECRET, 'content-type': 'application/json' }, body: body === '' ? undefined : body })"
      + ".then(async (r) => process.stdout.write(JSON.stringify({ status: r.status, text: await r.text() })), (e) => { process.stderr.write(String(e)); process.exit(3); })";
    let failure = "";
    for (let attempt = 0; attempt < 5; attempt++) {
      const result = spawnSync("docker", ["exec", SIDECAR, "node", "-e", script, method, route, body === undefined ? "" : JSON.stringify(body)], { encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });
      if (result.status === 0 && result.stdout.startsWith("{")) return JSON.parse(result.stdout) as { status: number; text: string };
      failure = `exit ${result.status}: ${result.stderr.trim().slice(0, 300)}`;
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 1000);
    }
    if (method === "GET" && route === "/health") return { status: 0, text: failure };
    throw new Error(`${method} ${route} on the sidecar did not answer, five times: ${failure}`);
  }

  #compose(args: string[], allowFailure = false): { status: number | null; stdout: string; stderr: string } {
    const result = spawnSync("docker", ["compose", "-p", PROJECT, "--env-file", path.join(this.dir, "compose.env"), "-f", path.join(repo, "deploy", "compose.yaml"), "-f", path.join(this.dir, "compose.test.yaml"), ...args], { cwd: repo, encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });
    if (result.status !== 0 && !allowFailure) throw new Error(`docker compose ${args.slice(0, 3).join(" ")} failed: ${result.stderr}`);
    return { status: result.status, stdout: result.stdout, stderr: result.stderr };
  }

  /** The shipped file's own inputs, and the test-only override. Rewritten once the API's port is known. */
  #writeCompose(): void {
    const workers = [{ name: "nowhere", url: "http://127.0.0.1:9", tokenEnv: "MEMORIA_WORKER_TOKEN_GPU", priority: 1 }];
    writeFileSync(path.join(this.dir, "compose.env"), [
      `MEMORIA_VERSION=${VERSION}`,
      `MEMORIA_SIDECAR_SECRET=${this.sidecarSecret}`,
      // Its own, so a deployment on this host keeps its proxy network.
      `MEMORIA_PROXY_NETWORK=${PROJECT}-proxy`,
      `MEMORIA_INGEST_DIGEST_KEY=${this.digestKey}`,
      `MEMORIA_QMD_WORKERS=${JSON.stringify(workers)}`,
      `MEMORIA_WORKER_TOKEN_GPU=${this.workerToken}`,
      `MEMORIA_CORPUS=${path.join(this.dir, "corpus")}`,
      "",
    ].join("\n"));
    const user = typeof process.getuid === "function" ? `    user: "${process.getuid()}:${process.getgid?.() ?? 0}"\n` : "";
    const state = `      - { type: bind, source: ${JSON.stringify(this.dir)}, target: ${STATE} }\n`;
    const sidecarEnv: NodeJS.ProcessEnv = { ...this.options.sidecarEnv, MEMORIA_QMD_DB_PATH: `${STATE}/index.sqlite`, E2E_STALL_FILE: `${STATE}/stall.json`, MEMORIA_QMD_KEEP_WARM: "0" };
    if (sidecarEnv["NODE_OPTIONS"] !== undefined) sidecarEnv["NODE_OPTIONS"] = "--import=file:///e2e-scripts/stall-writes.mjs";
    // The suite's settings for the API, minus where it listens, where the sidecar is, and where its database is: compose and this layout say those.
    const apiEnv = Object.fromEntries(Object.entries(this.apiEnvironment()).filter(([name]) => (name.startsWith("Memoria__") || name.startsWith("Logging__")) && !["Memoria__Sidecar__BaseUrl", "Memoria__Sidecar__Secret", "Memoria__Ingest__DigestKey", "Memoria__Auth__DatabasePath"].includes(name)));
    writeFileSync(path.join(this.dir, "compose.test.yaml"), `# Test only: never shipped. See scripts/e2e/compose-layout.ts.
services:
  memoria-qmd:
    container_name: ${SIDECAR}
${user}    environment: ${JSON.stringify(sidecarEnv)}
    volumes:
${state}      - { type: bind, source: ${JSON.stringify(path.join(repo, "scripts", "e2e"))}, target: /e2e-scripts, read_only: true }
  memoria-api:
    container_name: ${API}
${user}    environment: ${JSON.stringify({ ...apiEnv, Memoria__Auth__DatabasePath: `${STATE}/control.sqlite` })}
    volumes:
${state}    ports: !override
      - "127.0.0.1:${this.#apiPort || 1}:8080"
`);
  }
}

function docker(args: string[], allowFailure = false): void {
  const result = spawnSync("docker", args, { cwd: repo, encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });
  if (result.status !== 0 && !allowFailure) throw new Error(`docker ${args.slice(0, 3).join(" ")} failed: ${result.stderr}`);
}

async function until(done: () => boolean, seconds: number): Promise<boolean> {
  for (let waited = 0; waited < seconds; waited++) {
    if (done()) return true;
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
  return done();
}
