//go:build unix

package selfupdate

import (
	"errors"
	"os"
	"path/filepath"
	"testing"
)

func TestOwnerCheck(t *testing.T) {
	exe := installedBinary(t)
	if err := checkOwnedByCurrentUser(exe); err != nil {
		t.Fatalf("own file: %v", err)
	}
	if err := checkOwnedByCurrentUser(filepath.Join(t.TempDir(), "missing")); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("missing file: %v", err)
	}
	// Root owns /, so this is another user's file for anyone else, and only then
	// does it say something.
	if os.Geteuid() != 0 {
		if err := checkOwnedByCurrentUser("/"); err == nil {
			t.Fatal("expected another owner to be refused")
		}
	}
}
