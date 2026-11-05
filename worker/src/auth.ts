import { createHash, timingSafeEqual } from "node:crypto";

/**
 * Constant-time comparison of a presented bearer token against the shared
 * secret. Hashing gives equal lengths, so comparison time reveals nothing about
 * either value.
 */
export function tokenMatches(header: string | undefined, secret: string): boolean {
  if (header === undefined) return false;
  // The token starts at a non-space, so the two parts cannot compete for spaces and backtrack.
  const match = /^bearer\s+(\S.*)$/i.exec(header.trim());
  if (!match) return false;
  const presented = createHash("sha256").update(match[1]!).digest();
  const expected = createHash("sha256").update(secret).digest();
  return timingSafeEqual(presented, expected);
}
