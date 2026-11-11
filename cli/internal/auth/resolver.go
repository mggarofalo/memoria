package auth

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
)

// Environment variables the CLI reads.
const (
	EnvAPIKey            = "MEMORIA_API_KEY" //nolint:gosec // G101: the names of environment variables, not credentials
	EnvURL               = "MEMORIA_URL"
	EnvProfile           = "MEMORIA_PROFILE"
	EnvAllowInsecureHTTP = "MEMORIA_ALLOW_INSECURE_HTTP"
	EnvAllowPrivateHTTP  = "MEMORIA_ALLOW_PRIVATE_HTTP"
)

// Sources a key can come from, in precedence order.
const (
	SourceFlag    = "flag"
	SourceEnv     = "env"
	SourceKeyring = "keyring"
	SourceFile    = "file"
)

// Resolver finds the credential and server URL for a command. Precedence, for
// the key: --api-key, then MEMORIA_API_KEY, then the OS keyring, then the on-disk
// credentials file beside the config. That last tier stands in for "config" in
// plane-cli's flag, env, keyring, config order: the config file itself never
// holds a key, and only selects the profile and backend.
//
// A flag is the riskiest: it lands in shell history and in any process listing
// for as long as the command runs. An environment variable is inherited by every
// child process and is readable by the same user through /proc or a debugger.
// The keyring is the only tier that encrypts at rest.
type Resolver struct {
	FlagKey string
	FlagURL string
	Profile string
	Config  *Config
	Getenv  func(string) string
	// Keyring opens the OS keyring lazily. Commands given a key by flag or env
	// should never touch it, since some keyrings prompt.
	Keyring func() (SecretStore, error)
	File    SecretStore
}

// Resolved is a key and where it came from.
type Resolved struct {
	Credential Credential
	Source     string
	// Origin is the server a stored key is bound to. It comes from the store, not
	// from the config, so replacing the config cannot redirect the key.
	Origin string
}

// Stored reports whether the key came from a store rather than this invocation.
func (r Resolved) Stored() bool {
	return r.Source == SourceKeyring || r.Source == SourceFile
}

func (r *Resolver) getenv(name string) string {
	if r.Getenv != nil {
		return r.Getenv(name)
	}
	return os.Getenv(name)
}

// Resolve returns the first available credential.
func (r *Resolver) Resolve() (Resolved, error) {
	if r.FlagKey != "" {
		return explicit(r.FlagKey, "--api-key", SourceFlag)
	}
	if raw := r.getenv(EnvAPIKey); raw != "" {
		return explicit(raw, EnvAPIKey, SourceEnv)
	}

	profile := r.Config.Profile(r.Profile)
	name := KeyName(r.Profile)

	value, keyringErr := r.fromKeyring(profile, name)
	if value != "" {
		return stored(value, SourceKeyring)
	}
	value, err := r.fromFile(name)
	if err != nil {
		return Resolved{}, apperr.Wrap(apperr.KindAuth, err, err.Error())
	}
	if value != "" {
		return stored(value, SourceFile)
	}
	return Resolved{}, r.nothingFound(profile, keyringErr)
}

func explicit(raw, label, source string) (Resolved, error) {
	key, err := NormalizeKey(raw)
	if err != nil {
		return Resolved{}, apperr.Wrap(apperr.KindUsage, err, "invalid "+label+": "+err.Error())
	}
	return Resolved{Credential: NewCredential(key), Source: source}, nil
}

func (r *Resolver) fromKeyring(profile Profile, name string) (string, error) {
	if profile.Backend == BackendFile || r.Keyring == nil {
		return "", nil
	}
	store, err := r.Keyring()
	if err != nil {
		return "", err
	}
	value, err := store.Get(name)
	if errors.Is(err, ErrNotFound) {
		return "", nil
	}
	return value, err
}

func (r *Resolver) fromFile(name string) (string, error) {
	if r.File == nil {
		return "", nil
	}
	value, err := r.File.Get(name)
	if errors.Is(err, ErrNotFound) {
		return "", nil
	}
	return value, err
}

// nothingFound reports a keyring failure as itself, not as the misleading "not
// logged in". A machine with no keyring and no recorded use of one is simply
// not logged in.
func (r *Resolver) nothingFound(profile Profile, keyringErr error) error {
	switch {
	case keyringErr == nil, errors.Is(keyringErr, ErrKeyringUnavailable) && profile.Backend != BackendKeyring:
		return apperr.New(apperr.KindAuth, "no API key found: run 'memoria auth login' or set %s", EnvAPIKey)
	case errors.Is(keyringErr, ErrKeyringUnavailable):
		return apperr.Wrap(apperr.KindAuth, keyringErr,
			fmt.Sprintf("profile %q keeps its key in the OS keyring, but %v", r.Profile, keyringErr))
	}
	return apperr.Wrap(apperr.KindAuth, keyringErr, keyringErr.Error())
}

func stored(value, source string) (Resolved, error) {
	origin, key, err := decodeStoredKey(value)
	if err != nil {
		return Resolved{}, apperr.Wrap(apperr.KindAuth, err, err.Error()+"; run 'memoria auth login' again")
	}
	return Resolved{Credential: NewCredential(key), Source: source, Origin: origin}, nil
}

// EncodeStoredKey is the value a store holds: the key with the origin it may be
// sent to. Keeping them in one secret means nothing outside the store can
// separate them.
func EncodeStoredKey(origin, key string) (string, error) {
	if origin == "" {
		return "", errors.New("a stored key needs an origin")
	}
	data, err := json.Marshal(storedKey{Origin: origin, Key: key})
	if err != nil {
		return "", errors.New("cannot encode the stored key")
	}
	return string(data), nil
}

type storedKey struct {
	Origin string `json:"origin"`
	Key    string `json:"key"`
}

func decodeStoredKey(value string) (origin, key string, err error) {
	var entry storedKey
	// The decoder's message can quote the value, so it is not passed on.
	if json.Unmarshal([]byte(value), &entry) != nil || entry.Origin == "" {
		return "", "", errors.New("the stored API key is not in the expected form")
	}
	key, err = NormalizeKey(entry.Key)
	if err != nil {
		return "", "", fmt.Errorf("the stored API key is invalid: %w", err)
	}
	return entry.Origin, key, nil
}

// StoredOrigin returns the origin a store's entry for a profile is bound to, or
// an empty string if it has no readable entry.
func StoredOrigin(store SecretStore, profile string) string {
	value, err := store.Get(KeyName(profile))
	if err != nil {
		return ""
	}
	origin, _, err := decodeStoredKey(value)
	if err != nil {
		return ""
	}
	return origin
}

// ExplicitURL reports whether this invocation names the server itself, by flag or
// environment, rather than leaving it to the config file. A key given the same way
// is tied to no server, so it may only go to a server named the same way: the
// config file can be replaced by anyone who can set XDG_CONFIG_HOME or HOME.
func (r *Resolver) ExplicitURL() bool {
	return r.FlagURL != "" || r.getenv(EnvURL) != ""
}

// ResolveURL returns the server URL from flag, env, or the active profile.
func (r *Resolver) ResolveURL() string {
	if r.FlagURL != "" {
		return r.FlagURL
	}
	if v := r.getenv(EnvURL); v != "" {
		return v
	}
	return r.Config.Profile(r.Profile).APIURL
}
