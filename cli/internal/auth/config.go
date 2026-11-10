package auth

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
)

const (
	configDirName      = "memoria"
	configFileName     = "config.json"
	credentialFileName = "credentials.json"

	// DefaultProfile is used until another profile is chosen.
	DefaultProfile = "default"

	// BackendKeyring stores the key in the OS keyring.
	BackendKeyring = "keyring"
	// BackendFile stores the key in the plaintext credentials file.
	BackendFile = "file"
)

var profileNamePattern = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`)

// Config holds non-secret settings. It has no field for a key on purpose: the
// key lives in a SecretStore, and a key pasted into this file by hand is ignored.
type Config struct {
	ActiveProfile string             `json:"active_profile"`
	Profiles      map[string]Profile `json:"profiles"`

	// HasStraySecret is set when the file on disk holds a field that looks like
	// a key. It is never read as one, and the next Save removes it.
	HasStraySecret bool `json:"-"`
}

// Profile holds per-profile settings.
type Profile struct {
	// APIURL is the profile's server. It is a convenience: the origin a stored key
	// may go to is recorded with the key itself, because this file can be replaced
	// by anyone who can set XDG_CONFIG_HOME or HOME.
	APIURL string `json:"api_url,omitempty"`
	// Backend is where the stored key lives: BackendKeyring or BackendFile.
	Backend string `json:"backend,omitempty"`
}

// ValidateProfileName rejects names that could not serve as a store key.
func ValidateProfileName(name string) error {
	if !profileNamePattern.MatchString(name) {
		return errors.New("profile names may contain letters, digits, '.', '_' and '-', and must start with a letter or digit")
	}
	return nil
}

// ConfigDir returns the configuration directory, respecting XDG_CONFIG_HOME.
func ConfigDir() (string, error) {
	base := os.Getenv("XDG_CONFIG_HOME")
	// The XDG spec says to ignore a relative value, which would otherwise make the
	// config depend on the current directory.
	if !filepath.IsAbs(base) {
		home, err := os.UserHomeDir()
		if err != nil {
			return "", fmt.Errorf("cannot determine home directory: %w", err)
		}
		base = filepath.Join(home, ".config")
	}
	return filepath.Join(base, configDirName), nil
}

// ConfigPath returns the path to the config file.
func ConfigPath() (string, error) {
	dir, err := ConfigDir()
	if err != nil {
		return "", err
	}
	return filepath.Join(dir, configFileName), nil
}

// LoadConfig reads the config file, returning defaults when it does not exist.
func LoadConfig() (*Config, error) {
	path, err := ConfigPath()
	if err != nil {
		return nil, err
	}
	cfg := &Config{ActiveProfile: DefaultProfile, Profiles: map[string]Profile{}}

	switch err := verifyPrivate(path); {
	case errors.Is(err, os.ErrNotExist):
		return cfg, nil
	case errors.Is(err, ErrInsecurePermissions):
		return nil, fmt.Errorf("refusing to use the config: %w: %s; %s", err, path, permissionFixHint(path))
	case err != nil:
		return nil, fmt.Errorf("failed to check config: %w", err)
	}
	data, err := os.ReadFile(path) //nolint:gosec // G304: the CLI's own config, whose path and permissions were checked above
	if err != nil {
		return nil, fmt.Errorf("failed to read config: %w", err)
	}
	if err := json.Unmarshal(data, cfg); err != nil {
		return nil, errors.New("failed to parse config: " + path + " is not valid JSON")
	}
	if cfg.Profiles == nil {
		cfg.Profiles = map[string]Profile{}
	}
	if cfg.ActiveProfile == "" {
		cfg.ActiveProfile = DefaultProfile
	}
	cfg.HasStraySecret = containsSecretField(data)
	return cfg, nil
}

// Save writes the config atomically with restrictive permissions.
func (c *Config) Save() error {
	path, err := ConfigPath()
	if err != nil {
		return err
	}
	data, err := json.MarshalIndent(c, "", "  ")
	if err != nil {
		return fmt.Errorf("failed to encode config: %w", err)
	}
	if err := writePrivateFile(path, append(data, '\n')); err != nil {
		return fmt.Errorf("failed to write config: %w", err)
	}
	c.HasStraySecret = false
	return nil
}

// Profile returns the named profile, or a zero Profile.
func (c *Config) Profile(name string) Profile { return c.Profiles[name] }

// containsSecretField looks for key-like fields at the top level and inside each
// profile. It reports only their presence and never keeps the values.
func containsSecretField(data []byte) bool {
	var raw struct {
		Profiles map[string]map[string]json.RawMessage `json:"profiles"`
	}
	var top map[string]json.RawMessage
	if json.Unmarshal(data, &top) != nil || json.Unmarshal(data, &raw) != nil {
		return false
	}
	if anySecretName(top) {
		return true
	}
	for _, profile := range raw.Profiles {
		if anySecretName(profile) {
			return true
		}
	}
	return false
}

func anySecretName(fields map[string]json.RawMessage) bool {
	for name := range fields {
		switch strings.ToLower(strings.NewReplacer("-", "", "_", "").Replace(name)) {
		case "apikey", "key", "token", "secret", "bearer", "password":
			return true
		}
	}
	return false
}
