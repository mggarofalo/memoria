import { createHash, timingSafeEqual } from "node:crypto";

/**
 * Constant-time comparison of a presented bearer token against the shared
 * secret. Both sides are hashed first so length differences do not leak.
 */
export function bearerMatches(header: string | undefined, secret: string): boolean {
  if (!header) return false;
  const match = header.match(/^Bearer\s+(\S+)\s*$/i);
  if (!match) return false;
  const presented = createHash("sha256").update(match[1]!).digest();
  const expected = createHash("sha256").update(secret).digest();
  return timingSafeEqual(presented, expected);
}
