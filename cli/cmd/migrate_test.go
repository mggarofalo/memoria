package cmd

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/migrate"
)

// migrateFixture is a QMD config over two fixture collections, in a temporary folder.
type migrateFixture struct {
	h      *harness
	config string
	notes  string
	tool   string
}

func writeFile(t *testing.T, path, text string) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte(text), 0o600); err != nil {
		t.Fatal(err)
	}
}

func newMigrateFixture(t *testing.T) *migrateFixture {
	t.Helper()
	dir := t.TempDir()
	notes, sessions := filepath.Join(dir, "notes"), filepath.Join(dir, "sessions")
	writeFile(t, filepath.Join(notes, "a.md"), "12345")
	writeFile(t, filepath.Join(sessions, "b.md"), "123")
	writeFile(t, filepath.Join(sessions, "c.md"), "12")
	config := filepath.Join(dir, "index.yml")
	writeFile(t, config, fmt.Sprintf("collections:\n  notes:\n    path: %q\n  sessions:\n    path: %q\n", notes, sessions))
	tool := filepath.Join(dir, "RedactionCheck.dll")
	writeFile(t, tool, "")
	h := newHarness(t)
	h.ringErr = errors.New("the keyring must not be opened")
	return &migrateFixture{h: h, config: config, notes: notes, tool: tool}
}

func TestMigrateAloneShowsHelp(t *testing.T) {
	h := newHarness(t)
	if code := h.run("migrate"); code != 0 || !strings.Contains(h.stdout.String()+h.stderr.String(), "only read") {
		t.Fatalf("code = %d, stdout = %q", code, h.stdout.String())
	}
}

func TestMigratePlanCountsWithNoKeyOrServer(t *testing.T) {
	f := newMigrateFixture(t)
	if code := f.h.run("migrate", "plan", "--qmd-config", f.config); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	out := f.h.stdout.String()
	for _, want := range []string{"notes", "sessions", f.notes, "total: 3 files, 10 bytes"} {
		if !strings.Contains(out, want) {
			t.Fatalf("stdout lacks %q: %q", want, out)
		}
	}
}

