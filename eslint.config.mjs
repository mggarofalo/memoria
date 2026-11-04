// Deliberately minimal: this config enforces cyclomatic complexity and nothing
// else. Types are tsc's job. It is not a style guide.
import parser from "@typescript-eslint/parser";

const MAX_COMPLEXITY = 10;

// Functions still over the limit, by file, at their current highest. Each is in
// code an open change is rewriting. Remove an entry when its file reaches 10;
// never raise one. See "Quality Gates" in AGENTS.md.
const NOT_YET_AT_LIMIT = {
  "sidecar/bench/parity.ts": 15,
  "sidecar/src/config.ts": 18,
  "sidecar/src/llm/client.ts": 11,
  "sidecar/src/llm/validate.ts": 13,
  "sidecar/src/multi-get.ts": 11,
  "sidecar/src/routes.ts": 30,
  "sidecar/src/store.ts": 18,
  "sidecar/test/fake-backend.ts": 13,
  "worker/src/server.ts": 19,
};

export default [
  { ignores: ["**/node_modules/**", "**/dist/**", "**/.stryker-tmp/**", "**/reports/**", "**/coverage/**"] },
  {
    files: ["sidecar/**/*.ts", "worker/**/*.ts", "scripts/release/**/*.ts"],
    languageOptions: { parser },
    // Directives for rules this config does not enable are not "unused" to their authors.
    linterOptions: { reportUnusedDisableDirectives: "off" },
    rules: { complexity: ["error", { max: MAX_COMPLEXITY }] },
  },
  ...Object.entries(NOT_YET_AT_LIMIT).map(([file, max]) => ({
    files: [file],
    rules: { complexity: ["error", { max }] },
  })),
];
