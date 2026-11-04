import { createHash } from "node:crypto";
import { mkdirSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import path from "node:path";
import { crc32, deflateRawSync, gzipSync } from "node:zlib";

/**
 * Reproducible release assets: the CLI for each platform, and the sidecar and
 * worker packages. Archives are written here rather than by tar and zip so they
 * carry no build-host user, timezone or tool version: the same commit gives the
 * same bytes. The asset names are a contract (docs/releasing.md).
 */

export interface Target {
  os: "windows" | "linux" | "darwin";
  arch: "amd64" | "arm64";
}

export interface Asset {
  name: string;
  data: Buffer;
}

export interface TarEntry {
  name: string;
  data: Buffer;
  mode: number;
}

export interface Service {
  name: string;
  /** Its directory in the repository. */
  dir: string;
  /** The directory holding its code in the package, where VERSION goes. */
  stampDir: string;
  /** Files and directories to ship, relative to `dir`. */
  include: readonly string[];
}

export const TARGETS: readonly Target[] = (["windows", "linux", "darwin"] as const).flatMap((os) =>
  (["amd64", "arm64"] as const).map((arch) => ({ os, arch })),
);

/**
 * The files each service needs to run and to install; a missing one fails the
 * build. The stamp goes in the code directory because the service installers copy
 * that directory: it has to reach an install location that holds no `.git`.
 */
export const SERVICES: readonly Service[] = [
  { name: "memoria-sidecar", dir: "sidecar", stampDir: "dist", include: ["dist", "scripts/install-service.ps1", "scripts/uninstall-service.ps1", ".env.example", "README.md", "package.json", "package-lock.json"] },
  { name: "memoria-worker", dir: "worker", stampDir: "src", include: ["src", "scripts", "README.md", "package.json", "package-lock.json"] },
];

/** In every service package, at the same relative path as in the repository. */
export const SHARED_FILES: readonly string[] = ["deploy/windows/MemoriaService.psm1"];

const VERSION_VARIABLE = "github.com/mggarofalo/memoria/cli/cmd.Version";
const SAFE_VERSION = /^[0-9A-Za-z][0-9A-Za-z.+-]*$/;
const TAR_BLOCK = 512;
const TAR_NAME_LIMIT = 100;

export function assetName(version: string, target: Target): string {
  return `memoria_${version}_${target.os}_${target.arch}.${target.os === "windows" ? "zip" : "tar.gz"}`;
}

/** The hyphen is deliberate: the self-updater's `memoria_` prefix can never match these. */
export function serviceAssetName(version: string, service: Service): string {
  return `${service.name}_${version}.tar.gz`;
}

export function binaryName(target: Target): string {
  return target.os === "windows" ? "memoria.exe" : "memoria";
}

export function goBuildArgs(version: string, output: string): string[] {
  return ["build", "-trimpath", "-buildvcs=false", "-ldflags", `-s -w -buildid= -X ${VERSION_VARIABLE}=${version}`, "-o", output, "."];
}

function writeOctal(header: Buffer, value: number, offset: number, width: number): void {
  header.write(value.toString(8).padStart(width - 1, "0"), offset, "ascii");
}

function tarEntry({ name, data, mode }: TarEntry, epoch: number): Buffer {
  if (name.length > TAR_NAME_LIMIT) throw new Error(`path too long for a tar header: ${name}`);
  const header = Buffer.alloc(TAR_BLOCK);
  header.write(name, 0, "ascii");
  writeOctal(header, mode, 100, 8);
  writeOctal(header, 0, 108, 8);
  writeOctal(header, 0, 116, 8);
  writeOctal(header, data.length, 124, 12);
  writeOctal(header, epoch, 136, 12);
  header.fill(" ", 148, 156);
  header.write("0", 156, "ascii");
  header.write("ustar\u000000", 257, "ascii");
  const sum = header.reduce((total, byte) => total + byte, 0);
  header.write(`${sum.toString(8).padStart(6, "0")}\u0000 `, 148, "ascii");
  return Buffer.concat([header, data, Buffer.alloc((TAR_BLOCK - (data.length % TAR_BLOCK)) % TAR_BLOCK)]);
}

/** A gzipped ustar archive, sorted by name, owned by nobody, every entry dated `epoch`. */
export function tarGz(entries: readonly TarEntry[], epoch: number): Buffer {
  const sorted = [...entries].sort((a, b) => (a.name < b.name ? -1 : 1));
  const blocks = [...sorted.map((entry) => tarEntry(entry, epoch)), Buffer.alloc(TAR_BLOCK * 2)];
  const gz = gzipSync(Buffer.concat(blocks), { level: 9 });
  gz[9] = 0xff; // gzip's "unknown OS" byte; zlib otherwise records the build host's.
  return gz;
}

function dosDateTime(epoch: number): { time: number; date: number } {
  const d = new Date(epoch * 1000);
  return {
    time: (d.getUTCHours() << 11) | (d.getUTCMinutes() << 5) | Math.floor(d.getUTCSeconds() / 2),
    date: ((d.getUTCFullYear() - 1980) << 9) | ((d.getUTCMonth() + 1) << 5) | d.getUTCDate(),
  };
}

/** A zip of one file, deflated, dated `epoch` in UTC. */
export function zip(name: string, data: Buffer, epoch: number): Buffer {
  const fileName = Buffer.from(name, "ascii");
  const body = deflateRawSync(data, { level: 9 });
  const { time, date } = dosDateTime(epoch);

  // The fields a local header and a central directory entry have in common.
  const common = Buffer.alloc(26);
  common.writeUInt16LE(20, 0); // version needed
  common.writeUInt16LE(0, 2); // flags
  common.writeUInt16LE(8, 4); // deflate
  common.writeUInt16LE(time, 6);
  common.writeUInt16LE(date, 8);
  common.writeUInt32LE(crc32(data), 10);
  common.writeUInt32LE(body.length, 14);
  common.writeUInt32LE(data.length, 18);
  common.writeUInt16LE(fileName.length, 22);

  const local = Buffer.concat([Buffer.from("PK\u0003\u0004", "latin1"), common, fileName, body]);

  const centralPrefix = Buffer.alloc(6);
  centralPrefix.write("PK\u0001\u0002", 0, "latin1");
  centralPrefix.writeUInt16LE(20, 4); // version made by
  const central = Buffer.concat([centralPrefix, common, Buffer.alloc(14), fileName]);

  const end = Buffer.alloc(22);
  end.write("PK\u0005\u0006", 0, "latin1");
  end.writeUInt16LE(1, 8);
  end.writeUInt16LE(1, 10);
  end.writeUInt32LE(central.length, 12);
  end.writeUInt32LE(local.length, 16);
  return Buffer.concat([local, central, end]);
}

export function archive(target: Target, data: Buffer, epoch: number): Buffer {
  const name = binaryName(target);
  return target.os === "windows" ? zip(name, data, epoch) : tarGz([{ name, data, mode: 0o755 }], epoch);
}

function listFiles(root: string, relative: string): string[] {
  if (!statSync(path.join(root, relative)).isDirectory()) return [relative];
  return readdirSync(path.join(root, relative))
    .sort()
    .flatMap((name) => listFiles(root, `${relative}/${name}`));
}

/** Everything in a service package: its allowlisted files, the shared ones, and the VERSION stamp beside the code. */
export function serviceEntries(service: Service, tree: string, version: string): TarEntry[] {
  const dir = path.join(tree, service.dir);
  const entry = (root: string, name: string): TarEntry => ({ name, data: readFileSync(path.join(root, name)), mode: name.endsWith(".sh") ? 0o755 : 0o644 });
  return [
    { name: `${service.stampDir}/VERSION`, data: Buffer.from(`${version}\n`, "ascii"), mode: 0o644 },
    ...service.include.flatMap((item) => listFiles(dir, item)).map((name) => entry(dir, name)),
    ...SHARED_FILES.map((name) => entry(tree, name)),
  ];
}

/** sha256sum format, sorted by name so the file's bytes do not depend on build order. */
export function checksums(assets: readonly Asset[]): string {
  return [...assets]
    .sort((a, b) => (a.name < b.name ? -1 : 1))
    .map((asset) => `${createHash("sha256").update(asset.data).digest("hex")}  ${asset.name}\n`)
    .join("");
}

export interface BuildOptions {
  version: string;
  outDir: string;
  /** Commit timestamp; the archives' only clock. */
  epoch: number;
  /** Runs `go` in the CLI module with the extra environment. */
  go: (args: string[], env: Record<string, string>) => void;
  /** A tree holding the services and shared files from the commit, the sidecar compiled. */
  serviceTree: string;
}

/** Build every asset into outDir and return what was written, ending with checksums.txt. */
export function buildAssets(options: BuildOptions): string[] {
  const { version, outDir, epoch, go, serviceTree } = options;
  if (!SAFE_VERSION.test(version)) throw new Error(`not a usable version: ${version}`);
  rmSync(outDir, { recursive: true, force: true });
  mkdirSync(outDir, { recursive: true });

  const cli = TARGETS.map((target): Asset => {
    const binary = path.join(outDir, binaryName(target));
    go(goBuildArgs(version, binary), { CGO_ENABLED: "0", GOOS: target.os, GOARCH: target.arch, GOFLAGS: "-mod=readonly" });
    const data = readFileSync(binary);
    rmSync(binary);
    return { name: assetName(version, target), data: archive(target, data, epoch) };
  });
  const services = SERVICES.map((service): Asset => ({
    name: serviceAssetName(version, service),
    data: tarGz(serviceEntries(service, serviceTree, version), epoch),
  }));

  const assets = [...cli, ...services];
  for (const asset of assets) writeFileSync(path.join(outDir, asset.name), asset.data);
  writeFileSync(path.join(outDir, "checksums.txt"), checksums(assets), "utf8");
  return [...assets.map((asset) => asset.name), "checksums.txt"];
}
