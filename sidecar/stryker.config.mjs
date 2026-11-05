// A pull request only reports; the nightly holds the score. See scripts/mutation-js.sh.
// The sidecar's nightly score is not known yet, so it has no break score for now.
const nightlyBreak = null;

export default {
  testRunner: "tap",
  tap: {
    testFiles: ["sidecar/test/**/*.test.ts"],
    nodeArgs: ["--max-old-space-size=512", "--test-reporter=tap", "-r", "{{hookFile}}", "{{testFile}}"],
  },
  coverageAnalysis: "perTest",
  mutate: ["sidecar/src/**/*.ts", "!sidecar/src/secret-file.ts"],
  ignorePatterns: ["/cli", "/src", "/tests/Memoria.Api.Tests", "/docs", "/deploy", "/.claude", "/sidecar/test/install-scripts.test.ts", "**/bin", "**/obj"],
  reporters: ["clear-text", "html", "json"],
  htmlReporter: { fileName: "sidecar/reports/mutation/mutation.html" },
  jsonReporter: { fileName: "sidecar/reports/mutation/mutation.json" },
  thresholds: { high: 80, low: 60, break: process.env.MUTATION_BASE ? null : nightlyBreak },
};
