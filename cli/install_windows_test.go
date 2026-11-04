//go:build windows

package main

import (
	"crypto/rand"
	"encoding/hex"
	"strings"
	"testing"

	"golang.org/x/sys/windows/registry"
)

// The usual way of updating PATH expands its value first, which permanently turns
// every %VAR% entry into a fixed path and changes the type to REG_SZ. This runs on
// a throwaway variable and never touches the real PATH.
func TestInstallPs1KeepsUnexpandedPathEntries(t *testing.T) {
	suffix := make([]byte, 6)
	if _, err := rand.Read(suffix); err != nil {
		t.Fatal(err)
	}
	name := "MEMORIA_TEST_PATH_" + hex.EncodeToString(suffix)
	key, err := registry.OpenKey(registry.CURRENT_USER, "Environment", registry.SET_VALUE|registry.QUERY_VALUE)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = key.Close() }()
	const original = `%SystemRoot%\memoria-test;C:\memoria-test-dir;`
	if err := key.SetExpandStringValue(name, original); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = key.DeleteValue(name) })

	good, sums := goodPsFixture(t)
	dir := t.TempDir()
	r := runInstallPsIn(t, good, sums, dir, "-Version", "v1.2.3", "-PathVariable", name)
	if r.err != nil {
		t.Fatalf("%v: %s", r.err, r.output)
	}
	assertPathValue(t, key, name, `%SystemRoot%\memoria-test;C:\memoria-test-dir;`+dir)

	// Installing again adds nothing.
	r = runInstallPsIn(t, good, sums, dir, "-Version", "v1.2.3", "-PathVariable", name)
	if r.err != nil {
		t.Fatalf("%v: %s", r.err, r.output)
	}
	assertPathValue(t, key, name, `%SystemRoot%\memoria-test;C:\memoria-test-dir;`+dir)
}

func assertPathValue(t *testing.T, key registry.Key, name, want string) {
	t.Helper()
	// GetStringValue returns the stored text, with %VAR% intact.
	got, kind, err := key.GetStringValue(name)
	if err != nil {
		t.Fatal(err)
	}
	if kind != registry.EXPAND_SZ {
		t.Fatalf("type = %d, want REG_EXPAND_SZ", kind)
	}
	if !strings.EqualFold(got, want) {
		t.Fatalf("value = %q, want %q", got, want)
	}
}
