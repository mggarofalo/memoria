import assert from "node:assert/strict";
import { link, lstat, mkdir, readdir, readFile, rename, rm, stat, symlink, writeFile } from "node:fs/promises";
import path from "node:path";
import { test } from "node:test";
import { HttpError } from "../src/errors.ts";
import { removeDocument, resolveTarget, retryTransient, writeDocument } from "../src/ingest.ts";
import { FRONTMATTER_DOC } from "./fake-backend.ts";
import { authHeaders, makeApp, tempDir } from "./helpers.ts";

const isCode = (code: string) => (err: unknown) => err instanceof HttpError && err.code === code;

/** Swap a real directory for a junction to `outside`, keeping the original aside. */
async function flip(dir: string, outside: string): Promise<void> {
  await rename(dir, `${dir}-real`);
  await symlink(outside, dir, "junction");
}

interface Fixture {
  root: string;
  outside: string;
  victim: string;
  dir: string;
}

async function fixture(): Promise<Fixture> {
  const root = await tempDir();
  const outside = await tempDir();
  const victim = path.join(outside, "victim.md");
  await writeFile(victim, "keep");
  const dir = path.join(root, "sub");
  await mkdir(dir);
  return { root, outside, victim, dir };
}

async function assertVictimUntouched(f: Fixture): Promise<void> {
  assert.equal(await readFile(f.victim, "utf8"), "keep");
  assert.deepEqual(await readdir(f.outside), ["victim.md"], "nothing else landed outside the root");
}

for (const step of ["opened", "written", "checked"] as const) {
  // Windows refuses to rename a directory while a file inside it is open, so this swap cannot happen there.
  const skip = step === "opened" && process.platform === "win32" ? "the OS blocks this swap" : false;
  test(`a directory swapped after "${step}" cannot redirect a write out of the root`, { skip }, async () => {
    const f = await fixture();
    const target = resolveTarget(f.root, "**/*.md", "sub/victim.md");
    let flipped = false;
    await assert.rejects(
      writeDocument(target, "overwritten", true, {
        onStep: async (s) => {
          if (s === step && !flipped) {
            flipped = true;
            await flip(f.dir, f.outside);
          }
        },
      }),
      (err: unknown) => err instanceof HttpError,
    );
    assert.equal(flipped, true);
    await assertVictimUntouched(f);
  });
}

test("a temp file created through a swapped directory is caught when the swap is undone", async () => {
  const f = await fixture();
  const target = resolveTarget(f.root, "**/*.md", "sub/victim.md");
  let unflipped = false;
  await assert.rejects(
    writeDocument(target, "overwritten", true, {
      onStep: async (s) => {
        if (s === "ready") await flip(f.dir, f.outside);
        if (s === "opened") {
          // Back to the real directory, so a check of the directory alone would pass.
          await rm(f.dir, { force: true });
          await rename(`${f.dir}-real`, f.dir);
          unflipped = true;
        }
      },
    }),
    isCode("path_changed"),
  );
  assert.equal(unflipped, true);
  assert.equal(await readFile(f.victim, "utf8"), "keep");
  // The only trace outside the root is an empty file: the content was never written.
  for (const name of (await readdir(f.outside)).filter((n) => n !== "victim.md")) {
    assert.equal((await stat(path.join(f.outside, name))).size, 0);
  }
});

test("a temp file created through a swapped directory that stays swapped is refused", async () => {
  const f = await fixture();
  const target = resolveTarget(f.root, "**/*.md", "sub/victim.md");
  await assert.rejects(
    writeDocument(target, "overwritten", true, {
      onStep: async (s) => {
        if (s === "ready") await flip(f.dir, f.outside);
      },
    }),
    isCode("invalid_path"),
  );
  await assertVictimUntouched(f);
});

test("a directory swapped just before the rename leaves the temp file's name unresolvable", async () => {
  const f = await fixture();
  const target = resolveTarget(f.root, "**/*.md", "sub/victim.md");
  await assert.rejects(
    writeDocument(target, "overwritten", true, {
      onStep: async (s) => {
        if (s === "checked") await flip(f.dir, f.outside);
      },
    }),
    isCode("path_changed"),
  );
  await assertVictimUntouched(f);
});

test("a directory swapped after the rename is reported, not reported as success", async () => {
  const f = await fixture();
  const target = resolveTarget(f.root, "**/*.md", "sub/x.md");
  await assert.rejects(
    writeDocument(target, "body", true, {
      onStep: async (s) => {
        if (s === "renamed") await flip(f.dir, f.outside);
      },
    }),
    isCode("invalid_path"),
  );
  await assertVictimUntouched(f);
});

