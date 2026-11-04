package auth

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
)

// FileWarning is shown whenever the plaintext file backend is written or used.
// It states what the file does and does not defend against.
const FileWarning = "the file backend stores the API key unencrypted on disk. It is weaker than the OS keyring: " +
	"permissions keep other non-admin users out, but any process running as you, an administrator or root, " +
	"and anything that reads your backups or disk can read the key. Use it only where no keyring exists, such as CI or a headless host."

// ErrInsecurePermissions means another account can reach the file or its directory.
var ErrInsecurePermissions = errors.New("permissions are too open")

// FileStore implements SecretStore in a plaintext JSON file beside the config.
//
// It is plaintext rather than passphrase-encrypted because its purpose is
// unattended use: a passphrase kept next to the file adds nothing, and a prompt
// cannot work in CI. Saying so is better than implying protection.
type FileStore struct {
	path string
}

type credentialFile struct {
	Version int               `json:"version"`
	Keys    map[string]string `json:"keys"`
}

// NewFileStore returns the store for the current config directory.
func NewFileStore() (*FileStore, error) {
	dir, err := ConfigDir()
	if err != nil {
		return nil, err
	}
	return &FileStore{path: filepath.Join(dir, credentialFileName)}, nil
}

// Path returns the credentials file path.
func (f *FileStore) Path() string { return f.path }

// Get retrieves a secret. It refuses a file other accounts can reach, as ssh
// does for private keys, because a key that was exposed should not be used
// silently.
func (f *FileStore) Get(name string) (string, error) {
	creds, err := f.load()
	if err != nil {
		return "", err
	}
	value, ok := creds.Keys[name]
	if !ok || value == "" {
		return "", ErrNotFound
	}
	return value, nil
}

// Set stores a secret.
func (f *FileStore) Set(name, value string) error {
	creds, err := f.load()
	if err != nil && !errors.Is(err, ErrInsecurePermissions) {
		return err
	}
	if errors.Is(err, ErrInsecurePermissions) {
		return fmt.Errorf("%w: %s; %s", err, f.path, permissionFixHint(f.path))
	}
	creds.Keys[name] = value
	return f.save(creds)
}

// Delete removes a secret. Deleting works on a too-open file, since removing the
// key is exactly what such a file needs.
func (f *FileStore) Delete(name string) error {
	creds, err := f.read()
	if err != nil {
		return err
	}
	if _, ok := creds.Keys[name]; !ok {
		return ErrNotFound
	}
	delete(creds.Keys, name)
	if len(creds.Keys) == 0 {
		if err := os.Remove(f.path); err != nil {
			return fmt.Errorf("failed to remove the credentials file: %w", err)
		}
		return nil
	}
	return f.save(creds)
}

func (f *FileStore) load() (*credentialFile, error) {
	err := verifyPrivate(f.path)
	switch {
	case err == nil, errors.Is(err, os.ErrNotExist):
		return f.read()
	case errors.Is(err, ErrInsecurePermissions):
		return nil, fmt.Errorf("%w: %s; %s", err, f.path, permissionFixHint(f.path))
	}
	return nil, fmt.Errorf("cannot check the credentials file: %w", err)
}

func (f *FileStore) read() (*credentialFile, error) {
	empty := &credentialFile{Version: 1, Keys: map[string]string{}}
	info, err := os.Lstat(f.path)
	if errors.Is(err, os.ErrNotExist) {
		return empty, nil
	}
	if err != nil {
		return nil, fmt.Errorf("cannot check the credentials file: %w", err)
	}
	if !info.Mode().IsRegular() {
		return nil, fmt.Errorf("%w: %s is not a regular file", ErrInsecurePermissions, f.path)
	}
	data, err := os.ReadFile(f.path)
	if err != nil {
		return nil, fmt.Errorf("failed to read the credentials file: %w", err)
	}
	var creds credentialFile
	// The decoder's message can quote file content, so it is not passed on.
	if err := json.Unmarshal(data, &creds); err != nil {
		return nil, errors.New("the credentials file is corrupt: " + f.path)
	}
	if creds.Keys == nil {
		creds.Keys = map[string]string{}
	}
	return &creds, nil
}

func (f *FileStore) save(creds *credentialFile) error {
	creds.Version = 1
	data, err := json.MarshalIndent(creds, "", "  ")
	if err != nil {
		return errors.New("failed to encode the credentials file")
	}
	if err := writePrivateFile(f.path, append(data, '\n')); err != nil {
		return fmt.Errorf("failed to write the credentials file: %w", err)
	}
	return nil
}

// writePrivateFile creates the private directory if needed and atomically
// replaces path, so a crash cannot leave a half-written or loosely permissioned
// file. The temp file is locked down before any content goes in.
func writePrivateFile(path string, data []byte) (err error) {
	dir := filepath.Dir(path)
	if err := ensurePrivateDir(dir); err != nil {
		return err
	}
	tmp, err := os.CreateTemp(dir, ".tmp-*")
	if err != nil {
		return err
	}
	name := tmp.Name()
	defer func() {
		if err != nil {
			_ = tmp.Close()
			_ = os.Remove(name)
		}
	}()
	if err = protectFile(name); err != nil {
		return err
	}
	if _, err = tmp.Write(data); err != nil {
		return err
	}
	if err = tmp.Sync(); err != nil {
		return err
	}
	if err = tmp.Close(); err != nil {
		return err
	}
	return os.Rename(name, path)
}
