/**
 * Every frontmatter filter, through each way in: REST, MCP and the CLI, to a real sidecar and index. A fake sidecar accepts whatever it is sent, so only this
 * shows the request is one the sidecar will take.
 */
import type { CliResult, Harness } from "./harness.ts";
import { forceIndex, mcpSearch, SAFE, type FilterArguments } from "./ingest.ts";

const WORD = "filterword";
const IN_PROJECT = "filterproject";
const OTHER_PROJECT = "otherproject";
const TAG = "filtertag";
const BRANCH = "filterbranch";

function json(key: string): Record<string, string> {
  return { authorization: `Bearer ${key}`, "content-type": "application/json" };
}

async function seed(h: Harness, writer: string): Promise<void> {
  const post = (sourceId: string, project: string, updated: string, tags: string[]): Promise<Response> =>
    fetch(`${h.apiUrl}/v1/documents`, { method: "POST", headers: json(writer), body: JSON.stringify({ collection: SAFE, source: "filters", sourceId, updated, project, tags, body: `${WORD} ${sourceId}` }) });
  await post("wanted", IN_PROJECT, "2026-03-10T00:00:00Z", [TAG]);
  await post("unwanted", OTHER_PROJECT, "2026-01-10T00:00:00Z", []);
  const line = JSON.stringify({ type: "user", timestamp: "2026-03-10T10:00:00.000Z", cwd: "/srv/demo", gitBranch: BRANCH, message: { role: "user", content: `${WORD} on a branch` } }) + "\n";
  await fetch(`${h.apiUrl}/v1/ingest/session?${new URLSearchParams({ collection: SAFE, format: "claude", sessionId: "fafafafa-1111-2222-3333-444444444444", updated: "2026-03-10T11:00:00Z" })}`, { method: "POST", headers: { ...json(writer), "content-type": "application/x-ndjson" }, body: line });
  await forceIndex({ h });
}

const CASES: { name: string; rest: string; mcp: FilterArguments; cli: string[]; expect: (text: string) => boolean }[] = [
  { name: "project", rest: `project=${IN_PROJECT}`, mcp: { project: [IN_PROJECT] }, cli: ["--project", IN_PROJECT], expect: (text) => text.includes("wanted") && !text.includes("unwanted") },
  { name: "branch", rest: `branch=${BRANCH}`, mcp: { branch: [BRANCH] }, cli: ["--branch", BRANCH], expect: (text) => text.includes("on a branch") && !text.includes("unwanted") },
  { name: "since", rest: "since=2026-02-01", mcp: { since: "2026-02-01" }, cli: ["--since", "2026-02-01"], expect: (text) => text.includes("wanted") && !text.includes("unwanted") },
  { name: "until", rest: "until=2026-02-01", mcp: { until: "2026-02-01" }, cli: ["--until", "2026-02-01"], expect: (text) => text.includes("unwanted") && !text.includes("on a branch") },
  { name: "tag", rest: `tag=${TAG}`, mcp: { tags: [TAG] }, cli: ["--tag", TAG], expect: (text) => text.includes("wanted") && !text.includes("unwanted") },
];

function brief(answer: string): string {
  return answer.slice(0, 300);
}

/** Runs against a server started with INGEST_SERVER. */
export async function filters(h: Harness, writer: string, reader: string): Promise<void> {
  await seed(h, writer);
  for (const filter of CASES) {
    const rest = await fetch(`${h.apiUrl}/v1/search?query=${WORD}&rerank=false&limit=20&collection=${SAFE}&${filter.rest}`, { headers: json(reader) });
    const restText = await rest.text();
    h.check(`filters: ${filter.name} through REST`, rest.status === 200 && filter.expect(restText), brief(restText));

    const mcpText = await mcpSearch(h, reader, { query: WORD, collections: [SAFE], rerank: false, ...filter.mcp });
    h.check(`filters: ${filter.name} through MCP`, filter.expect(mcpText) && !mcpText.includes('"isError":true'), brief(mcpText));

    const cli: CliResult = h.memoria(["query", WORD, "--collection", SAFE, "--no-rerank", "--format", "json", ...filter.cli], { key: reader });
    h.check(`filters: ${filter.name} through the CLI`, cli.code === 0 && filter.expect(cli.out), brief(cli.out + cli.err));
  }

  // The other optional settings have the same path to the same strict schema.
  for (const extra of ["intent=the%20reason%20for%20asking", "minScore=0.01", "candidateLimit=5", "limit=1"]) {
    const answer = await fetch(`${h.apiUrl}/v1/search?query=${WORD}&rerank=false&collection=${SAFE}&${extra}`, { headers: json(reader) });
    const body = await answer.text();
    h.check(`search: ${extra.split("=")[0]} reaches the sidecar and is accepted`, answer.status === 200 && body.includes(WORD), brief(body));
  }

  const everything = await fetch(`${h.apiUrl}/v1/search?query=${WORD}&rerank=false&limit=20&collection=${SAFE}&project=${IN_PROJECT}&since=2026-02-01&until=2026-04-01&tag=${TAG}`, { headers: json(reader) });
  const text = await everything.text();
  h.check("filters: all of them together", everything.status === 200 && text.includes("wanted") && !text.includes("unwanted"), brief(text));
}
