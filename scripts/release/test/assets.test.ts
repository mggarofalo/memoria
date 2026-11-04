import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFileSync, readdirSync, writeFileSync } from "node:fs";
import path from "node:path";
import { test } from "node:test";
import { gunzipSync, inflateRawSync } from "node:zlib";
import { archive, assetName, binaryName, buildAssets, checksums, goBuildArgs, tarGz, TARGETS, zip, type Target } from "../assets.ts";
import { tempDir } from "./helpers.ts";

const EPOCH = 1_750_000_000; // 2025-06-15T15:06:40Z
const PAYLOAD = Buffer.from("not really an executable\n".repeat(50));

const octal = (block: Buffer, offset: number, width: number): number => parseInt(block.toString("ascii", offset, offset + width - 1), 8);

test("the asset names are the contract the self-updater depends on", () => {
  const names = TARGETS.map((target) => assetName("1.2.3", target)).sort();
  assert.deepEqual(names, [
    "memoria_1.2.3_darwin_amd64.tar.gz",
    "memoria_1.2.3_darwin_arm64.tar.gz",
    "memoria_1.2.3_linux_amd64.tar.gz",
    "memoria_1.2.3_linux_arm64.tar.gz",
    "memoria_1.2.3_windows_amd64.zip",
    "memoria_1.2.3_windows_arm64.zip",
  ]);
  assert.equal(binaryName({ os: "windows", arch: "amd64" }), "memoria.exe");
  assert.equal(binaryName({ os: "linux", arch: "arm64" }), "memoria");
  assert.equal(binaryName({ os: "darwin", arch: "arm64" }), "memoria");
});

test("the go build flags are the reproducible ones and stamp the version", () => {
  const args = goBuildArgs("1.2.3", "out/memoria");
  assert.ok(args.includes("-trimpath"));
  assert.ok(args.includes("-buildvcs=false"));
  const ldflags = args[args.indexOf("-ldflags") + 1] as string;
  assert.match(ldflags, /-buildid= /);
  assert.match(ldflags, /-X github\.com\/mggarofalo\/memoria\/cli\/cmd\.Version=1\.2\.3$/);
});

test("tarGz writes one valid ustar entry carrying only the name, mode, size and epoch", () => {
  const compressed = tarGz("memoria", PAYLOAD, EPOCH);
  assert.equal(compressed[9], 0xff, "no build-host byte in the gzip header");
  assert.equal(compressed.readUInt32LE(4), 0, "no timestamp in the gzip header");
  const tar = gunzipSync(compressed);

  assert.equal(tar.length % 512, 0);
  assert.equal(tar.toString("ascii", 0, 7), "memoria");
  assert.equal(octal(tar, 100, 8), 0o755);
  assert.equal(octal(tar, 108, 8), 0);
  assert.equal(octal(tar, 116, 8), 0);
  assert.equal(octal(tar, 124, 12), PAYLOAD.length);
  assert.equal(octal(tar, 136, 12), EPOCH);
  assert.equal(tar.toString("ascii", 156, 157), "0");
  assert.equal(tar.toString("latin1", 257, 265), "ustar\u000000");

  const stored = octal(tar, 148, 8);
  const blanked = Buffer.from(tar.subarray(0, 512));
  blanked.fill(" ", 148, 156);
  assert.equal(stored, blanked.reduce((sum, byte) => sum + byte, 0));

  assert.deepEqual(tar.subarray(512, 512 + PAYLOAD.length), PAYLOAD);
  assert.ok(tar.subarray(tar.length - 1024).every((byte) => byte === 0), "two zero blocks end the archive");
});

test("tarGz does not pad an exactly block-sized file", () => {
  const tar = gunzipSync(tarGz("memoria", Buffer.alloc(512, 1), EPOCH));
  assert.equal(tar.length, 512 + 512 + 1024);
});

