package cmd

import (
	"bytes"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/auth"
)

// TestExecuteUsesProcessArguments runs the entry point itself, with stdout
// redirected to a file so the version line stays out of test output.
func TestExecuteUsesProcessArguments(t *testing.T) {
	out, err := os.Create(filepath.Join(t.TempDir(), "stdout"))
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = out.Close() }()

	previousArgs, previousStdout := os.Args, os.Stdout
	os.Args, os.Stdout = []string{"memoria", "version"}, out
	defer func() { os.Args, os.Stdout = previousArgs, previousStdout }()

	if code := Execute(); code != apperr.ExitOK {
		t.Fatalf("code = %d", code)
	}
	data, err := os.ReadFile(out.Name())
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(data), "memoria "+Version) {
		t.Fatalf("stdout = %q", data)
	}
}

func TestStandaloneRootReportsTheStampedVersion(t *testing.T) {
	withVersion(t, "9.8.7")
	root := newRootCmd()
	var out bytes.Buffer
	root.SetOut(&out)
	root.SetArgs([]string{"--version"})
	if err := root.Execute(); err != nil {
		t.Fatal(err)
	}
	if got := strings.TrimSpace(out.String()); got != "memoria version 9.8.7" {
		t.Fatalf("--version printed %q", got)
	}
}

func TestDefaultKeyringFactoryOnlyOpens(t *testing.T) {
	h := newHarness(t)
	a := newApp(h.stdin, &h.stdout, &h.stderr)
	// Either answer is fine here; nothing is read or written.
	if store, err := a.openKeyring(); err == nil && store == nil {
		t.Fatal("expected a store")
	}
}

func TestCommandsFailWithoutAHomeDirectory(t *testing.T) {
	h := newHarness(t)
	t.Setenv("XDG_CONFIG_HOME", "")
	t.Setenv("HOME", "")
	t.Setenv("USERPROFILE", "")
	h.env[auth.EnvAPIKey] = testKey
	if code := h.run("status", "--api-url", "https://memoria.example.com"); code != apperr.ExitGeneric {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
}

func TestLogoutWithoutAHomeDirectory(t *testing.T) {
	h := newHarness(t)
	t.Setenv("XDG_CONFIG_HOME", "")
	t.Setenv("HOME", "")
	t.Setenv("USERPROFILE", "")
	if code := h.run("auth", "logout"); code != apperr.ExitGeneric {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
}
