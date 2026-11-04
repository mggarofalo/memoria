//go:build !windows

package auth

import (
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestFileAndDirectoryModes(t *testing.T) {
	dir := useTempConfig(t)
	// A pre-existing loose directory gets tightened.
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatal(err)
	}
	store, err := NewFileStore()
	if err != nil {
		t.Fatal(err)
	}
	if err := store.Set("a", fileKey); err != nil {
		t.Fatal(err)
	}
	info, err := os.Stat(store.Path())
	if err != nil {
		t.Fatal(err)
	}
	if info.Mode().Perm() != 0o600 {
		t.Errorf("file mode = %o", info.Mode().Perm())
	}
	info, err = os.Stat(dir)
	if err != nil {
		t.Fatal(err)
	}
	if info.Mode().Perm() != 0o700 {
		t.Errorf("dir mode = %o", info.Mode().Perm())
	}
}

func TestLooseFileIsRefusedButCanBeDeleted(t *testing.T) {
	useTempConfig(t)
	store, err := NewFileStore()
	if err != nil {
		t.Fatal(err)
	}
	if err := store.Set("a", fileKey); err != nil {
		t.Fatal(err)
	}
	if err := os.Chmod(store.Path(), 0o644); err != nil {
		t.Fatal(err)
	}
	_, err = store.Get("a")
	if !errors.Is(err, ErrInsecurePermissions) {
		t.Fatalf("err = %v", err)
	}
	if strings.Contains(err.Error(), fileKey) || !strings.Contains(err.Error(), "chmod 600") {
		t.Fatalf("err = %v", err)
	}
	if err := store.Set("b", flagKey); !errors.Is(err, ErrInsecurePermissions) {
		t.Fatalf("writing to a loose file should fail: %v", err)
	}
	if err := store.Delete("a"); err != nil {
		t.Fatalf("logout must still work: %v", err)
	}
}

func TestSymlinkedCredentialsAreRefused(t *testing.T) {
	dir := useTempConfig(t)
	if err := os.MkdirAll(dir, 0o700); err != nil {
		t.Fatal(err)
	}
	target := filepath.Join(t.TempDir(), "elsewhere.json")
	if err := os.WriteFile(target, []byte(`{"version":1,"keys":{"a":"`+fileKey+`"}}`), 0o600); err != nil {
		t.Fatal(err)
	}
	store, err := NewFileStore()
	if err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(target, store.Path()); err != nil {
		t.Skip("symlinks unavailable:", err)
	}
	if _, err := store.Get("a"); !errors.Is(err, ErrInsecurePermissions) {
		t.Fatalf("err = %v", err)
	}
}

func TestConfigFileIsPrivate(t *testing.T) {
	useTempConfig(t)
	cfg, err := LoadConfig()
	if err != nil {
		t.Fatal(err)
	}
	if err := cfg.Save(); err != nil {
		t.Fatal(err)
	}
	path, _ := ConfigPath()
	info, err := os.Stat(path)
	if err != nil {
		t.Fatal(err)
	}
	if info.Mode().Perm() != 0o600 {
		t.Errorf("mode = %o", info.Mode().Perm())
	}
}