test("zip writes one valid deflated entry dated in UTC", () => {
  const data = zip("memoria.exe", PAYLOAD, EPOCH);
  assert.equal(data.toString("latin1", 0, 4), "PK\u0003\u0004");
  const compressedSize = data.readUInt32LE(18);
  const nameLength = data.readUInt16LE(26);
  assert.equal(data.toString("ascii", 30, 30 + nameLength), "memoria.exe");
  assert.equal(data.readUInt16LE(8), 8);
  assert.equal(data.readUInt32LE(22), PAYLOAD.length);
  // 2025-06-15 15:06:40 UTC in DOS form.
  assert.equal(data.readUInt16LE(10), (15 << 11) | (6 << 5) | 20);
  assert.equal(data.readUInt16LE(12), (45 << 9) | (6 << 5) | 15);
  const start = 30 + nameLength;
  assert.deepEqual(inflateRawSync(data.subarray(start, start + compressedSize)), PAYLOAD);

  const central = start + compressedSize;
  assert.equal(data.toString("latin1", central, central + 4), "PK\u0001\u0002");
  const end = data.length - 22;
  assert.equal(data.toString("latin1", end, end + 4), "PK\u0005\u0006");
  assert.equal(data.readUInt16LE(end + 10), 1);
  assert.equal(data.readUInt32LE(end + 12), end - central);
  assert.equal(data.readUInt32LE(end + 16), central);
});

test("archives are byte-identical for the same input and change with the epoch", () => {
  for (const target of TARGETS) {
    assert.deepEqual(archive(target, PAYLOAD, EPOCH), archive(target, PAYLOAD, EPOCH));
    assert.notDeepEqual(archive(target, PAYLOAD, EPOCH), archive(target, PAYLOAD, EPOCH + 2));
  }
});

test("checksums are sha256sum format, sorted, regardless of input order", () => {
  const a = { name: "a.zip", data: Buffer.from("a") };
  const b = { name: "b.tar.gz", data: Buffer.from("b") };
  const hash = (data: Buffer): string => createHash("sha256").update(data).digest("hex");
  const expected = `${hash(a.data)}  a.zip\n${hash(b.data)}  b.tar.gz\n`;
  assert.equal(checksums([b, a]), expected);
  assert.equal(checksums([a, b]), expected);
});

test("buildAssets builds every target with cgo off and writes assets plus checksums", (t) => {
  const outDir = path.join(tempDir(t), "dist");
  const seen: Array<Record<string, string>> = [];
  const go = (args: string[], env: Record<string, string>): void => {
    seen.push(env);
    writeFileSync(args[args.indexOf("-o") + 1] as string, `${env["GOOS"]}/${env["GOARCH"]}`);
  };

  const written = buildAssets({ version: "1.2.3", outDir, epoch: EPOCH, go });

  assert.equal(seen.length, 6);
  assert.ok(seen.every((env) => env["CGO_ENABLED"] === "0"));
  assert.deepEqual([...new Set(seen.map((env) => `${env["GOOS"]}/${env["GOARCH"]}`))].length, 6);
  assert.equal(written.at(-1), "checksums.txt");
  assert.deepEqual(readdirSync(outDir).sort(), [...written].sort(), "no stray binaries left behind");

  for (const line of readFileSync(path.join(outDir, "checksums.txt"), "utf8").trimEnd().split("\n")) {
    const [hash, name] = line.split("  ") as [string, string];
    assert.equal(createHash("sha256").update(readFileSync(path.join(outDir, name))).digest("hex"), hash);
  }

  const target: Target = { os: "linux", arch: "arm64" };
  const tar = gunzipSync(readFileSync(path.join(outDir, assetName("1.2.3", target))));
  assert.equal(tar.toString("ascii", 512, 512 + "linux/arm64".length), "linux/arm64");
});

test("buildAssets replaces an earlier build and refuses an unsafe version", (t) => {
  const outDir = path.join(tempDir(t), "dist");
  const go = (args: string[]): void => writeFileSync(args[args.indexOf("-o") + 1] as string, "x");

  buildAssets({ version: "1.2.3", outDir, epoch: EPOCH, go });
  buildAssets({ version: "1.2.4", outDir, epoch: EPOCH, go });
  assert.ok(readdirSync(outDir).every((name) => !name.includes("1.2.3")));

  for (const bad of ["", "../evil", "1.2.3 -X x", "1/2"]) {
    assert.throws(() => buildAssets({ version: bad, outDir, epoch: EPOCH, go }), /not a usable version/);
  }
});
