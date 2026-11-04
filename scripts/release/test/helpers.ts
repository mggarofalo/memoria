import { execFileSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import type { TestContext } from "node:test";
import type { Tools } from "../cli.ts";

/** A throwaway repository on a local disk; nothing here can reach a remote. */
export interface TempRepo {
  dir: string;
  git: (...args: string[]) => string;
  commit: (message: string) => string;
  tag: (name: string, ref?: string) => void;
}

export function tempDir(t: TestContext): string {
  const dir = mkdtempSync(path.join(tmpdir(), "memoria-release-"));
  t.after(() => rmSync(dir, { recursive: true, force: true, maxRetries: 5 }));
  return dir;
}

export function tempRepo(t: TestContext): TempRepo {
  const dir = tempDir(t);
  const git = (...args: string[]): string => execFileSync("git", args, { cwd: dir, encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });
  git("init", "-q", "-b", "main");
  git("config", "user.name", "Release Test");
  git("config", "user.email", "release-test@example.invalid");
  git("config", "commit.gpgsign", "false");
  git("config", "tag.gpgsign", "false");
  let count = 0;
  return {
    dir,
    git,
    commit: (message) => {
      writeFileSync(path.join(dir, "file.txt"), `${++count}\n`);
      git("add", "file.txt");
      git("commit", "-q", "-m", message);
      return git("rev-parse", "HEAD").trim();
    },
    tag: (name, ref = "HEAD") => {
      git("tag", "-a", name, "-m", name, ref);
    },
  };
}

export interface FakeRelease {
  tools: Tools;
  lines: string[];
  errors: string[];
  ghCalls: string[][];
}

/** Tools over a real temporary repository and a scripted gh and go. */
export function fakeTools(repo: TempRepo, gh: (args: string[]) => string = () => "", go: Tools["go"] = () => {}): FakeRelease {
  const lines: string[] = [];
  const errors: string[] = [];
  const ghCalls: string[][] = [];
  return {
    lines,
    errors,
    ghCalls,
    tools: {
      root: repo.dir,
      repo: "owner/name",
      git: (args) => repo.git(...args),
      gh: (args) => {
        ghCalls.push(args);
        return gh(args);
      },
      go,
      print: (line) => lines.push(line),
    },
  };
}
