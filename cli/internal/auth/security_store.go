package auth

import (
	"bytes"
	"context"
	"encoding/hex"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"regexp"
	"slices"
	"strings"
	"time"
)

const (
	securityTimeout = time.Minute
	// securityLineLimit stays under the 4096-byte line "security -i" reads.
	securityLineLimit = 4000
	// securityNotFound is errSecItemNotFound as the tool's exit status.
	securityNotFound = 44
)

// securityNamePattern is all a service or entry name may contain. Names go into
// a line that "security -i" parses, so anything else is refused rather than
// quoted: there is then no quoting to get wrong.
var securityNamePattern = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9._/-]*$`)

// SecurityStore implements SecretStore on macOS by running /usr/bin/security,
// which reaches the Keychain without cgo.
//
// The key must never be a process argument, where any local process could read
// it. "security add-generic-password -w <key>" would do exactly that, so writes
// go through "security -i", which reads its command from stdin, and the key is
// hex encoded with -X so it needs no quoting either. Reads use -w, which prints
// the key on stdout. The tool's output is never repeated in an error.
//
// The entry's access list trusts /usr/bin/security, because that is what created
// it. So any process running as this user can read the key through that tool
// without a prompt. The Keychain still encrypts it at rest and locks it with the
// login keychain. A cgo build could scope access to the memoria binary; the
// release build is cgo-free so that it stays reproducible on one runner.
type SecurityStore struct {
	path    string
	service string
}

type securityResult struct {
	stdout []byte
	stderr []byte
	err    error
}

// Get retrieves a secret.
func (s *SecurityStore) Get(name string) (string, error) {
	if err := s.checkNames(name); err != nil {
		return "", err
	}
	result := s.run("", "find-generic-password", "-s", s.service, "-a", name, "-w")
	if result.err != nil {
		return "", classifySecurityFailure(result, "read from")
	}
	return strings.TrimRight(string(result.stdout), "\r\n"), nil
}

// Set stores a secret, then reads it back. "security -i" keeps going after a
// failed command, so its exit status does not show whether the write happened.
func (s *SecurityStore) Set(name, value string) error {
	if err := s.checkNames(name); err != nil {
		return err
	}
	line := fmt.Sprintf("add-generic-password -U -s %s -a %s -X %s\n", s.service, name, hex.EncodeToString([]byte(value)))
	if len(line) > securityLineLimit {
		return errors.New("the API key is too long to store in the Keychain")
	}
	if result := s.run(line, "-i"); result.err != nil {
		return classifySecurityFailure(result, "write to")
	}
	stored, err := s.Get(name)
	if err != nil {
		return fmt.Errorf("the Keychain did not keep the key: %w", err)
	}
	if stored != value {
		return errors.New("the Keychain did not keep the key: it read back differently")
	}
	return nil
}

// Delete removes a secret. Removing an absent entry is ErrNotFound.
func (s *SecurityStore) Delete(name string) error {
	if err := s.checkNames(name); err != nil {
		return err
	}
	if result := s.run("", "delete-generic-password", "-s", s.service, "-a", name); result.err != nil {
		return classifySecurityFailure(result, "delete from")
	}
	return nil
}

func (s *SecurityStore) checkNames(name string) error {
	if !securityNamePattern.MatchString(s.service) || !securityNamePattern.MatchString(name) {
		return errors.New("the Keychain entry name contains unsupported characters")
	}
	return nil
}

func (s *SecurityStore) run(stdin string, args ...string) securityResult {
	ctx, cancel := context.WithTimeout(context.Background(), securityTimeout)
	defer cancel()
	cmd := exec.CommandContext(ctx, s.path, args...)
	cmd.Env = withoutAPIKey(os.Environ())
	cmd.Stdin = strings.NewReader(stdin)
	var stdout, stderr bytes.Buffer
	cmd.Stdout, cmd.Stderr = &stdout, &stderr
	err := cmd.Run()
	return securityResult{stdout: stdout.Bytes(), stderr: stderr.Bytes(), err: err}
}

func withoutAPIKey(environ []string) []string {
	return slices.DeleteFunc(environ, func(entry string) bool {
		return strings.HasPrefix(strings.ToUpper(entry), EnvAPIKey+"=")
	})
}

// classifySecurityFailure recognizes known outcomes. Only fixed text comes out.
func classifySecurityFailure(result securityResult, action string) error {
	var exit *exec.ExitError
	if !errors.As(result.err, &exit) {
		return fmt.Errorf("%w: cannot run the security tool", ErrKeyringUnavailable)
	}
	message := string(result.stderr)
	switch {
	case exit.ExitCode() == securityNotFound, strings.Contains(message, "could not be found"):
		return ErrNotFound
	case strings.Contains(message, "User interaction is not allowed"):
		return errors.New("the Keychain is locked and cannot prompt here; unlock it with 'security unlock-keychain', or use a terminal in your desktop session")
	}
	return fmt.Errorf("failed to %s the Keychain: the security tool exited with status %d", action, exit.ExitCode())
}
