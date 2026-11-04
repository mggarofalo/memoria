package auth

import (
	"errors"
	"fmt"

	"github.com/99designs/keyring"
)

const keyringServiceName = "memoria-cli"

var (
	// ErrNotFound means the store holds no entry for that name.
	ErrNotFound = errors.New("no stored credential")
	// ErrKeyringUnavailable means this machine has no usable OS keyring.
	ErrKeyringUnavailable = errors.New("no OS keyring is available")
)

// openRing is replaceable so tests can reach the failure paths on any machine.
var openRing = keyring.Open

// SecretStore abstracts credential storage.
type SecretStore interface {
	Get(name string) (string, error)
	Set(name, value string) error
	Delete(name string) error
}

// KeyName is the store entry for a profile's API key.
func KeyName(profile string) string { return profile + "/api-key" }

// KeyringStore implements SecretStore over the OS keyring.
type KeyringStore struct {
	ring keyring.Keyring
}

// NewKeyringStore opens the OS keyring. On macOS that goes through the security
// tool (SecurityStore), and elsewhere through the keyring library (KeyringStore).
func NewKeyringStore() (SecretStore, error) {
	return openPlatformKeyring(keyringServiceName)
}

func newKeyringStoreNamed(service string) (SecretStore, error) {
	ring, err := openRing(keyring.Config{
		ServiceName: service,
		// Real OS keyrings only. The library's file backend prompts for a
		// passphrase, which cannot work headless, and FileStore covers that case
		// honestly. pass spawns a subprocess, and keyctl does not outlive the
		// session.
		AllowedBackends: []keyring.BackendType{
			keyring.WinCredBackend,
			keyring.SecretServiceBackend,
			keyring.KWalletBackend,
		},
		WinCredPrefix:           service,
		LibSecretCollectionName: service,
		KWalletAppID:            service,
		KWalletFolder:           service,
	})
	if err != nil {
		if errors.Is(err, keyring.ErrNoAvailImpl) {
			return nil, ErrKeyringUnavailable
		}
		return nil, fmt.Errorf("%w: %v", ErrKeyringUnavailable, err)
	}
	return &KeyringStore{ring: ring}, nil
}

// Get retrieves a secret. Errors never include the value.
func (k *KeyringStore) Get(name string) (string, error) {
	item, err := k.ring.Get(name)
	if err != nil {
		if errors.Is(err, keyring.ErrKeyNotFound) {
			return "", ErrNotFound
		}
		return "", fmt.Errorf("failed to read from the OS keyring: %w", err)
	}
	return string(item.Data), nil
}

// Set stores a secret.
func (k *KeyringStore) Set(name, value string) error {
	err := k.ring.Set(keyring.Item{
		Key:         name,
		Data:        []byte(value),
		Label:       "Memoria API key (" + name + ")",
		Description: "Memoria CLI API key",
	})
	if err != nil {
		return fmt.Errorf("failed to write to the OS keyring: %w", err)
	}
	return nil
}

// Delete removes a secret. Removing an absent entry is ErrNotFound.
func (k *KeyringStore) Delete(name string) error {
	if err := k.ring.Remove(name); err != nil {
		if errors.Is(err, keyring.ErrKeyNotFound) {
			return ErrNotFound
		}
		return fmt.Errorf("failed to delete from the OS keyring: %w", err)
	}
	return nil
}
