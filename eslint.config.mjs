// Deliberately small: cyclomatic complexity everywhere, and for the two services three
// type-aware rules about promises that tsc cannot catch. Types are otherwise tsc's job. It
// is not a style guide. See "Quality Gates" in AGENTS.md.
import parser from "@typescript-eslint/parser";
import plugin from "@typescript-eslint/eslint-plugin";

export default [
  { ignores: ["**/node_modules/**", "**/dist/**", "**/.stryker-tmp/**", "**/reports/**", "**/coverage/**"] },
  {
    files: ["sidecar/**/*.ts", "worker/**/*.ts", "scripts/release/**/*.ts", "scripts/e2e/**/*.ts", "scripts/*.mts", "deploy/mutation-runner/*.mts"],
    languageOptions: { parser },
    // Directives for rules this config does not enable are not "unused" to their authors.
    linterOptions: { reportUnusedDisableDirectives: "off" },
    rules: { complexity: ["error", { max: 10 }] },
  },
  {
    // A promise nobody awaits loses its rejection, which in a long-running service is an
    // unhandled rejection or a silent failure. These need types, so each file is read
    // through the tsconfig nearest it.
    files: ["sidecar/**/*.ts", "worker/**/*.ts"],
    languageOptions: { parser, parserOptions: { projectService: true, tsconfigRootDir: import.meta.dirname } },
    plugins: { "@typescript-eslint": plugin },
    rules: {
      // node:test's runner awaits its own tests, so the promise `test(...)` returns is not floating.
      "@typescript-eslint/no-floating-promises": ["error", { allowForKnownSafeCalls: [{ from: "package", package: "node:test", name: ["test", "describe", "it", "suite", "before", "after", "beforeEach", "afterEach"] }] }],
      "@typescript-eslint/no-misused-promises": "error",
      "@typescript-eslint/await-thenable": "error",
    },
  },
];