test("a junction flipping under real requests never overwrites a file outside the root", async () => {
  const f = await fixture();
  const { app, backend } = await makeApp();
  const root = backend.collections[0]!.root;
  const dir = path.join(root, "sub");
  await mkdir(dir);
  const payload = { collection: "claude-sessions", path: "sub/victim.md", content: "overwritten" };
  let running = true;
  const flipper = (async () => {
    while (running) {
      await rename(dir, `${dir}-real`).catch(() => undefined);
      await symlink(f.outside, dir, "junction").catch(() => undefined);
      await new Promise((resolve) => setImmediate(resolve));
      await rm(dir, { recursive: false, force: true }).catch(() => undefined);
      await rename(`${dir}-real`, dir).catch(() => undefined);
      await new Promise((resolve) => setImmediate(resolve));
    }
  })();
  // Bounded by time as well: refused attempts wait on retries, so the fixed code gets through fewer of them.
  const deadline = Date.now() + 4000;
  try {
    for (let i = 0; i < 150 && Date.now() < deadline; i++) {
      await app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload });
      await app.inject({ method: "DELETE", url: "/documents?collection=claude-sessions&path=sub/victim.md", headers: authHeaders });
    }
  } finally {
    running = false;
    await flipper;
    await app.close();
  }
  // A delete's rename aside can land out here and stay under a temp name. The content must survive it.
  const contents = await Promise.all((await readdir(f.outside)).map((name) => readFile(path.join(f.outside, name), "utf8")));
  assert.ok(contents.includes("keep"), "the outside file's content is still there");
  assert.ok(!contents.includes("overwritten"), "no document content reached the outside directory");
});

test("the rename replaces a hard link instead of writing through it", async () => {
  const f = await fixture();
  const target = resolveTarget(f.root, "**/*.md", "hardlink.md");
  await writeDocument(target, "body", true, {
    onStep: async (s) => {
      // Past every check: only the rename stands between this link and the outside file.
      if (s === "checked") await link(f.victim, target.absolute);
    },
  });
  assert.equal(await readFile(f.victim, "utf8"), "keep");
  assert.equal(await readFile(target.absolute, "utf8"), "body");
  assert.equal((await stat(f.victim)).nlink, 1);
});

test("overwriting a hard-linked file is refused and leaves both names intact", async () => {
  const f = await fixture();
  const target = resolveTarget(f.root, "**/*.md", "hardlink.md");
  await link(f.victim, target.absolute);
  await assert.rejects(writeDocument(target, "overwritten", true), isCode("hard_link"));
  assert.equal(await readFile(f.victim, "utf8"), "keep");
  assert.equal(await readFile(target.absolute, "utf8"), "keep");
});

test("deleting a hard link removes that name only", async () => {
  const f = await fixture();
  const target = resolveTarget(f.root, "**/*.md", "hardlink.md");
  await link(f.victim, target.absolute);
  assert.equal(await removeDocument(target), true);
  await assert.rejects(readFile(target.absolute), /ENOENT/);
  assert.equal(await readFile(f.victim, "utf8"), "keep");
});

test("a failed write leaves no temp file behind and the old document in place", async () => {
  const f = await fixture();
  const target = resolveTarget(f.root, "**/*.md", "sub/x.md");
  await writeDocument(target, "old", true);
  await assert.rejects(
    writeDocument(target, "new", true, {
      onStep: async (s) => {
        if (s === "written") throw new Error("boom");
      },
    }),
    /boom/,
  );
  assert.equal(await readFile(target.absolute, "utf8"), "old");
  assert.deepEqual(await readdir(f.dir), ["x.md"]);
});

test("a successful write leaves only the document", async () => {
  const f = await fixture();
  await writeDocument(resolveTarget(f.root, "**/*.md", "sub/x.md"), "body", true);
  assert.deepEqual(await readdir(f.dir), ["x.md"]);
});

test("an overwrite keeps the file's permission bits", { skip: process.platform === "win32" ? "POSIX mode bits" : false }, async () => {
  const f = await fixture();
  const target = resolveTarget(f.root, "**/*.md", "secret.md");
  await writeFile(target.absolute, "old", { mode: 0o600 });
  await writeDocument(target, "new", true);
  assert.equal((await stat(target.absolute)).mode & 0o777, 0o600);
});

