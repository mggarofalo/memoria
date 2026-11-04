// Deliberately minimal: this config enforces cyclomatic complexity and nothing
// else. Types are tsc's job. It is not a style guide.
import parser from "@typescript-eslint/parser";

// Phase 1 ratchet: the highest value on main. The target is 10.
// See "Quality Gates" in AGENTS.md.
const MAX_COMPLEXITY = 30;

export default [
  { ignores: ["**/node_modules/**", "**/dist/**", "**/.stryker-tmp/**", "**/reports/**"] },
  {
    files: ["sidecar/**/*.ts", "worker/**/*.ts", "scripts/release/**/*.ts"],
    languageOptions: { parser },
    // Directives for rules this config does not enable are not "unused" to their authors.
    linterOptions: { reportUnusedDisableDirectives: "off" },
    rules: { complexity: ["error", { max: MAX_COMPLEXITY }] },
  },
];
