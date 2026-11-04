package cmd

import (
	"bytes"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

const stampedVersion = "9.8.7"

func TestVersionFlagReportsVersionVariable(t *testing.T) {
	original := Version
	t.Cleanup(func() { Version = original })
	Version = stampedVersion

	root := newRootCmd()
	var out bytes.Buffer
	root.SetOut(&out)
	root.SetArgs([]string{"--version"})
	if err := root.Execute(); err != nil {
		t.Fatalf("--version: %v", err)
	}
	if got, want := strings.TrimSpace(out.String()), "memoria version "+stampedVersion; got != want {
		t.Fatalf("--version printed %q, want %q", got, want)
	}
}

// The release build's -X path must name a variable that exists: the linker
// silently ignores one that does not, and the binary would report "dev".
func TestLdflagsStampReachesTheBinary(t *testing.T) {
	if testing.Short() {
		t.Skip("builds the CLI")
	}
	binary := filepath.Join(t.TempDir(), "memoria")
	if runtime.GOOS == "windows" {
		binary += ".exe"
	}
	build := exec.Command("go", "build", "-trimpath", "-buildvcs=false",
		"-ldflags", "-X github.com/mggarofalo/memoria/cli/cmd.Version="+stampedVersion,
		"-o", binary, "..")
	if out, err := build.CombinedOutput(); err != nil {
		t.Fatalf("go build: %v\n%s", err, out)
	}
	out, err := exec.Command(binary, "--version").Output()
	if err != nil {
		t.Fatalf("run --version: %v", err)
	}
	if got, want := strings.TrimSpace(string(out)), "memoria version "+stampedVersion; got != want {
		t.Fatalf("binary printed %q, want %q", got, want)
	}
}
