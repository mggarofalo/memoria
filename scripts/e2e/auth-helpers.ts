/** What the auth checks share: issuing keys over REST, reading audit rows, and tracing a request. */
import { randomBytes } from "node:crypto";
import { pause, type ApiResponse, type Harness } from "./harness.ts";

export const BAD_KEY = `mem_${"0".repeat(16)}_${"0".repeat(64)}`;
export const COLLECTIONS = "/v1/collections";
export const ADMIN_KEYS = "/v1/admin/keys";

/** Peers on loopback. Each has its own failed-auth budget, and only the trusted one is believed. */
export const PEER = {
  untrusted: "127.0.0.2",
  limits: "127.0.0.3",
  concurrent: "127.0.0.4",
  known: "127.0.0.5",
  fresh: "127.0.0.6",
  malformed: "127.0.0.7",
  audit: "127.0.0.8",
} as const;

export interface IssuedKey {
  token: string;
  id: string;
}

export interface AuditRow {
  audit_id: string;
  key_id: string;
  operation: string;
  collections: string | null;
  attempted_collections: string | null;
  result_count: number | null;
  status: number;
  trace_id: string;
  target: string | null;
  query: string | null;
}

export function idOf(token: string): string {
  return token.split("_")[1] ?? "";
}

export async function issueKey(h: Harness, admin: string, body: object): Promise<IssuedKey> {
  const response = await h.request({ method: "POST", path: ADMIN_KEYS, key: admin, body, issuing: true });
  if (response.status !== 201) throw new Error(`could not issue a key: HTTP ${response.status} ${response.body}`);
  const token = h.remember(parse<{ key: string }>(response).key);
  return { token, id: idOf(token) };
}

export function readerOf(label: string): object {
  return { label, scopes: ["read"], collections: ["notes"] };
}

export function parse<T>(response: ApiResponse): T {
  return JSON.parse(response.body) as T;
}

/** A problem body's stable code, or nothing when the body is not one. */
export function codeOf(response: ApiResponse): string | undefined {
  try {
    return parse<{ code?: string }>(response).code;
  } catch {
    return undefined;
  }
}

export function hasRetryAfter(response: ApiResponse): boolean {
  return /^[1-9]\d*$/.test(String(response.headers["retry-after"]));
}

/** A request's trace id is the client's own to choose, which makes it a marker for finding the row. */
export function newTrace(): { traceId: string; headers: Record<string, string> } {
  const traceId = randomBytes(16).toString("hex");
  return { traceId, headers: { traceparent: `00-${traceId}-${randomBytes(8).toString("hex")}-01` } };
}

/** Audit rows are written as the response ends, so a check waits for the rows it expects, then gives up and looks. */
async function waitFor<T>(read: () => T[], enough: (rows: T[]) => boolean): Promise<T[]> {
  for (let attempt = 0; attempt < 50; attempt++) {
    const rows = read();
    if (enough(rows)) return rows;
    await pause(100);
  }
  return read();
}

export function auditByTrace(h: Harness, traceId: string, count = 1): Promise<AuditRow[]> {
  return waitFor(() => h.control<AuditRow>("SELECT * FROM audit_log WHERE trace_id = ? ORDER BY id", traceId), (rows) => rows.length >= count);
}

export function auditByKey(h: Harness, keyId: string, enough: (rows: AuditRow[]) => boolean): Promise<AuditRow[]> {
  return waitFor(() => h.control<AuditRow>("SELECT * FROM audit_log WHERE key_id = ? ORDER BY id", keyId), enough);
}

/** Log lines are written by a background queue, so this also waits. */
export async function logShows(read: () => string, text: string): Promise<boolean> {
  const seen = await waitFor(() => (read().includes(text) ? [text] : []), (found) => found.length > 0);
  return seen.length > 0;
}

export function statuses(responses: ApiResponse[]): number[] {
  return responses.map((response) => response.status);
}

export function count(values: number[], wanted: number): number {
  return values.filter((value) => value === wanted).length;
}

export async function repeat(times: number, send: (index: number) => Promise<ApiResponse>): Promise<ApiResponse[]> {
  const answers: ApiResponse[] = [];
  for (let index = 0; index < times; index++) answers.push(await send(index));
  return answers;
}
