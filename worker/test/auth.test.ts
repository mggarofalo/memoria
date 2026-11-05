import assert from "node:assert/strict";
import { test } from "node:test";
import { tokenMatches } from "../src/auth.ts";

const SECRET = "worker-test-token-0123456789";

test("the right token is accepted, in any case of the scheme and with surrounding space", () => {
  for (const header of [`Bearer ${SECRET}`, `bearer ${SECRET}`, `BEARER   ${SECRET}`, `  Bearer ${SECRET}  `]) {
    assert.equal(tokenMatches(header, SECRET), true, header);
  }
});

test("a token is refused unless it is exactly the secret", () => {
  for (const header of [`Bearer ${SECRET}x`, `Bearer ${SECRET.slice(0, -1)}`, `Bearer ${SECRET.toUpperCase()}`, "Bearer other", `Bearer x ${SECRET}`]) {
    assert.equal(tokenMatches(header, SECRET), false, header);
  }
});

test("the token must reach the end of the header, so a trailing line cannot hide behind a good one", () => {
  assert.equal(tokenMatches(`Bearer ${SECRET}\nextra`, SECRET), false);
});

test("a long run of spaces is refused promptly rather than backtracked over", () => {
  const started = performance.now();
  assert.equal(tokenMatches(`Bearer ${" ".repeat(100_000)}x\ny`, SECRET), false);
  assert.ok(performance.now() - started < 1_000);
});

test("anything that is not a bearer header is refused", () => {
  for (const header of [undefined, "", "Bearer", "Bearer ", SECRET, `Basic ${SECRET}`, `Token ${SECRET}`, `xBearer ${SECRET}`, `Bearer${SECRET}`]) {
    assert.equal(tokenMatches(header, SECRET), false, String(header));
  }
});

test("a secret that happens to read like a header is compared as a secret", () => {
  assert.equal(tokenMatches("Bearer Bearer x", "Bearer x"), true);
  assert.equal(tokenMatches("Bearer x", "Bearer x"), false);
});
