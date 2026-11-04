//go:build !darwin

package auth

import (
	"errors"
	"strings"
	"testing"

	"github.com/zalando/go-keyring"
)

func TestLibraryStoreContract(t *testing.T) {
	keyring.MockInit()
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
	keyring.MockInit()
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
	keyring.MockInitWithError(errors.New("no session bus"))
	if _, err := NewKeyringStore(); !errors.Is(err, ErrKeyringUnavailable) {
		t.Fatalf("err = %v", err)
	}
}

func TestKeyringOpensWhenTheProbeIsNotFound(t *testing.T) {
	keyring.MockInit()
	if _, err := NewKeyringStore(); err != nil {
		t.Fatalf("err = %v", err)
	}
}
