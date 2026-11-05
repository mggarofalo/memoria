/**
 * The skill names MCP tools and their parameters. A real server says what it has, so a tool
 * renamed there fails here, in the skill's own words. The CLI's half is in cli/cmd/skill_test.go.
 */
import { readFileSync } from "node:fs";
import path from "node:path";
import { INITIALIZE, mcpFrames } from "./checks.ts";
import { repo, type Harness } from "./harness.ts";

const SIGNATURE = /^([a-z_]+)\(([^)]*)\)$/gm;

interface ListedTool {
  name: string;
  inputSchema?: { properties?: Record<string, unknown> };
}

function signatures(skill: string): Map<string, string[]> {
  const found = new Map<string, string[]>();
  for (const [, name, parameters] of skill.matchAll(SIGNATURE)) {
    if (name) found.set(name, (parameters ?? "").split(",").map((parameter) => parameter.trim()).filter(Boolean));
  }
  return found;
}

function listTools(h: Harness, key: string): ListedTool[] {
  const frames = [INITIALIZE, { jsonrpc: "2.0", method: "notifications/initialized" }, { jsonrpc: "2.0", id: 2, method: "tools/list" }];
  const session = h.memoria(["mcp"], { key, stdin: frames.map((frame) => JSON.stringify(frame)).join("\n") + "\n" });
  return (mcpFrames(session.out).replies.get(2)?.result?.tools ?? []) as ListedTool[];
}

/** Sorted, so a difference reads clearly in the failure. */
function sameNames(left: string[], right: string[]): boolean {
  return [...left].sort().join(",") === [...right].sort().join(",");
}

export function skillMatchesTheServer(h: Harness, reader: string): void {
  const skill = readFileSync(path.join(repo, "skills", "memoria", "SKILL.md"), "utf8").replaceAll("\r\n", "\n");
  const wanted = signatures(skill);
  const tools = listTools(h, reader);
  h.check("skill: names exactly the MCP tools a real server has", tools.length > 0 && sameNames([...wanted.keys()], tools.map((tool) => tool.name)), { skill: [...wanted.keys()], server: tools.map((tool) => tool.name) });
  for (const tool of tools) {
    const server = Object.keys(tool.inputSchema?.properties ?? {});
    h.check(`skill: ${tool.name} lists exactly the parameters a real server takes`, sameNames(wanted.get(tool.name) ?? [], server), { skill: wanted.get(tool.name), server });
  }
}
