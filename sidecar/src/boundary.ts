import type { Server } from "node:http";
import { isIP, type Socket } from "node:net";

/**
 * Checks that a request came to this service on purpose. The bearer secret guards the
 * data, and these guard against a browser being used to reach it: a web page can make a
 * browser send requests here, with a hostile DNS name pointing at this address, or from
 * another origin. Memoria.Api is a server and sends neither signal.
 */

/**
 * The name is what DNS rebinding controls. An address cannot be rebound, so any IP
 * address is fine, which keeps a LAN deployment working without configuration.
 */
function hostAllowed(hostHeader: string | undefined, allowedHosts: ReadonlySet<string>): boolean {
  if (hostHeader === undefined) return false;
  const name = hostHeader.trim().toLowerCase().replace(/:\d+$/, "");
  if (isIP(name.replace(/^\[|\]$/g, "")) !== 0) return true;
  return allowedHosts.has(name);
}

export interface BoundaryRefusal {
  statusCode: number;
  error: string;
  message: string;
}

export function refuseAtBoundary(headers: { host?: string | undefined; origin?: string | undefined }, allowedHosts: ReadonlySet<string>): BoundaryRefusal | null {
  if (headers.origin !== undefined) {
    return { statusCode: 403, error: "origin_forbidden", message: "This service does not take requests from browsers" };
  }
  if (!hostAllowed(headers.host, allowedHosts)) {
    return { statusCode: 421, error: "host_not_allowed", message: "This host name is not allowed. Add it to MEMORIA_QMD_ALLOWED_HOSTS, or use an IP address" };
  }
  return null;
}

/**
 * The total cap alone would let one peer, with no secret, hold every connection and lock
 * out the API. This stops any one address holding more than its share: with the defaults
 * it takes 16 addresses to reach the total.
 */
export function limitConnectionsPerSource(server: Server, perSource: number, trusted: { addresses: ReadonlySet<string>; limit: number }): void {
  const held = new Map<string, number>();
  server.on("connection", (socket: Socket) => {
    const source = (socket.remoteAddress ?? "unknown").replace(/^::ffff:/, "");
    const count = (held.get(source) ?? 0) + 1;
    if (count > (trusted.addresses.has(source) ? trusted.limit : perSource)) {
      socket.destroy();
      return;
    }
    held.set(source, count);
    socket.once("close", () => {
      const left = held.get(source)! - 1;
      if (left === 0) held.delete(source);
      else held.set(source, left);
    });
  });
}

/** W3C trace ids and UUIDs fit. Anything else in a log line is the caller's to control. */
const REQUEST_ID = /^[A-Za-z0-9._:-]{1,128}$/;

export function validRequestId(header: string | string[] | undefined): string | null {
  return typeof header === "string" && REQUEST_ID.test(header) ? header : null;
}

const MAX_LOGGED_PATH = 200;

/** The path alone, bounded: a query string can hold anything, including what was searched for. */
export function loggablePath(url: string): string {
  const path = url.split("?", 1)[0]!;
  return path.length > MAX_LOGGED_PATH ? `${path.slice(0, MAX_LOGGED_PATH)}...` : path;
}
