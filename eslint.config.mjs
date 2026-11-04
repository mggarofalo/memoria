// Deliberately minimal: this config enforces cyclomatic complexity and nothing
// else. Types are tsc's job. It is not a style guide.
import tseslint from "typescript-eslint";

// Phase 1 ratchet: the highest value on main. The target is 10.
// See "Quality gates" in AGENTS.md.
const MAX_COMPLEXITY = 30;

export default [
  { ignores: ["**/node_modules/**", "**/dist/**", "**/.stryker-tmp/**", "**/reports/**"] },
  {
    files: ["sidecar/**/*.ts", "worker/**/*.ts"],
    languageOptions: { parser: tseslint.parser },
    linterOptions: { reportUnusedDisableDirectives: "error" },
    rules: { complexity: ["error", { max: MAX_COMPLEXITY }] },
  },
];
