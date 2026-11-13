import assert from "node:assert/strict";
import { test } from "node:test";
import { makeApp } from "./helpers.ts";

/** A reindex job and its scan, which runs on a thread of its own: the job's side, on the fake backend. */

const heads = (calls: Array<{ method: string }>): number => calls.filter((c) => c.method === "readDocumentHeads").length;

test("a job takes its metadata from the scan, without reading heads on this thread", async () => {
  const { app, backend } = await makeApp();
  try {
    const before = heads(backend.calls);
    const job = app.ctx.jobs.enqueue({ collections: null, embed: true, force: false, reason: "test" });
    await app.ctx.jobs.idle();
    const ended = app.ctx.jobs.get(job.id)!;
    assert.equal(ended.state, "succeeded", ended.error ?? "");
    assert.ok(app.ctx.metadata.size > 0);
    assert.deepEqual(ended.result!.metadata, { documents: backend.docs.length, withMetadata: app.ctx.metadata.size });
    assert.equal(heads(backend.calls), before, "the heads were read with the scan");
    assert.equal("metadata" in ended.result!.update, false, "the parsed metadata is not kept in the job");
  } finally {
    await app.close();
  }
});

test("a backend whose scan reads no metadata has it read here instead", async () => {
  const { app, backend } = await makeApp();
  backend.scanReadsMetadata = false;
  try {
    const before = heads(backend.calls);
    const job = app.ctx.jobs.enqueue({ collections: null, embed: false, force: false, reason: "test" });
    await app.ctx.jobs.idle();
    assert.equal(app.ctx.jobs.get(job.id)!.state, "succeeded");
    assert.equal(heads(backend.calls), before + 1);
  } finally {
    await app.close();
  }
});

test("cancelling a job mid-scan stops the scan, and the job reports Cancelled", async () => {
  const { app, backend } = await makeApp();
  let open = (): void => {};
  backend.updateGate = new Promise((resolve) => (open = resolve));
  try {
    const job = app.ctx.jobs.enqueue({ collections: null, embed: true, force: false, reason: "test" });
    for (let i = 0; i < 20 && app.ctx.jobs.get(job.id)!.state !== "running"; i++) await new Promise((r) => setImmediate(r));
    assert.equal(app.ctx.jobs.get(job.id)!.state, "running");
    app.ctx.jobs.cancel(job.id);
    open();
    await app.ctx.jobs.idle();
    const ended = app.ctx.jobs.get(job.id)!;
    assert.equal(ended.state, "failed");
    assert.equal(ended.error, "Cancelled");
    assert.equal(backend.calls.filter((c) => c.method === "embed").length, 0, "nothing ran after the scan stopped");
  } finally {
    await app.close();
  }
});

test("a scan that fails for another reason fails the job with its own error", async () => {
  const { app, backend } = await makeApp();
  backend.update = async () => {
    throw new Error("The scan thread exited with code 1 before it answered");
  };
  try {
    const job = app.ctx.jobs.enqueue({ collections: null, embed: false, force: false, reason: "test" });
    await app.ctx.jobs.idle();
    assert.equal(app.ctx.jobs.get(job.id)!.state, "failed");
    assert.equal(app.ctx.jobs.get(job.id)!.error, "The scan thread exited with code 1 before it answered");
  } finally {
    await app.close();
  }
});
