// Package auth stores and resolves API keys. Mirrors plane-cli's internal/auth:
// a SecretStore over the OS keyring, non-secret profiles in a config file, and a
// resolver with a fixed precedence.
package auth

import (
	"errors"
	"strings"

	"github.com/mggarofalo/memoria/cli/internal/redact"
)

const (
	minKeyLength = 8
	maxKeyLength = 4096
	// utf8BOM can lead a key piped from PowerShell.
	utf8BOM = "\xef\xbb\xbf"
)

// Credential holds an API key. The value sits behind a closure so that no
// formatting verb, struct dump or JSON encoder can reach it, even when a
// Credential is nested in an unexported field where fmt cannot call methods.
//
// There is deliberately no masked form. plane-cli shows a prefix and the last
// four characters; for a key that opens a corpus of transcripts, status output
// says only that a key is present.
type Credential struct {
	reveal func() string
}

// NewCredential wraps a key.
func NewCredential(key string) Credential {
	return Credential{reveal: func() string { return key }}
}

// Reveal returns the key. Callers: the transport and the secret stores.
func (c Credential) Reveal() string {
	if c.reveal == nil {
		return ""
	}
	return c.reveal()
}

// IsEmpty reports whether no key is held.
func (c Credential) IsEmpty() bool { return c.Reveal() == "" }

// String implements fmt.Stringer without exposing the key.
func (c Credential) String() string { return redact.Placeholder }

// GoString implements fmt.GoStringer without exposing the key.
func (c Credential) GoString() string { return redact.Placeholder }

// MarshalJSON keeps the key out of any JSON encoding.
func (c Credential) MarshalJSON() ([]byte, error) { return []byte(`"` + redact.Placeholder + `"`), nil }

// NormalizeKey trims input and checks that the key can travel in a header. The
// key is otherwise opaque: its mem_<id>_<secret> format belongs to the server.
// Errors never include the value.
func NormalizeKey(raw string) (string, error) {
	key := strings.TrimSpace(strings.TrimPrefix(raw, utf8BOM))
	switch {
	case key == "":
		return "", errors.New("the API key is empty")
	case len(key) < minKeyLength:
		return "", errors.New("the API key is too short")
	case len(key) > maxKeyLength:
		return "", errors.New("the API key is too long")
	}
	for i := 0; i < len(key); i++ {
		if key[i] <= 0x20 || key[i] >= 0x7f {
			return "", errors.New("the API key contains whitespace or non-printable characters")
		}
	}
	return key, nil
}
