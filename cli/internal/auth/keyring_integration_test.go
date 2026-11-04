package auth

import (
	"crypto/rand"
	"encoding/hex"
	"errors"
	"os"
	"strings"
	"testing"

	"github.com/99designs/keyring"
)

func TestKeyringStoreOverTheLibrary(t *testing.T) {
	store := &KeyringStore{ring: keyring.NewArrayKeyring(nil)}
	exerciseStore(t, store, "profile/api-key", fileKey)
}

// brokenRing fails every operation, as a locked keyring does.
type brokenRing struct{ keyring.Keyring }

var errLocked = errors.New("keyring is locked")

func (brokenRing) Get(string) (keyring.Item, error) { return keyring.Item{}, errLocked }
func (brokenRing) Set(keyring.Item) error           { return errLocked }
func (brokenRing) Remove(string) error              { return errLocked }

func TestKeyringStoreReportsFailuresWithoutTheValue(t *testing.T) {
	store := &KeyringStore{ring: brokenRing{}}
	_, getErr := store.Get("a")
	setErr := store.Set("a", fileKey)
	deleteErr := store.Delete("a")
	for _, err := range []error{getErr, setErr, deleteErr} {
		if !errors.Is(err, errLocked) || strings.Contains(err.Error(), fileKey) {
			t.Errorf("err = %v", err)
		}
	}
}

// absentRing reports every entry missing, as a real keyring does.
type absentRing struct{ keyring.Keyring }

func (absentRing) Remove(string) error { return keyring.ErrKeyNotFound }

func TestKeyringDeleteOfAnAbsentEntry(t *testing.T) {
	store := &KeyringStore{ring: absentRing{}}
	if err := store.Delete("a"); !errors.Is(err, ErrNotFound) {
		t.Fatalf("err = %v", err)
	}
}

func TestOpenFailuresBecomeUnavailable(t *testing.T) {
	previous := openRing
	t.Cleanup(func() { openRing = previous })
	for _, cause := range []error{keyring.ErrNoAvailImpl, errors.New("no session bus")} {
		openRing = func(keyring.Config) (keyring.Keyring, error) { return nil, cause }
		if _, err := NewKeyringStore(); !errors.Is(err, ErrKeyringUnavailable) {
			t.Errorf("cause %v: err = %v", cause, err)
		}
	}
	openRing = func(keyring.Config) (keyring.Keyring, error) { return keyring.NewArrayKeyring(nil), nil }
	if _, err := NewKeyringStore(); err != nil {
		t.Errorf("err = %v", err)
	}
}

// TestOpeningTheKeyring passes either way a machine can answer: a store, or the
// sentinel the CLI turns into its advice about --backend file. It only opens the
// keyring; it reads and writes nothing.
func TestOpeningTheKeyring(t *testing.T) {
	store, err := NewKeyringStore()
	if err != nil && !errors.Is(err, ErrKeyringUnavailable) {
		t.Fatalf("err = %v", err)
	}
	if err == nil && store == nil {
		t.Fatal("expected a store")
	}
}

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
	store, err := newKeyringStoreNamed("memoria-cli-test")
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
