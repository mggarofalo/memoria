//go:build !darwin

package auth

import (
	"errors"
	"strings"
	"testing"

	"github.com/zalando/go-keyring"
)

// useMockKeyring swaps in go-keyring's mock, which has no bus to check.
func useMockKeyring(t *testing.T) {
	t.Helper()
	previous := sessionBusCheck
	sessionBusCheck = func() error { return nil }
	t.Cleanup(func() { sessionBusCheck = previous })
	keyring.MockInit()
}

func TestLibraryStoreContract(t *testing.T) {
	useMockKeyring(t)
	store, err := openPlatformKeyring("memoria-cli-test")
	if err != nil {
		t.Fatal(err)
	}
	exerciseStore(t, store, "profile/api-key", fileKey)
	if err := store.Delete("profile/api-key"); !errors.Is(err, ErrNotFound) {
		t.Fatalf("second Delete = %v", err)
	}
}

func TestLibraryStoreReportsFailuresWithoutTheValue(t *testing.T) {
	useMockKeyring(t)
	store, err := openPlatformKeyring("memoria-cli-test")
	if err != nil {
		t.Fatal(err)
	}
	errLocked := errors.New("keyring is locked")
	keyring.MockInitWithError(errLocked)
	_, getErr := store.Get("a")
	setErr := store.Set("a", fileKey)
	deleteErr := store.Delete("a")
	for _, err := range []error{getErr, setErr, deleteErr} {
		if !errors.Is(err, errLocked) || strings.Contains(err.Error(), fileKey) {
			t.Errorf("err = %v", err)
		}
	}
}

func TestNoKeyringIsUnavailable(t *testing.T) {
	useMockKeyring(t)
	keyring.MockInitWithError(errors.New("no session bus"))
	if _, err := NewKeyringStore(); !errors.Is(err, ErrKeyringUnavailable) {
		t.Fatalf("err = %v", err)
	}
}

func TestKeyringOpensWhenTheProbeIsNotFound(t *testing.T) {
	useMockKeyring(t)
	if _, err := NewKeyringStore(); err != nil {
		t.Fatalf("err = %v", err)
	}
}

// Every operation checks the bus first; the store is the attacker if it is wrong.
func TestRefusedBusStopsEveryOperation(t *testing.T) {
	useMockKeyring(t)
	sessionBusCheck = func() error { return errors.New("not this user's bus") }
	store := &libraryStore{service: "memoria-cli-test"}
	_, getErr := store.Get("a")
	for name, err := range map[string]error{"get": getErr, "set": store.Set("a", fileKey), "delete": store.Delete("a")} {
		if !errors.Is(err, ErrKeyringUnavailable) || strings.Contains(err.Error(), fileKey) {
			t.Errorf("%s: %v", name, err)
		}
	}
	if _, err := NewKeyringStore(); !errors.Is(err, ErrKeyringUnavailable) || !strings.Contains(err.Error(), "not this user's bus") {
		t.Errorf("open: %v", err)
	}
}
