//go:build unix

package harden

import (
	"syscall"
	"testing"
)

func TestCoreDumpsAreOff(t *testing.T) {
	if err := Process(); err != nil {
		t.Fatal(err)
	}
	var limit syscall.Rlimit
	if err := syscall.Getrlimit(syscall.RLIMIT_CORE, &limit); err != nil {
		t.Fatal(err)
	}
	if limit.Cur != 0 || limit.Max != 0 {
		t.Fatalf("RLIMIT_CORE = %+v", limit)
	}
}
