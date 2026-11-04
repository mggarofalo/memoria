package auth

import (
	"crypto/rand"
	"encoding/hex"
	"errors"
	"os"
	"testing"
)

// exerciseStore walks a store through its whole contract, leaving it empty.
func exerciseStore(t *testing.T, store SecretStore, name, value string) {
	t.Helper()
	if _, err := store.Get(name); !errors.Is(err, ErrNotFound) {
		t.Fatalf("Get before Set = %v", err)
	}
	if err := store.Set(name, value); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = store.Delete(name) })

	if got, err := store.Get(name); err != nil || got != value {
		t.Fatalf("read back a different value, err = %v", err)
	}
	if err := store.Delete(name); err != nil {
		t.Fatal(err)
	}
	if _, err := store.Get(name); !errors.Is(err, ErrNotFound) {
		t.Fatalf("Get after Delete = %v", err)
	}
}

// TestRealKeyringRoundTrip exercises the actual OS keyring under a separate,
// test-only service name, with a random throwaway value that is removed before
// the test ends. It is opt-in so CI and other machines never touch a keyring.
func TestRealKeyringRoundTrip(t *testing.T) {
	if os.Getenv("MEMORIA_TEST_REAL_KEYRING") != "1" {
		t.Skip("set MEMORIA_TEST_REAL_KEYRING=1 to test against the real OS keyring")
	}
	store, err := openPlatformKeyring("memoria-cli-test")
	if err != nil {
		t.Fatal(err)
	}
	raw := make([]byte, 16)
	if _, err := rand.Read(raw); err != nil {
		t.Fatal(err)
	}
	value := hex.EncodeToString(raw)
	name := "test-" + value[:8] + "/api-key"
	exerciseStore(t, store, name, value)
	if err := store.Delete(name); !errors.Is(err, ErrNotFound) {
		t.Fatalf("second Delete = %v", err)
	}
}