test("a directory swapped before a delete's rename gets the outside file put back", async () => {
  const f = await fixture();
  const target = resolveTarget(f.root, "**/*.md", "sub/victim.md");
  await writeDocument(target, "inside", true);
  await assert.rejects(
    removeDocument(target, {
      onStep: async (s) => {
        if (s === "checked") await flip(f.dir, f.outside);
      },
    }),
    isCode("invalid_path"),
  );
  await assertVictimUntouched(f);
});

test("a directory swapped after a delete's rename cannot touch the outside file", async () => {
  const f = await fixture();
  const target = resolveTarget(f.root, "**/*.md", "sub/victim.md");
  await writeDocument(target, "inside", true);
  await assert.rejects(
    removeDocument(target, {
      onStep: async (s) => {
        if (s === "moved") await flip(f.dir, f.outside);
      },
    }),
    isCode("invalid_path"),
  );
  await assertVictimUntouched(f);
  // The document went aside with the real directory: inside the root, hidden from QMD.
  assert.equal((await readdir(`${f.dir}-real`)).length, 1);
});

test("a delete of a target that vanishes before the rename reports nothing deleted", async () => {
  const f = await fixture();
  const target = resolveTarget(f.root, "**/*.md", "sub/x.md");
  assert.equal(await removeDocument(target), false);
});

test("a link at the last segment pointing outside the root can be deleted, leaving its target alone", async () => {
  const f = await fixture();
  // A junction, so this runs on Windows without the symlink privilege.
  await symlink(f.outside, path.join(f.root, "linked.md"), "junction");
  assert.equal(await removeDocument(resolveTarget(f.root, "**/*.md", "linked.md")), true);
  await assert.rejects(lstat(path.join(f.root, "linked.md")), /ENOENT/);
  await assertVictimUntouched(f);
});

test("a link between the root and the file is refused even when it stays inside the root", async () => {
  const f = await fixture();
  await writeFile(path.join(f.dir, "x.md"), "keep");
  await symlink(f.dir, path.join(f.root, "alias"), "junction");
  const target = resolveTarget(f.root, "**/*.md", "alias/x.md");
  await assert.rejects(writeDocument(target, "new", true), isCode("invalid_path"));
  await assert.rejects(removeDocument(target), isCode("invalid_path"));
  assert.equal(await readFile(path.join(f.dir, "x.md"), "utf8"), "keep");
});

test("a file used as a directory is a conflict, not a server error", async () => {
  const f = await fixture();
  await writeFile(path.join(f.root, "file.md"), "keep");
  await assert.rejects(writeDocument(resolveTarget(f.root, "**/*.md", "file.md/x.md"), "x", true), isCode("not_a_directory"));
  assert.equal(await readFile(path.join(f.root, "file.md"), "utf8"), "keep");
});

test("blank content is refused, because QMD would leave the old text indexed", async () => {
  const f = await fixture();
  const target = resolveTarget(f.root, "**/*.md", "x.md");
  await writeDocument(target, "body", true);
  for (const blank of ["", "  ", "\n\r\n\t"]) await assert.rejects(writeDocument(target, blank, true), isCode("empty_content"));
  assert.equal(await readFile(target.absolute, "utf8"), "body");
});

test("retryTransient retries sharing errors, gives up eventually, and passes others through", async () => {
  let calls = 0;
  const flaky = async () => {
    calls++;
    if (calls < 3) throw Object.assign(new Error("busy"), { code: "EBUSY" });
    return "done";
  };
  assert.equal(await retryTransient(flaky, [0, 0, 0]), "done");
  assert.equal(calls, 3);

  calls = 0;
  await assert.rejects(
    retryTransient(async () => {
      calls++;
      throw Object.assign(new Error("denied"), { code: "EPERM" });
    }, [0, 0]),
    /denied/,
  );
  assert.equal(calls, 3);

  calls = 0;
  await assert.rejects(
    retryTransient(async () => {
      calls++;
      throw Object.assign(new Error("other"), { code: "ENOSPC" });
    }, [0, 0]),
    /other/,
  );
  assert.equal(calls, 1);
});

// --- through the routes ---------------------------------------------------------

const post = (app: Awaited<ReturnType<typeof makeApp>>["app"], p: string, content = "# x\n") =>
  app.inject({ method: "POST", url: "/documents", headers: authHeaders, payload: { collection: "claude-sessions", path: p, content } });