func TestMigratePlanNeedsAnExplicitConfig(t *testing.T) {
	f := newMigrateFixture(t)
	if code := f.h.run("migrate", "plan"); code != apperr.ExitUsage || !strings.Contains(f.h.stderr.String(), "no default") {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	if code := f.h.run("migrate", "plan", "--qmd-config", filepath.Join(t.TempDir(), "absent.yml")); code != apperr.ExitUsage {
		t.Fatalf("a missing config: code = %d", code)
	}
}

func TestMigratePlanTargets(t *testing.T) {
	f := newMigrateFixture(t)
	if code := f.h.run("migrate", "plan", "--qmd-config", f.config, "--target", "notes=archive", "--format", "json"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	var result migratePlanResult
	if err := json.Unmarshal(f.h.stdout.Bytes(), &result); err != nil {
		t.Fatal(err)
	}
	if result.Collections[0].Target != "archive" || result.Collections[1].Target != "sessions" {
		t.Fatalf("collections = %+v", result.Collections)
	}
}

func TestMigratePlanRefusesBadTargets(t *testing.T) {
	f := newMigrateFixture(t)
	for name, target := range map[string][]string{
		"no equals":       {"--target", "notes"},
		"no local":        {"--target", "=archive"},
		"a bad target":    {"--target", "notes=a/b"},
		"an unknown one":  {"--target", "absent=archive"},
		"a shared target": {"--target", "notes=sessions"},
		"a bad codex":     {"--codex-target", "a/b"},
		"shared by codex": {"--codex-sessions", f.notes, "--codex-target", "notes"},
	} {
		args := append([]string{"migrate", "plan", "--qmd-config", f.config}, target...)
		if code := f.h.run(args...); code != apperr.ExitUsage {
			t.Errorf("%s: code = %d, stderr = %q", name, code, f.h.stderr.String())
		}
	}
}

func TestMigratePlanCountsRawCodexSessions(t *testing.T) {
	f := newMigrateFixture(t)
	codex := t.TempDir()
	writeFile(t, filepath.Join(codex, "2026", "one.jsonl"), "1234")
	if code := f.h.run("migrate", "plan", "--qmd-config", f.config, "--codex-sessions", codex); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	if !strings.Contains(f.h.stdout.String(), "raw Codex sessions: 1 files, 4 bytes, could go to codex-sessions") {
		t.Fatalf("stdout = %q", f.h.stdout.String())
	}
	if code := f.h.run("migrate", "plan", "--qmd-config", f.config, "--codex-sessions", filepath.Join(codex, "absent")); code != 0 || !strings.Contains(f.h.stdout.String(), "cannot be opened") {
		t.Fatalf("code = %d, stdout = %q", code, f.h.stdout.String())
	}
	if code := f.h.run("migrate", "plan", "--qmd-config", f.config, "--codex-sessions", codex, "--format", "markdown"); code != 0 || !strings.Contains(f.h.stdout.String(), "raw Codex sessions") {
		t.Fatalf("code = %d, stdout = %q", code, f.h.stdout.String())
	}
}

func TestMigratePlanShowsAProblemRatherThanAZero(t *testing.T) {
	f := newMigrateFixture(t)
	if err := os.RemoveAll(f.notes); err != nil {
		t.Fatal(err)
	}
	if code := f.h.run("migrate", "plan", "--qmd-config", f.config); code != 0 || !strings.Contains(f.h.stdout.String(), "cannot be opened") {
		t.Fatalf("code = %d, stdout = %q", code, f.h.stdout.String())
	}
	if code := f.h.run("migrate", "plan", "--qmd-config", f.config, "--format", "markdown"); code != 0 || !strings.Contains(f.h.stdout.String(), "cannot be opened") {
		t.Fatalf("code = %d, stdout = %q", code, f.h.stdout.String())
	}
}

const cleanCheck = `{"files":1,"changed":1,"refused":0,"replacements":{"github_token":3},"filesByRule":{"github_token":1},"gitleaksRan":false,"gitleaksFindings":{}}`

// fakeCheck stands in for the tool, recording the lists it was given.
func (f *migrateFixture) fakeCheck(output string, err error) *[][]string {
	seen := &[][]string{}
	f.h.checkOutput, f.h.checkErr, f.h.checkLists = output, err, seen
	return seen
}

func (f *migrateFixture) report(args ...string) int {
	return f.h.run(append([]string{"migrate", "report", "--qmd-config", f.config, "--tool", f.tool, "--no-gitleaks"}, args...)...)
}

func TestMigrateReportGivesTheToolExactlyThePlansFiles(t *testing.T) {
	f := newMigrateFixture(t)
	writeFile(t, filepath.Join(f.notes, "node_modules", "skip.md"), "excluded")
	seen := f.fakeCheck("["+cleanCheck+","+cleanCheck+"]", nil)
	if code := f.report(); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	lists := *seen
	if len(lists) != 2 || len(lists[0]) != 1 || filepath.Base(lists[0][0]) != "a.md" || len(lists[1]) != 2 {
		t.Fatalf("lists = %v", lists)
	}
	if !strings.Contains(strings.Join(f.h.checkArgs, " "), "--no-gitleaks") {
		t.Fatalf("args = %v", f.h.checkArgs)
	}
}

func TestMigrateReportHoldsCountsAndNoPath(t *testing.T) {
	f := newMigrateFixture(t)
	f.fakeCheck("["+cleanCheck+","+cleanCheck+"]", nil)
	if code := f.report(); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	out := f.h.stdout.String()
	if !strings.Contains(out, "notes: github_token: 3 replacements in 1 files") || !strings.Contains(out, "not run") {
		t.Fatalf("stdout = %q", out)
	}
	if strings.Contains(out, f.notes) || strings.Contains(out, "a.md") {
		t.Fatalf("a path reached the report: %q", out)
	}
}

func TestMigrateReportRemovesItsLists(t *testing.T) {
	f := newMigrateFixture(t)
	f.fakeCheck("["+cleanCheck+","+cleanCheck+"]", nil)
	if code := f.report(); code != 0 {
		t.Fatalf("code = %d", code)
	}
	for _, list := range f.h.checkFiles {
		if _, err := os.Stat(list); !errors.Is(err, os.ErrNotExist) {
			t.Fatalf("a list of paths was left at %s: %v", list, err)
		}
	}
	if len(f.h.checkFiles) != 2 {
		t.Fatalf("lists = %v", f.h.checkFiles)
	}
}

func TestMigrateReportExitsOneWhenNotClean(t *testing.T) {
	refused := `{"files":1,"changed":0,"refused":1,"replacements":{},"filesByRule":{},"gitleaksRan":true,"gitleaksFindings":{}}`
	found := `{"files":1,"changed":0,"refused":0,"replacements":{},"filesByRule":{},"gitleaksRan":true,"gitleaksFindings":{"generic":2}}`
	for name, check := range map[string]string{"refused": refused, "found": found} {
		f := newMigrateFixture(t)
		f.fakeCheck("["+check+","+cleanCheck+"]", nil)
		if code := f.report(); code != apperr.ExitGeneric {
			t.Errorf("%s: code = %d", name, code)
		}
		if name == "found" && !strings.Contains(f.h.stdout.String(), "gitleaks generic: 2 left") {
			t.Errorf("stdout = %q", f.h.stdout.String())
		}
	}
}

func TestMigrateReportMarkdownAndJSON(t *testing.T) {
	found := `{"files":1,"changed":1,"refused":0,"replacements":{"github_token":3},"filesByRule":{"github_token":1},"gitleaksRan":true,"gitleaksFindings":{"generic":2}}`
	f := newMigrateFixture(t)
	f.fakeCheck("["+found+","+cleanCheck+"]", nil)
	if code := f.report("--format", "markdown"); code != apperr.ExitGeneric {
		t.Fatalf("code = %d", code)
	}
	out := f.h.stdout.String()
	if !strings.Contains(out, "3 replacements in 1 files") || !strings.Contains(out, "2 left after redaction") {
		t.Fatalf("stdout = %q", out)
	}
	if code := f.report("--format", "json"); code != apperr.ExitGeneric {
		t.Fatalf("code = %d", code)
	}
	var reports []migrate.Report
	if err := json.Unmarshal(f.h.stdout.Bytes(), &reports); err != nil {
		t.Fatal(err)
	}
	if reports[0].Name != "notes" || reports[1].Name != "sessions" || reports[0].Replacements["github_token"] != 3 {
		t.Fatalf("reports = %+v", reports)
	}
}

func TestMigrateReportRefusesBadInput(t *testing.T) {
	f := newMigrateFixture(t)
	f.fakeCheck("[]", nil)
	dir := t.TempDir()
	t.Setenv("PATH", dir)
	for name, args := range map[string][]string{
		"no tool":     {"migrate", "report", "--qmd-config", f.config},
		"a bad tool":  {"migrate", "report", "--qmd-config", f.config, "--tool", filepath.Join(dir, "absent.dll"), "--no-gitleaks"},
		"a folder":    {"migrate", "report", "--qmd-config", f.config, "--tool", dir, "--no-gitleaks"},
		"no config":   {"migrate", "report", "--tool", f.tool, "--no-gitleaks"},
		"no gitleaks": {"migrate", "report", "--qmd-config", f.config, "--tool", f.tool},
	} {
		if code := f.h.run(args...); code != apperr.ExitUsage {
			t.Errorf("%s: code = %d, stderr = %q", name, code, f.h.stderr.String())
		}
	}
	if len(f.h.checkArgs) != 0 {
		t.Fatalf("the tool ran: %v", f.h.checkArgs)
	}
}

func TestMigrateReportWillNotPassOverACollectionItCannotCount(t *testing.T) {
	f := newMigrateFixture(t)
	f.fakeCheck("[]", nil)
	if err := os.RemoveAll(f.notes); err != nil {
		t.Fatal(err)
	}
	if code := f.report(); code != apperr.ExitUsage || len(f.h.checkArgs) != 0 {
		t.Fatalf("code = %d, args = %v", code, f.h.checkArgs)
	}
}

func TestMigrateReportWhenTheToolFails(t *testing.T) {
	f := newMigrateFixture(t)
	f.fakeCheck("", apperr.New(apperr.KindGeneric, "the redaction check did not finish"))
	if code := f.report(); code != apperr.ExitGeneric || !strings.Contains(f.h.stderr.String(), "did not finish") {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	for name, output := range map[string]string{"not json": "garbage", "too few": "[" + cleanCheck + "]"} {
		f.fakeCheck(output, nil)
		if code := f.report(); code != apperr.ExitGeneric {
			t.Errorf("%s: code = %d", name, code)
		}
	}
}

func TestRunDotnetClassifiesExits(t *testing.T) {
	if _, err := exec.LookPath("dotnet"); err != nil {
		t.Skip("no dotnet here")
	}
	if _, err := runDotnet(context.Background(), []string{"--version"}); err != nil {
		t.Fatalf("err = %v", err)
	}
	// dotnet exits 1 for a missing file, the same as the tool's "not clean". No result comes with it, so it is still refused.
	stdout, err := runDotnet(context.Background(), []string{filepath.Join(t.TempDir(), "absent.dll")})
	if err != nil {
		t.Fatalf("err = %v", err)
	}
	if _, err := migrate.ParseCheck(stdout, 1); err == nil {
		t.Fatal("a tool that could not run was read as a result")
	}
}

func TestRunDotnetWhenThereIsNoDotnet(t *testing.T) {
	t.Setenv("PATH", t.TempDir())
	if _, err := runDotnet(context.Background(), []string{"--version"}); err == nil {
		t.Fatal("no error with no dotnet")
	}
}

func TestRunDotnetTreatsExitOneAsAResult(t *testing.T) {
	if os.Getenv("MEMORIA_EXIT_ONE") == "1" {
		os.Exit(1)
	}
	if !exitOneIsAResult(&exec.ExitError{ProcessState: exitOneState(t)}) {
		t.Fatal("an exit of 1 should keep the counts")
	}
	if exitOneIsAResult(errors.New("did not start")) {
		t.Fatal("a failure to start is not a result")
	}
}

func exitOneState(t *testing.T) *os.ProcessState {
	t.Helper()
	command := exec.Command(os.Args[0], "-test.run", "TestRunDotnetTreatsExitOneAsAResult")
	command.Env = append(os.Environ(), "MEMORIA_EXIT_ONE=1")
	err := command.Run()
	var exit *exec.ExitError
	if !errors.As(err, &exit) {
		t.Fatalf("err = %v", err)
	}
	return exit.ProcessState
}
