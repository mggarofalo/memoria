/**
 * `node scripts/e2e/wireguard.ts`: the tunnel between the API host and a worker, as docs/deployment.md describes it, simulated in Docker
 * and never shipped.
 *
 * - The API host is a Docker host of its own, a dind container sharing the network of a WireGuard container, so its dockerd makes the
 *   bridge, the masquerading and the forwarding rules a real host gets, beside wg0 and ufw.
 * - The sidecar runs on it from deploy/compose.yaml as shipped, on an image built from this checkout.
 * - The worker is the real worker over the tests' fake llama-server, in the network of a WireGuard container of its own.
 * - The two hosts share only wg-wan, which carries the tunnel's UDP. The worker binds its tunnel address alone, so the sidecar reaches it
 *   only through the tunnel.
 *
 * Containers and networks are named wg-*, and removed by name at the end. Needs Docker, and a kernel with WireGuard.
 */
import { spawnSync } from "node:child_process";
import { chmodSync, copyFileSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { randomBytes } from "node:crypto";
import { fileURLToPath } from "node:url";

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const VERSION = "0.0.0-wireguard-check";
const SIDECAR_IMAGE = `ghcr.io/mggarofalo/memoria-qmd:${VERSION}`;
const WIREGUARD_IMAGE = "lscr.io/linuxserver/wireguard:1.0.20260223-r0-ls129";
const DIND_IMAGE = "docker:29-dind";
const NODE_IMAGE = "node:22-bookworm-slim";
const WAN = "wg-wan";
const HOST_TUNNEL = "wg-apihost-tunnel";
const HOST = "wg-apihost";
const WORKER_TUNNEL = "wg-worker-tunnel";
const WORKER = "wg-worker";
const CONTAINERS = [WORKER, HOST, WORKER_TUNNEL, HOST_TUNNEL];
// Addresses from docs/deployment.md: the API host is 10.0.0.1 and the GPU PC 10.0.0.3. The LAN between them is an example of our own.
const LAN = { subnet: "192.168.213.0/24", host: "192.168.213.2", worker: "192.168.213.3" };
const TUNNEL = { host: "10.0.0.1", worker: "10.0.0.3" };
const WORKER_URL = `http://${TUNNEL.worker}:8383`;
const WORKER_TOKEN = randomBytes(32).toString("base64url");

const dir = mkdtempSync(path.join(os.tmpdir(), "memoria-wireguard-"));
const results: { name: string; ok: boolean }[] = [];

function check(name: string, ok: unknown, detail?: unknown): void {
  results.push({ name, ok: Boolean(ok) });
  console.log(`${ok ? "PASS" : "FAIL"} ${name}`);
  if (!ok && detail !== undefined) console.log(`     ${String(typeof detail === "string" ? detail : JSON.stringify(detail)).slice(0, 800)}`);
}

function docker(args: string[], options: { allowFailure?: boolean; input?: string } = {}): { code: number; out: string } {
  const result = spawnSync("docker", args, { cwd: repo, encoding: "utf8", input: options.input, maxBuffer: 64 * 1024 * 1024 });
  const out = `${result.stdout ?? ""}${result.stderr ?? ""}`;
  if (result.status !== 0 && !options.allowFailure) throw new Error(`docker ${args.slice(0, 6).join(" ")} failed: ${out.slice(0, 2000)}`);
  return { code: result.status ?? -1, out };
}

/** A shell command on the API host, or in a container of its own Docker. */
function onHost(script: string, allowFailure = false): { code: number; out: string } {
  return docker(["exec", HOST, "sh", "-c", script], { allowFailure });
}

/** wg, which the WireGuard containers have, on either host. */
function wg(container: string, args: string): string {
  return docker(["exec", container, "sh", "-c", `wg ${args}`], { allowFailure: true }).out;
}

function onWorkerTunnel(script: string, allowFailure = false): { code: number; out: string } {
  return docker(["exec", WORKER_TUNNEL, "sh", "-c", script], { allowFailure });
}

/** A request from inside the sidecar's container, as the sidecar would send it. Returns the status, or the error's code. */
function fromSidecar(url: string, init: { method?: string; bytes?: number; token?: boolean } = {}): string {
  // A body of that size the worker must read to the end: texts to embed, in four parts.
  const body = init.bytes ? `,body:JSON.stringify({texts:Array(4).fill('word '.repeat(${init.bytes / 20}))}),headers:{...h,'content-type':'application/json'}` : ",headers:h";
  const script = `const h=${init.token ? "{authorization:'Bearer '+process.env.MEMORIA_WORKER_TOKEN_GPU}" : "{}"};`
    + `fetch(${JSON.stringify(url)},{method:${JSON.stringify(init.method ?? "GET")}${body},signal:AbortSignal.timeout(8000)})`
    + `.then(r=>process.stdout.write(String(r.status)),e=>process.stdout.write(String(e.cause?.code??e.name)))`;
  return onHost(`docker exec memoria-qmd node -e ${shellQuote(script)}`, true).out.trim();
}

/** A call on the sidecar's own API, from inside its container. */
function sidecar(route: string, body?: unknown): any {
  const init = body === undefined ? "{headers:h}" : `{method:'POST',headers:{...h,'content-type':'application/json'},body:${JSON.stringify(JSON.stringify(body))}}`;
  const script = `const h={authorization:'Bearer '+process.env.MEMORIA_QMD_SECRET};fetch('http://127.0.0.1:8282${route}',${init}).then(r=>r.text()).then(t=>process.stdout.write(t))`;
  return JSON.parse(onHost(`docker exec memoria-qmd node -e ${shellQuote(script)}`).out);
}

function shellQuote(text: string): string {
  return `'${text.replaceAll("'", `'"'"'`)}'`;
}

async function until<T>(what: () => T, done: (value: T) => boolean, seconds: number): Promise<T> {
  let value = what();
  for (let waited = 0; !done(value) && waited < seconds; waited++) {
    await new Promise((resolve) => setTimeout(resolve, 1000));
    value = what();
  }
  return value;
}

function keyPair(): { privateKey: string; publicKey: string } {
  const out = docker(["run", "--rm", "--entrypoint", "sh", WIREGUARD_IMAGE, "-c", "k=$(wg genkey); echo $k; echo $k | wg pubkey"]).out.trim().split(/\s+/);
  return { privateKey: out[0]!, publicKey: out[1]! };
}

/** The two wg0.conf files, in the form docs/deployment.md gives them. */
function tunnelConfigs(): { host: string; worker: string } {
  const host = keyPair();
  const worker = keyPair();
  return {
    host: `[Interface]
Address = ${TUNNEL.host}/24
ListenPort = 51820
PrivateKey = ${host.privateKey}

[Peer]
# GPU PC
PublicKey = ${worker.publicKey}
AllowedIPs = ${TUNNEL.worker}/32
`,
    worker: `[Interface]
Address = ${TUNNEL.worker}/24
PrivateKey = ${worker.privateKey}

[Peer]
# API host
PublicKey = ${host.publicKey}
Endpoint = ${LAN.host}:51820
AllowedIPs = ${TUNNEL.host}/32
PersistentKeepalive = 25
`,
  };
}

/** A WireGuard container on the LAN, holding one config, made but not started. */
function tunnel(name: string, address: string, config: string): void {
  docker(["create", "--name", name, "--network", WAN, "--ip", address, "--cap-add", "NET_ADMIN", "--sysctl", "net.ipv4.conf.all.src_valid_mark=1", "-e", "PUID=1000", "-e", "PGID=1000", WIREGUARD_IMAGE]);
  const confs = path.join(dir, name, "wg_confs");
  mkdirSync(confs, { recursive: true });
  writeFileSync(path.join(confs, "wg0.conf"), config, { mode: 0o600 });
  docker(["cp", confs, `${name}:/config/`]);
}

function prepare(): void {
  docker(["build", "--quiet", "-t", SIDECAR_IMAGE, "--build-arg", `MEMORIA_VERSION=${VERSION}`, "sidecar"]);
  // Everything the API host is given, copied in as one folder, since dind keeps /tmp in memory where docker cp cannot write.
  const deploy = path.join(dir, "deploy");
  mkdirSync(deploy);
  docker(["save", "-o", path.join(deploy, "sidecar.tar"), SIDECAR_IMAGE]);
  copyFileSync(path.join(repo, "deploy", "compose.yaml"), path.join(deploy, "compose.yaml"));
  docker(["network", "create", "--subnet", LAN.subnet, WAN]);
  const configs = tunnelConfigs();
  tunnel(HOST_TUNNEL, LAN.host, configs.host);
  tunnel(WORKER_TUNNEL, LAN.worker, configs.worker);

  // The worker's machine: empty model files, which the fake llama-server never reads, and llama-server itself.
  const models = path.join(dir, "models");
  mkdirSync(models);
  for (const file of ["Qwen3-Embedding-0.6B-Q8_0.gguf", "Qwen3-Reranker-4B.Q8_0.gguf", "qmd-query-expansion-1.7B-q4_k_m.gguf"]) writeFileSync(path.join(models, file), "");
  writeFileSync(path.join(dir, "llama-server"), "#!/bin/sh\nexec node /worker/test/fixtures/fake-llama-server.ts \"$@\"\n");
  chmodSync(path.join(dir, "llama-server"), 0o755);
  chmodSync(dir, 0o755);

  // The sidecar's compose values. The API's are set so the file is valid, though only the sidecar runs here.
  const workers = [{ name: "gpu", url: WORKER_URL, tokenEnv: "MEMORIA_WORKER_TOKEN_GPU", priority: 1 }];
  writeFileSync(path.join(deploy, "check.env"), [
    `MEMORIA_VERSION=${VERSION}`,
    `MEMORIA_SIDECAR_SECRET=${randomBytes(32).toString("base64url")}`,
    `MEMORIA_INGEST_DIGEST_KEY=${randomBytes(32).toString("base64url")}`,
    `MEMORIA_QMD_WORKERS=${JSON.stringify(workers)}`,
    `MEMORIA_WORKER_TOKEN_GPU=${WORKER_TOKEN}`,
    "",
  ].join("\n"), { mode: 0o600 });
  writeFileSync(path.join(deploy, "check.yaml"), "services:\n  memoria-qmd:\n    environment:\n      MEMORIA_QMD_WORKER_POLL_MS: \"1000\"\n");
}

/** The API host: its tunnel, then its own dockerd in the same network, with the sidecar's image loaded. */
async function apiHost(): Promise<void> {
  docker(["start", HOST_TUNNEL]);
  docker(["run", "-d", "--name", HOST, "--privileged", "--network", `container:${HOST_TUNNEL}`, "-e", "DOCKER_TLS_CERTDIR=", DIND_IMAGE]);
  const ready = await until(() => onHost("docker info --format '{{.ServerVersion}}'", true).code, (code) => code === 0, 60);
  check("API host: its own dockerd starts beside the tunnel", ready === 0);
  docker(["cp", path.join(dir, "deploy"), `${HOST}:/`]);
  onHost("docker load -q -i /deploy/sidecar.tar && rm /deploy/sidecar.tar");
  const up = await until(() => wg(HOST_TUNNEL, "show wg0 listen-port").trim(), (port) => port === "51820", 30);
  check("API host: wg0 is up from the documented config, listening on 51820", up === "51820", up);
}

/** The worker's host: its tunnel, and the real worker binding the tunnel address, over the fake llama-server. */
async function workerHost(): Promise<void> {
  docker(["start", WORKER_TUNNEL]);
  await until(() => onWorkerTunnel(`ip -4 addr show wg0`, true).out, (out) => out.includes(TUNNEL.worker), 30);
  docker(["run", "-d", "--name", WORKER, "--network", `container:${WORKER_TUNNEL}`,
    "-v", `${path.join(repo, "worker")}:/worker:ro`, "-v", `${dir}:/check:ro`, "-w", "/worker",
    "-e", "MEMORIA_WORKER_ALLOW_HTTP_ON_PRIVATE_NETWORK=1", "-e", `MEMORIA_WORKER_HOST=${TUNNEL.worker}`, "-e", `MEMORIA_WORKER_TOKEN=${WORKER_TOKEN}`,
    "-e", "MEMORIA_WORKER_LLAMA_SERVER=/check/llama-server", "-e", "MEMORIA_WORKER_MODEL_DIR=/check/models", "-e", "MEMORIA_WORKER_WARMUP=0",
    "-e", "FAKE_EMBED_DIMENSIONS=1024", NODE_IMAGE, "node", "src/main.ts"]);
  const listening = await until(() => onWorkerTunnel("netstat -ltn 2>/dev/null || ss -ltn", true).out, (out) => out.includes(`${TUNNEL.worker}:8383`), 60);
  check("worker: with the documented settings it binds its tunnel address", listening.includes(`${TUNNEL.worker}:8383`), docker(["logs", WORKER], { allowFailure: true }).out);
}

/** Without the tunnel's address there is nothing to bind, so the worker stops, as the start-order note says. */
function startOrder(): void {
  const early = docker(["run", "--rm", "--network", `container:${WORKER_TUNNEL}`, "-v", `${path.join(repo, "worker")}:/worker:ro`, "-w", "/worker",
    "-e", "MEMORIA_WORKER_ALLOW_HTTP_ON_PRIVATE_NETWORK=1", "-e", "MEMORIA_WORKER_HOST=10.0.0.9", "-e", `MEMORIA_WORKER_TOKEN=${WORKER_TOKEN}`,
    "-e", "MEMORIA_WORKER_LLAMA_SERVER=/bin/false", NODE_IMAGE, "node", "src/main.ts"], { allowFailure: true });
  check("worker: on an address the host does not have yet, it fails to start", early.code !== 0, early.out);
  const plain = docker(["run", "--rm", "--network", `container:${WORKER_TUNNEL}`, "-v", `${path.join(repo, "worker")}:/worker:ro`, "-w", "/worker",
    "-e", `MEMORIA_WORKER_HOST=${TUNNEL.worker}`, "-e", `MEMORIA_WORKER_TOKEN=${WORKER_TOKEN}`, NODE_IMAGE, "node", "src/main.ts"], { allowFailure: true });
  check("worker: without MEMORIA_WORKER_ALLOW_HTTP_ON_PRIVATE_NETWORK it refuses the tunnel address", plain.code !== 0 && plain.out.includes("MEMORIA_WORKER_ALLOW_HTTP_ON_PRIVATE_NETWORK=1"), plain.out);
}

async function handshake(): Promise<void> {
  const latest = await until(() => wg(HOST_TUNNEL, "show wg0 latest-handshakes").trim().split(/\s+/)[1] ?? "0", (time) => time !== "0", 30);
  check("tunnel: the worker's keepalive reaches the API host, which needs no Endpoint for it", latest !== "0", wg(HOST_TUNNEL, "show wg0"));
}

/** The sidecar as compose ships it, run on the API host's own Docker. */
async function sidecarUp(): Promise<void> {
  const compose = "docker compose -p wg-check --env-file /deploy/check.env -f /deploy/compose.yaml -f /deploy/check.yaml";
  const refused = onHost(`${compose} run --rm --no-deps -e MEMORIA_QMD_ALLOW_HTTP_ON_PRIVATE_NETWORK=0 memoria-qmd 2>&1`, true);
  check("sidecar: without MEMORIA_QMD_ALLOW_HTTP_ON_PRIVATE_NETWORK it refuses a plain-http worker", refused.code !== 0 && refused.out.includes("MEMORIA_QMD_ALLOW_HTTP_ON_PRIVATE_NETWORK=1"), refused.out.slice(-600));
  const made = onHost(`${compose} run --rm --no-deps memoria-qmd scripts/init-index.mjs claude-sessions codex-sessions`);
  check("sidecar: the first-run command makes the index", made.out.includes("holds: claude-sessions, codex-sessions"), made.out);
  onHost(`${compose} up -d memoria-qmd`);
  const health = await until(() => onHost("docker exec memoria-qmd node -e \"fetch('http://127.0.0.1:8282/health').then(r=>process.stdout.write(String(r.status)),()=>process.stdout.write('0'))\"", true).out.trim(), (s) => s === "200", 60);
  check("sidecar: it starts on the API host", health === "200");
}

/** What reaches the worker, and from where. */
async function reach(): Promise<void> {
  check("reach: the worker does not answer on its LAN address, only on the tunnel", ["ECONNREFUSED", "TimeoutError"].includes(fromSidecar(`http://${LAN.worker}:8383/health`)), fromSidecar(`http://${LAN.worker}:8383/health`));
  const state = await until(() => sidecar("/workers").workers?.[0]?.state, (s) => s === "up", 60);
  check("reach: the sidecar finds the worker up, over the tunnel", state === "up", sidecar("/workers"));

  workerFirewall();
  const allowed = fromSidecar(`${WORKER_URL}/health`, { token: true });
  check("reach: the worker sees the sidecar's requests from the API host's tunnel address, so its firewall can name that address", allowed === "200", allowed);
  const counted = onWorkerTunnel(`iptables -L INPUT -v -n | grep ACCEPT | grep ${TUNNEL.host}`, true).out.trim().split(/\s+/)[0];
  check("reach: and the rule allowing it is the one that counted them", Number(counted) > 0, counted);

  // Larger than the tunnel's MTU many times over, so a path that drops full-size packets would stall here.
  const big = fromSidecar(`${WORKER_URL}/v1/embed`, { method: "POST", bytes: 2 * 1024 * 1024, token: true });
  check("reach: a 2 MiB request crosses the tunnel at its default MTU, and is answered", big === "200", big);
  const mtu = onHost("cat /sys/class/net/wg0/mtu").out.trim();
  check("reach: the tunnel's MTU is wg-quick's default, with no setting", mtu === "1420", mtu);
}

/** A document embedded on the worker, through the tunnel, as the sidecar does it for real. */
async function embeds(): Promise<void> {
  const paragraph = "The axolotl ledger records each tank, its water and its feeding. ".repeat(40);
  const text = `# Ledger\n\n${Array.from({ length: 60 }, (_, i) => `## Day ${i}\n\n${paragraph}`).join("\n\n")}\n`;
  writeFileSync(path.join(dir, "ledger.md"), text);
  docker(["cp", path.join(dir, "ledger.md"), `${HOST}:/deploy/ledger.md`]);
  onHost("docker cp /deploy/ledger.md memoria-qmd:/corpus/claude-sessions/ledger.md");
  const job = sidecar("/reindex", { embed: true }) as { job: { id: string } };
  const done = await until(() => sidecar(`/reindex/jobs/${job.job.id}`) as { job: { state: string } }, (r) => r.job.state === "succeeded" || r.job.state === "failed", 180);
  check("embed: a 150 KB document is embedded on the worker through the tunnel", done.job.state === "succeeded", done.job);
}

/** The worker's firewall rule from the docs: port 8383 on the tunnel address, from the API host's tunnel address alone. */
function workerFirewall(): void {
  onWorkerTunnel(`iptables -A INPUT -p tcp -d ${TUNNEL.worker} --dport 8383 -s ${TUNNEL.host} -j ACCEPT && iptables -A INPUT -p tcp --dport 8383 -j DROP`);
}

/** The worker's machine restarting: the tunnel first, then the worker, which needs its address. */
async function restartWorkerHost(): Promise<void> {
  docker(["restart", WORKER_TUNNEL]);
  await until(() => onWorkerTunnel("ip -4 addr show wg0", true).out, (out) => out.includes(TUNNEL.worker), 30);
  workerFirewall();
  docker(["restart", WORKER]);
  await until(() => onWorkerTunnel("netstat -ltn 2>/dev/null || ss -ltn", true).out, (out) => out.includes(`${TUNNEL.worker}:8383`), 60);
}

/** ufw on the API host, enabled with its defaults, which drop forwarded traffic and deny incoming. */
async function firewall(): Promise<void> {
  onHost("apk add --no-cache -q ufw >/dev/null && ufw --force enable >/dev/null");
  const policy = onHost("iptables -S FORWARD").out;
  check("ufw: enabled with its defaults, it drops forwarded traffic", policy.startsWith("-P FORWARD DROP"), policy);
  const reached = fromSidecar(`${WORKER_URL}/health`, { token: true });
  check("ufw: the sidecar still reaches the worker with no route rule, as Docker accepts its containers' outbound traffic ahead of ufw", reached === "200", { reached, policy });

  // A worker whose tunnel starts anew comes from a new port, which ufw's default deny incoming drops.
  const endpoint = () => wg(HOST_TUNNEL, "show wg0 endpoints").trim().split(/\s+/)[1] ?? "";
  const known = endpoint();
  await restartWorkerHost();
  // The worker sends a handshake every 5 seconds until one is answered, so in 15 the API host would have learned its new port.
  await new Promise((resolve) => setTimeout(resolve, 15_000));
  const blocked = fromSidecar(`${WORKER_URL}/health`, { token: true });
  check("ufw: with its default deny incoming, a worker that starts its tunnel anew cannot reach the API host", blocked !== "200" && endpoint() === known, { blocked, known, now: endpoint() });
  onHost("ufw allow 51820/udp >/dev/null");
  const opened = await until(() => fromSidecar(`${WORKER_URL}/health`, { token: true }), (status) => status === "200", 40);
  check("ufw: with the tunnel's port allowed, the worker's handshake gets through and the sidecar reaches it", opened === "200", { opened, wg: wg(HOST_TUNNEL, "show wg0") });

  // Docker puts its rules back ahead of ufw's when it starts, as at boot.
  docker(["restart", HOST]);
  await until(() => onHost("docker info --format '{{.ServerVersion}}'", true).code, (code) => code === 0, 60);
  const after = await until(() => fromSidecar(`${WORKER_URL}/health`, { token: true }), (status) => status === "200", 60);
  check("ufw: after the host's Docker restarts beside ufw, the sidecar still reaches the worker", after === "200", { after, forward: onHost("iptables -S FORWARD", true).out });
}

const started = Date.now();
let crashed = false;
try {
  prepare();
  await apiHost();
  await workerHost();
  startOrder();
  await handshake();
  await sidecarUp();
  await reach();
  await embeds();
  await firewall();
} catch (error) {
  crashed = true;
  console.error(error);
} finally {
  if (process.env.MEMORIA_WIREGUARD_KEEP !== "1") {
    docker(["rm", "-f", "-v", ...CONTAINERS], { allowFailure: true });
    docker(["network", "rm", WAN], { allowFailure: true });
    docker(["image", "rm", SIDECAR_IMAGE], { allowFailure: true });
    rmSync(dir, { recursive: true, force: true });
  }
}

const failed = results.filter((result) => !result.ok);
console.log(`\n${results.length - failed.length} passed, ${failed.length} failed, in ${Math.round((Date.now() - started) / 1000)}s`);
process.exitCode = failed.length > 0 || crashed ? 1 : 0;
