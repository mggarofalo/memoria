// Package redact keeps API keys out of everything the CLI writes.
//
// It is the last line of defence, not the first: keys are handled through
// auth.Credential and only ever leave through the transport. This package
// exists for the paths the CLI does not control, such as a server that echoes
// the Authorization header into an error, or a library error that quotes an
// argument.
package redact

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"io"
	"net/url"
	"regexp"
	"sort"
	"strings"
	"sync"
)

// Placeholder replaces every redacted value.
const Placeholder = "[REDACTED]"

const minSecretLength = 8

// diagnosticPatterns catch credentials the Redactor was never told about. They
// apply only to diagnostics (errors and stderr), because document content on
// stdout can legitimately mention other tokens and is the caller's own data.
var diagnosticPatterns = []struct {
	re   *regexp.Regexp
	repl string
}{
	{regexp.MustCompile(`(?i)(authorization["']?\s*[:=]\s*["']?)(?:bearer\s+|basic\s+)?[^\s"',;]+`), "${1}" + Placeholder},
	{regexp.MustCompile(`(?i)(bearer\s+)[A-Za-z0-9._~+/=-]+`), "${1}" + Placeholder},
	{regexp.MustCompile(`mem_[A-Za-z0-9]+_[A-Za-z0-9]+`), Placeholder},
}

// Redactor scrubs known secret values from text.
type Redactor struct {
	mu      sync.RWMutex
	secrets []string
}

// New returns an empty Redactor.
func New() *Redactor { return &Redactor{} }

// Add registers a secret, along with the encodings a server or library is
// likely to echo it back in.
func (r *Redactor) Add(secret string) {
	// A very short value cannot be a key (auth rejects it), and replacing it
	// everywhere would only garble the error that says so.
	if len(secret) < minSecretLength {
		return
	}
	variants := encodings(secret)
	// The key's format is the server's, so this is only a defence: the part after
	// the last underscore of mem_<id>_<secret> is what opens the corpus, even alone.
	if head, tail, ok := cutLast(secret); ok && strings.HasPrefix(head, "mem_") && len(tail) >= minSecretLength {
		variants = append(variants, encodings(tail)...)
	}

	r.mu.Lock()
	defer r.mu.Unlock()
	seen := map[string]struct{}{}
	for _, s := range r.secrets {
		seen[s] = struct{}{}
	}
	for _, v := range variants {
		if _, ok := seen[v]; !ok {
			seen[v] = struct{}{}
			r.secrets = append(r.secrets, v)
		}
	}
	// Longest first, so one variant cannot leave the tail of another behind.
	sort.SliceStable(r.secrets, func(i, j int) bool { return len(r.secrets[i]) > len(r.secrets[j]) })
}

func encodings(secret string) []string {
	variants := []string{
		secret,
		url.QueryEscape(secret),
		url.PathEscape(secret),
		base64.StdEncoding.EncodeToString([]byte(secret)),
		base64.RawStdEncoding.EncodeToString([]byte(secret)),
		base64.URLEncoding.EncodeToString([]byte(secret)),
		base64.RawURLEncoding.EncodeToString([]byte(secret)),
	}
	// Marshalling a string cannot fail.
	quoted, _ := json.Marshal(secret)
	return append(variants, string(quoted[1:len(quoted)-1]))
}

func cutLast(s string) (head, tail string, ok bool) {
	i := strings.LastIndexByte(s, '_')
	if i < 0 {
		return "", "", false
	}
	return s[:i], s[i+1:], true
}

// String removes known secrets from s.
func (r *Redactor) String(s string) string {
	r.mu.RLock()
	defer r.mu.RUnlock()
	for _, secret := range r.secrets {
		s = strings.ReplaceAll(s, secret, Placeholder)
	}
	return s
}

// Bytes removes known secrets from b. It returns b unchanged when none match.
func (r *Redactor) Bytes(b []byte) []byte {
	r.mu.RLock()
	defer r.mu.RUnlock()
	for _, secret := range r.secrets {
		if bytes.Contains(b, []byte(secret)) {
			b = bytes.ReplaceAll(b, []byte(secret), []byte(Placeholder))
		}
	}
	return b
}

// Diagnostic removes known secrets and anything shaped like a credential.
func (r *Redactor) Diagnostic(s string) string {
	s = r.String(s)
	for _, p := range diagnosticPatterns {
		s = p.re.ReplaceAllString(s, p.repl)
	}
	return s
}

// Writer returns a writer that scrubs known secrets from each Write.
func (r *Redactor) Writer(w io.Writer) io.Writer {
	return writerFunc(func(p []byte) (int, error) {
		if _, err := w.Write(r.Bytes(p)); err != nil {
			return 0, err
		}
		return len(p), nil
	})
}

// DiagnosticWriter returns a writer that applies Diagnostic to each Write.
func (r *Redactor) DiagnosticWriter(w io.Writer) io.Writer {
	return writerFunc(func(p []byte) (int, error) {
		if _, err := io.WriteString(w, r.Diagnostic(string(p))); err != nil {
			return 0, err
		}
		return len(p), nil
	})
}

type writerFunc func([]byte) (int, error)

func (f writerFunc) Write(p []byte) (int, error) { return f(p) }
