// Deliberately minimal: this config enforces cyclomatic complexity and nothing
// else. Types are tsc's job. It is not a style guide. See "Quality Gates" in AGENTS.md.
import parser from "@typescript-eslint/parser";

export default [
  { ignores: ["**/node_modules/**", "**/dist/**", "**/.stryker-tmp/**", "**/reports/**", "**/coverage/**"] },
  {
    files: ["sidecar/**/*.ts", "worker/**/*.ts", "scripts/release/**/*.ts", "scripts/e2e/**/*.ts", "scripts/*.mts", "deploy/mutation-runner/*.mts"],
    languageOptions: { parser },
    // Directives for rules this config does not enable are not "unused" to their authors.
    linterOptions: { reportUnusedDisableDirectives: "off" },
    rules: { complexity: ["error", { max: 10 }] },
  },
];
