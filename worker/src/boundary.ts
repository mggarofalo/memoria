import { isIP } from "node:net";

/**
 * A web page can make a browser reach this address, with a hostile DNS name pointing at
 * it or from another origin, and in no-token mode nothing else would stop that. The
 * sidecar is a server and sends neither signal. An IP address cannot be rebound.
 */
export function refuseAtBoundary(headers: { host?: string | undefined; origin?: string | undefined }, allowedHosts: ReadonlySet<string>): { status: number; error: string; message: string } | null {
  if (headers.origin !== undefined) return { status: 403, error: "origin_forbidden", message: "This service does not take requests from browsers" };
  const name = (headers.host ?? "").trim().toLowerCase().replace(/:\d+$/, "");
  if (isIP(name.replace(/^\[|\]$/g, "")) !== 0 || allowedHosts.has(name)) return null;
  return { status: 421, error: "host_not_allowed", message: "This host name is not allowed. Add it to MEMORIA_WORKER_ALLOWED_HOSTS, or use an IP address" };
}
