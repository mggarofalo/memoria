package auth

import "errors"

const keyringServiceName = "memoria-cli"

var (
	// ErrNotFound means the store holds no entry for that name.
	ErrNotFound = errors.New("no stored credential")
	// ErrKeyringUnavailable means this machine has no usable OS keyring.
	ErrKeyringUnavailable = errors.New("no OS keyring is available")
	// ErrBusRefused means a keyring may well exist, but the way to it was named by
	// something that failed the checks. It also matches ErrKeyringUnavailable, yet
	// must not be read as "no keyring": refusing the bus is exactly how a poisoned
	// environment would hide a keyring entry that gives it away.
	ErrBusRefused = errors.New("the session bus was refused")
)

// SecretStore abstracts credential storage.
type SecretStore interface {
	Get(name string) (string, error)
	Set(name, value string) error
	Delete(name string) error
}

// KeyName is the store entry for a profile's API key.
func KeyName(profile string) string { return profile + "/api-key" }

// OriginMarkerName is a keyring entry naming the server of a file-backend login. It
// holds no key. It gives a later poisoned config directory something to conflict
// with, as the credentials file itself lives in the directory that could be replaced.
func OriginMarkerName(profile string) string { return profile + "/file-origin" }

// RecordedOrigin returns the server the keyring knows this profile by, from its key
// or from the marker a file login left, or an empty string.
func RecordedOrigin(ring SecretStore, profile string) string {
	if origin := StoredOrigin(ring, profile); origin != "" {
		return origin
	}
	origin, err := ring.Get(OriginMarkerName(profile))
	if err != nil {
		return ""
	}
	return origin
}

// NewKeyringStore opens the OS keyring: the login Keychain on macOS, Credential
// Manager on Windows, and the Secret Service on Linux.
func NewKeyringStore() (SecretStore, error) {
	return openPlatformKeyring(keyringServiceName)
}
