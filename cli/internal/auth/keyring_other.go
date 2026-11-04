//go:build !darwin

package auth

import (
	"errors"
	"fmt"

	"github.com/zalando/go-keyring"
)

// availabilityProbe is an entry name nothing stores, used to ask whether the
// keyring answers at all.
const availabilityProbe = "availability-probe"

// libraryStore implements SecretStore over go-keyring: Credential Manager on
// Windows and the Secret Service over D-Bus on Linux.
type libraryStore struct {
	service string
}

// openPlatformKeyring reports ErrKeyringUnavailable on a machine with no keyring,
// such as a headless Linux host with no session bus.
func openPlatformKeyring(service string) (SecretStore, error) {
	if err := sessionBusCheck(); err != nil {
		return nil, fmt.Errorf("%w: %w", ErrKeyringUnavailable, err)
	}
	if _, err := keyring.Get(service, availabilityProbe); err != nil && !errors.Is(err, keyring.ErrNotFound) {
		return nil, ErrKeyringUnavailable
	}
	return &libraryStore{service: service}, nil
}

// Get retrieves a secret. Errors never include the value.
func (l *libraryStore) Get(name string) (string, error) {
	if err := sessionBusCheck(); err != nil {
		return "", fmt.Errorf("%w: %w", ErrKeyringUnavailable, err)
	}
	value, err := keyring.Get(l.service, name)
	if errors.Is(err, keyring.ErrNotFound) {
		return "", ErrNotFound
	}
	if err != nil {
		return "", fmt.Errorf("failed to read from the OS keyring: %w", err)
	}
	return value, nil
}

// Set stores a secret.
func (l *libraryStore) Set(name, value string) error {
	if err := sessionBusCheck(); err != nil {
		return fmt.Errorf("%w: %w", ErrKeyringUnavailable, err)
	}
	if err := keyring.Set(l.service, name, value); err != nil {
		return fmt.Errorf("failed to write to the OS keyring: %w", err)
	}
	return nil
}

// Delete removes a secret. Removing an absent entry is ErrNotFound.
func (l *libraryStore) Delete(name string) error {
	if err := sessionBusCheck(); err != nil {
		return fmt.Errorf("%w: %w", ErrKeyringUnavailable, err)
	}
	err := keyring.Delete(l.service, name)
	if errors.Is(err, keyring.ErrNotFound) {
		return ErrNotFound
	}
	if err != nil {
		return fmt.Errorf("failed to delete from the OS keyring: %w", err)
	}
	return nil
}
