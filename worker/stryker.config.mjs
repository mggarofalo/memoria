// A pull request only reports; the nightly holds the score. See scripts/mutation-js.sh.
const nightlyBreak = 70;

export default {
  testRunner: "tap",
  tap: {
    testFiles: ["worker/test/**/*.test.ts"],
    nodeArgs: ["--max-old-space-size=512", "--test-reporter=tap", "-r", "{{hookFile}}", "{{testFile}}"],
  },
  coverageAnalysis: "perTest",
  concurrency: 2,
  mutate: ["worker/src/**/*.ts"],
  ignorePatterns: ["/cli", "/src", "/tests", "/sidecar", "/docs", "/deploy", "/.claude", "**/bin", "**/obj"],
  reporters: ["clear-text", "html", "json"],
  htmlReporter: { fileName: "worker/reports/mutation/mutation.html" },
  jsonReporter: { fileName: "worker/reports/mutation/mutation.json" },
  thresholds: { high: 80, low: 70, break: process.env.MUTATION_BASE ? null : nightlyBreak },
};
