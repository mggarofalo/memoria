package cmd

import (
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
)

const trufflehogFound = `{"files":1,"changed":0,"refused":0,"replacements":{},"filesByRule":{},"gitleaksRan":true,"gitleaksFindings":{},"trufflehogRan":true,"trufflehogUnscanned":0,"trufflehogFindings":{"trufflehog-jdbc":2},"trufflehogIgnored":1}`

const trufflehogClean = `{"files":1,"changed":0,"refused":0,"replacements":{},"filesByRule":{},"gitleaksRan":true,"gitleaksFindings":{},"trufflehogRan":true,"trufflehogUnscanned":0,"trufflehogFindings":{}}`

func TestMigrateReportFailsOnWhatTrufflehogFindsAndSaysWhatAZeroMeans(t *testing.T) {
	f := newMigrateFixture(t)
	withFakeGitleaks(t)
	f.fakeCheck("["+trufflehogFound+","+trufflehogClean+"]", nil)
	if code := f.h.run("migrate", "report", "--qmd-config", f.config, "--tool", f.tool); code != apperr.ExitGeneric {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	out := f.h.stdout.String()
	for _, want := range []string{"TRUFFLEHOG FINDINGS", "notes: trufflehog-jdbc: 2 left after redaction", "notes: 1 finding(s) listed in the ignore file", "imported faithfully", "independent of the engine's rules"} {
		if !strings.Contains(out, want) {
			t.Fatalf("stdout lacks %q: %q", want, out)
		}
	}
	if strings.Contains(strings.Join(f.h.checkArgs, " "), "--no-trufflehog") {
		t.Fatalf("args = %v", f.h.checkArgs)
	}
}

func TestMigrateReportCountsWhatTrufflehogCouldNotRead(t *testing.T) {
	f := newMigrateFixture(t)
	withFakeGitleaks(t)
	unread := strings.Replace(trufflehogClean, `"trufflehogUnscanned":0`, `"trufflehogUnscanned":3`, 1)
	f.fakeCheck("["+unread+","+trufflehogClean+"]", nil)
	if code := f.h.run("migrate", "report", "--qmd-config", f.config, "--tool", f.tool); code != apperr.ExitGeneric || !strings.Contains(f.h.stdout.String(), "notes: 3 document(s) trufflehog could not read") {
		t.Fatalf("code = %d, stdout = %q", code, f.h.stdout.String())
	}
}

func TestMigrateReportNeedsTrufflehogOrSaysItGoesWithout(t *testing.T) {
	f := newMigrateFixture(t)
	dir := t.TempDir()
	name := "gitleaks"
	if runtime.GOOS == "windows" {
		name += ".exe"
	}
	if err := os.WriteFile(filepath.Join(dir, name), []byte("#!/bin/sh\n"), 0o755); err != nil {
		t.Fatal(err)
	}
	t.Setenv("PATH", dir)
	f.fakeCheck("["+cleanCheck+","+cleanCheck+"]", nil)
	if code := f.h.run("migrate", "report", "--qmd-config", f.config, "--tool", f.tool); code != apperr.ExitUsage || !strings.Contains(f.h.stderr.String(), "trufflehog is not on PATH") || f.h.checkArgs != nil {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	f.h.stderr.Reset()
	if code := f.h.run("migrate", "report", "--qmd-config", f.config, "--tool", f.tool, "--no-trufflehog"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	if !strings.Contains(strings.Join(f.h.checkArgs, " "), "--no-trufflehog") || !strings.Contains(f.h.stderr.String(), "nothing independent looks at what is left") {
		t.Fatalf("args = %v, stderr = %q", f.h.checkArgs, f.h.stderr.String())
	}
}

func TestMigrateReportKeepsTheIgnoreFileWithOneScanner(t *testing.T) {
	f := newMigrateFixture(t)
	withFakeGitleaks(t)
	ignore := filepath.Join(t.TempDir(), "reviewed.gitleaksignore")
	writeFile(t, ignore, "notes/a.md:trufflehog-jdbc:3\n")
	f.fakeCheck("["+trufflehogClean+","+trufflehogClean+"]", nil)
	if code := f.h.run("migrate", "report", "--qmd-config", f.config, "--tool", f.tool, "--no-gitleaks", "--gitleaks-ignore", ignore); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	if !strings.Contains(strings.Join(f.h.checkArgs, " "), "--gitleaks-ignore "+ignore) {
		t.Fatalf("args = %v", f.h.checkArgs)
	}
}
