package auth

import "errors"

const keyringServiceName = "memoria-cli"

var (
	// ErrNotFound means the store holds no entry for that name.
	ErrNotFound = errors.New("no stored credential")
	// ErrKeyringUnavailable means this machine has no usable OS keyring.
	ErrKeyringUnavailable = errors.New("no OS keyring is available")
)

// SecretStore abstracts credential storage.
type SecretStore interface {
	Get(name string) (string, error)
	Set(name, value string) error
	Delete(name string) error
}

// KeyName is the store entry for a profile's API key.
func KeyName(profile string) string { return profile + "/api-key" }

// NewKeyringStore opens the OS keyring: the login Keychain on macOS, Credential
// Manager on Windows, and the Secret Service on Linux.
func NewKeyringStore() (SecretStore, error) {
	return openPlatformKeyring(keyringServiceName)
}
