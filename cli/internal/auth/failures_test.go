package auth

import (
	"errors"
	"os"
	"path/filepath"
	"testing"
)

// noHome makes os.UserHomeDir fail on every platform.
func noHome(t *testing.T) {
	t.Helper()
	t.Setenv("XDG_CONFIG_HOME", "")
	t.Setenv("HOME", "")
	t.Setenv("USERPROFILE", "")
}

func TestNoHomeDirectory(t *testing.T) {
	noHome(t)
	if _, err := ConfigDir(); err == nil {
		t.Error("ConfigDir should fail")
	}
	if _, err := ConfigPath(); err == nil {
		t.Error("ConfigPath should fail")
	}
	if _, err := LoadConfig(); err == nil {
		t.Error("LoadConfig should fail")
	}
	if _, err := NewFileStore(); err == nil {
		t.Error("NewFileStore should fail")
	}
	if err := (&Config{}).Save(); err == nil {
		t.Error("Save should fail")
	}
}

func TestDefaultConfigDirIsUnderHome(t *testing.T) {
	t.Setenv("XDG_CONFIG_HOME", "")
	home, err := os.UserHomeDir()
	if err != nil {
		t.Skip("no home directory")
	}
	dir, err := ConfigDir()
	if err != nil {
		t.Fatal(err)
	}
	if dir != filepath.Join(home, ".config", configDirName) {
		t.Fatalf("dir = %s", dir)
	}
}

// configDirBlockedByFile puts a file where the parent directory should be.
func configDirBlockedByFile(t *testing.T) {
	t.Helper()
	blocker := filepath.Join(t.TempDir(), "file")
	if err := os.WriteFile(blocker, nil, 0o600); err != nil {
		t.Fatal(err)
	}
	t.Setenv("XDG_CONFIG_HOME", blocker)
}

func TestWritesFailWhenTheDirectoryCannotBeCreated(t *testing.T) {
	configDirBlockedByFile(t)
	if err := (&Config{}).Save(); err == nil {
		t.Error("Save should fail")
	}
	store, err := NewFileStore()
	if err != nil {
		t.Fatal(err)
	}
	if err := store.Set("a", fileKey); err == nil {
		t.Error("Set should fail")
	}
}

// credentialsAreADirectory makes the target path impossible to read or replace.
func credentialsAreADirectory(t *testing.T) *FileStore {
	t.Helper()
	dir := useTempConfig(t)
	if err := os.MkdirAll(filepath.Join(dir, credentialFileName, "child"), 0o700); err != nil {
		t.Fatal(err)
	}
	store, err := NewFileStore()
	if err != nil {
		t.Fatal(err)
	}
	return store
}

func TestDirectoryInPlaceOfTheCredentialsFile(t *testing.T) {
	store := credentialsAreADirectory(t)
	if _, err := store.Get("a"); !errors.Is(err, ErrInsecurePermissions) {
		t.Errorf("Get = %v", err)
	}
	if err := store.Set("a", fileKey); !errors.Is(err, ErrInsecurePermissions) {
		t.Errorf("Set = %v", err)
	}
	if err := store.Delete("a"); !errors.Is(err, ErrInsecurePermissions) {
		t.Errorf("Delete = %v", err)
	}
}

func TestReplacingADirectoryFailsAndLeavesNoTempFile(t *testing.T) {
	dir := useTempConfig(t)
	target := filepath.Join(dir, "target")
	if err := os.MkdirAll(filepath.Join(target, "child"), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := writePrivateFile(target, []byte("{}")); err == nil {
		t.Fatal("expected a rename failure")
	}
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	if len(entries) != 1 {
		t.Fatalf("entries = %v", entries)
	}
}

func TestConfigThatCannotBeRead(t *testing.T) {
	dir := useTempConfig(t)
	if err := os.MkdirAll(filepath.Join(dir, configFileName), 0o700); err != nil {
		t.Fatal(err)
	}
	if _, err := LoadConfig(); err == nil {
		t.Fatal("expected a read failure")
	}
}

func TestEmptyConfigFileGetsDefaults(t *testing.T) {
	dir := useTempConfig(t)
	if err := os.MkdirAll(dir, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, configFileName), []byte(`{"profiles":null}`), 0o600); err != nil {
		t.Fatal(err)
	}
	cfg, err := LoadConfig()
	if err != nil {
		t.Fatal(err)
	}
	if cfg.ActiveProfile != DefaultProfile || cfg.Profiles == nil || cfg.HasStraySecret {
		t.Fatalf("cfg = %+v", cfg)
	}
}

func TestVerifyPrivateOfAMissingOrIrregularPath(t *testing.T) {
	dir := t.TempDir()
	if err := verifyPrivate(filepath.Join(dir, "missing")); !errors.Is(err, os.ErrNotExist) {
		t.Errorf("missing: %v", err)
	}
	if err := verifyPrivate(dir); !errors.Is(err, ErrInsecurePermissions) {
		t.Errorf("directory: %v", err)
	}
}

func TestStoredReportsTheSource(t *testing.T) {
	for source, want := range map[string]bool{SourceFlag: false, SourceEnv: false, SourceKeyring: true, SourceFile: true} {
		if got := (Resolved{Source: source}).Stored(); got != want {
			t.Errorf("Stored(%s) = %v", source, got)
		}
	}
}

func TestEmptyCredentialsFileHasNoKeys(t *testing.T) {
	dir := useTempConfig(t)
	store, err := NewFileStore()
	if err != nil {
		t.Fatal(err)
	}
	if err := store.Set("a", fileKey); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, credentialFileName), []byte(`{"version":1}`), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := store.Get("a"); !errors.Is(err, ErrNotFound) {
		t.Fatalf("err = %v", err)
	}
}
