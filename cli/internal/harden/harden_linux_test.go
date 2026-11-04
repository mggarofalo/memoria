//go:build linux

package harden

import (
	"os"
	"testing"

	"golang.org/x/sys/unix"
)

func TestProcessIsNotDumpable(t *testing.T) {
	if err := Process(); err != nil {
		t.Fatal(err)
	}
	dumpable, err := unix.PrctlRetInt(unix.PR_GET_DUMPABLE, 0, 0, 0, 0)
	if err != nil {
		t.Fatal(err)
	}
	if dumpable != 0 {
		t.Fatalf("PR_GET_DUMPABLE = %d", dumpable)
	}
	// Non-dumpable makes /proc/self root-owned; self-update still has to find itself.
	if _, err := os.Executable(); err != nil {
		t.Fatalf("os.Executable: %v", err)
	}
}