test("bad names return 400 from both routes and create nothing", async () => {
  const { app, roots } = await makeApp();
  try {
    const names = ["hidden~1.md", "obsidi~1/workspace.md", "NUL/x.md", "CON.md", "com1.tar.md", "a<b.md", 'a"b.md', "a|b.md", "a?b.md", "a*b.md", "a\x01b.md", "a‮b.md", "node_modules/x.md", "vendor/x.md", "dist/x.md", "build/x.md", "x/vendor/y.md", " lead.md", `${"a".repeat(300)}.md`];
    for (const p of names) {
      const created = await post(app, p);
      assert.equal(created.statusCode, 400, JSON.stringify(p));
      assert.equal(created.json().error, "invalid_path", JSON.stringify(p));
      const deleted = await app.inject({ method: "DELETE", url: `/documents?collection=claude-sessions&path=${encodeURIComponent(p)}`, headers: authHeaders });
      assert.equal(deleted.statusCode, 400, JSON.stringify(p));
    }
    assert.deepEqual(await readdir(roots.claude), []);
    assert.deepEqual(app.ctx.jobs.list(), []);
  } finally {
    await app.close();
  }
});

test("a short name of a hidden file on this volume cannot reach it", async () => {
  const { app, roots } = await makeApp();
  try {
    const hidden = path.join(roots.claude, ".hidden.md");
    await writeFile(hidden, "keep");
    assert.equal((await post(app, "hidden~1.md", "overwritten")).statusCode, 400);
    assert.equal(await readFile(hidden, "utf8"), "keep");
  } finally {
    await app.close();
  }
});

test("a case alias cannot overwrite or delete a document under the wrong key", async () => {
  const { app, roots } = await makeApp();
  try {
    assert.equal((await post(app, "keep.md", FRONTMATTER_DOC({ project: "Original" }, "# keep\n"))).statusCode, 202);
    const alias = await post(app, "KEEP.md", FRONTMATTER_DOC({ project: "Alias" }, "# alias\n"));
    assert.equal(alias.statusCode, 409);
    assert.equal(alias.json().error, "case_conflict");
    assert.equal(app.ctx.metadata.get("claude-sessions/keep.md")?.project, "Original");
    assert.equal(app.ctx.metadata.get("claude-sessions/KEEP.md"), undefined);
    assert.match(await readFile(path.join(roots.claude, "keep.md"), "utf8"), /Original/);

    const deleted = await app.inject({ method: "DELETE", url: "/documents?collection=claude-sessions&path=KEEP.md", headers: authHeaders });
    assert.equal(deleted.statusCode, 409);
    assert.equal(deleted.json().error, "case_conflict");
    assert.deepEqual(await readdir(roots.claude), ["keep.md"]);

    // The same document under its own name still works.
    assert.equal((await post(app, "keep.md", "# v2\n")).statusCode, 202);
  } finally {
    await app.close();
  }
});

test("a case alias of a directory is refused too", async () => {
  const { app, roots } = await makeApp();
  try {
    assert.equal((await post(app, "sub/a.md")).statusCode, 202);
    const alias = await post(app, "SUB/b.md");
    assert.equal(alias.statusCode, 409);
    assert.equal(alias.json().error, "case_conflict");
    assert.deepEqual(await readdir(path.join(roots.claude, "sub")), ["a.md"]);
  } finally {
    await app.close();
  }
});

test("POST reports the metadata a reindex will report when frontmatter overruns the head window", async () => {
  const { app } = await makeApp();
  try {
    const padding = Array.from({ length: 600 }, (_, i) => `/some/long/path/number/${i}.md`);
    const huge = FRONTMATTER_DOC({ project: "Big", files_touched: padding }, "# Big\n\nbody\n");
    assert.ok(huge.indexOf("\n---\n", 4) > 8192, "the fixture's frontmatter must close past the window");
    const small = FRONTMATTER_DOC({ project: "Small" }, "# Small\n");

    const posted = await post(app, "big.md", huge);
    assert.equal(posted.statusCode, 202);
    assert.equal(posted.json().metadata, null);
    const smallPosted = await post(app, "small.md", small);
    assert.equal(smallPosted.json().metadata.project, "Small");

    await app.ctx.jobs.idle();
    assert.equal(app.ctx.metadata.get("claude-sessions/big.md"), undefined);
    assert.equal(app.ctx.metadata.get("claude-sessions/small.md")?.project, "Small");
  } finally {
    await app.close();
  }
});

test("lstat agrees that a junction is a link, which the containment walk relies on", async () => {
  const f = await fixture();
  const junction = path.join(f.root, "j");
  await symlink(f.outside, junction, "junction");
  assert.equal((await lstat(junction)).isSymbolicLink(), true);
});
