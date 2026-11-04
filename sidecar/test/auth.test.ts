import assert from "node:assert/strict";
import { test } from "node:test";
import { bearerMatches } from "../src/auth.ts";

const SECRET = "test-secret-0123456789abcdef";

test("the right token is accepted, whatever the case of the scheme and the space around it", () => {
  for (const header of [`Bearer ${SECRET}`, `bearer ${SECRET}`, `BEARER ${SECRET}`, `Bearer   ${SECRET}`, `Bearer ${SECRET}  `]) {
    assert.equal(bearerMatches(header, SECRET), true, header);
  }
});

test("a token is refused unless it is exactly the secret", () => {
  for (const header of [`Bearer ${SECRET}x`, `Bearer ${SECRET.slice(0, -1)}`, `Bearer ${SECRET.toUpperCase()}`, "Bearer other"]) {
    assert.equal(bearerMatches(header, SECRET), false, header);
  }
});

test("the token must be the whole of the header, with nothing before or after it", () => {
  for (const header of [`Bearer ${SECRET} extra`, `Bearer extra ${SECRET}`, `xBearer ${SECRET}`, `prefix Bearer ${SECRET}`]) {
    assert.equal(bearerMatches(header, SECRET), false, header);
  }
});

test("anything that is not a bearer header is refused", () => {
  for (const header of [undefined, "", "Bearer", "Bearer ", SECRET, `Basic ${SECRET}`, `Bearer${SECRET}`]) {
    assert.equal(bearerMatches(header, SECRET), false, String(header));
  }
});
