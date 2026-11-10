package cmd

import (
	"encoding/json"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/migrate"
)

// withFakeGitleaks puts an empty gitleaks on PATH, so the report's check for it passes. The tool is faked, so it never runs.
func withFakeGitleaks(t *testing.T) {
	t.Helper()
	dir := t.TempDir()
	name := "gitleaks"
	if runtime.GOOS == "windows" {
		name += ".exe"
	}
	if err := os.WriteFile(filepath.Join(dir, name), []byte("#!/bin/sh\n"), 0o755); err != nil {
		t.Fatal(err)
	}
	t.Setenv("PATH", dir)
}

const ignoredCheck = `{"files":1,"changed":0,"refused":0,"replacements":{},"filesByRule":{},"gitleaksRan":true,"gitleaksFindings":{},"gitleaksIgnored":2}`

func TestMigrateReportPassesTheIgnoreAndFindingsFilesToTheTool(t *testing.T) {
	f := newMigrateFixture(t)
	withFakeGitleaks(t)
	ignore := filepath.Join(t.TempDir(), "reviewed.gitleaksignore")
	writeFile(t, ignore, "# reviewed\nnotes/a.md:generic-api-key:3\n")
	findings := filepath.Join(t.TempDir(), "findings.txt")
	f.fakeCheck("["+ignoredCheck+","+ignoredCheck+"]", nil)
	code := f.h.run("migrate", "report", "--qmd-config", f.config, "--tool", f.tool, "--gitleaks-ignore", ignore, "--gitleaks-findings", findings)
	if code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	args := strings.Join(f.h.checkArgs, " ")
	if !strings.Contains(args, "--gitleaks-ignore "+ignore) || !strings.Contains(args, "--gitleaks-findings "+findings) || strings.Contains(args, "--no-gitleaks") {
		t.Fatalf("args = %v", f.h.checkArgs)
	}
	if out := f.h.stdout.String(); !strings.Contains(out, "notes: 2 gitleaks finding(s) listed in the ignore file") {
		t.Fatalf("stdout = %q", out)
	}
}

func TestMigrateReportStillFailsOnAFindingTheIgnoreFileDoesNotList(t *testing.T) {
	f := newMigrateFixture(t)
	withFakeGitleaks(t)
	ignore := filepath.Join(t.TempDir(), "reviewed.gitleaksignore")
	writeFile(t, ignore, "notes/a.md:generic-api-key:3\n")
	left := `{"files":1,"changed":0,"refused":0,"replacements":{},"filesByRule":{},"gitleaksRan":true,"gitleaksFindings":{"generic-api-key":1},"gitleaksIgnored":1}`
	f.fakeCheck("["+left+","+ignoredCheck+"]", nil)
	if code := f.h.run("migrate", "report", "--qmd-config", f.config, "--tool", f.tool, "--gitleaks-ignore", ignore); code != 1 {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
}

func TestMigrateReportRefusesAnIgnoreFileItCannotUse(t *testing.T) {
	f := newMigrateFixture(t)
	withFakeGitleaks(t)
	ignore := filepath.Join(t.TempDir(), "reviewed.gitleaksignore")
	writeFile(t, ignore, "")
	cases := map[string][]string{
		"missing":               {"--gitleaks-ignore", filepath.Join(t.TempDir(), "absent")},
		"a folder":              {"--gitleaks-ignore", t.TempDir()},
		"ignore, no gitleaks":   {"--gitleaks-ignore", ignore, "--no-gitleaks"},
		"findings, no gitleaks": {"--gitleaks-findings", filepath.Join(t.TempDir(), "out"), "--no-gitleaks"},
	}
	for name, extra := range cases {
		f.fakeCheck("["+cleanCheck+","+cleanCheck+"]", nil)
		f.h.checkArgs = nil
		args := append([]string{"migrate", "report", "--qmd-config", f.config, "--tool", f.tool}, extra...)
		if code := f.h.run(args...); code != apperr.ExitUsage || f.h.checkArgs != nil {
			t.Fatalf("%s: code = %d, tool ran = %v, stderr = %q", name, code, f.h.checkArgs != nil, f.h.stderr.String())
		}
	}
}

func TestMigrateReportNamesEachListByItsCollection(t *testing.T) {
	f := newMigrateFixture(t)
	var lists []migrate.CheckList
	f.fakeCheck("["+cleanCheck+","+cleanCheck+"]", nil)
	f.h.checkStdin = func(stdin []byte) {
		if err := json.Unmarshal(stdin, &lists); err != nil {
			t.Fatal(err)
		}
	}
	if code := f.report(); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	if len(lists) != 2 || lists[0].Name != "notes" || lists[1].Name != "sessions" {
		t.Fatalf("lists = %+v", lists)
	}
}
