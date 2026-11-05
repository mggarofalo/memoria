// Package sources holds what `memoria sources` keeps on this machine: which folders
// to send where, and a cursor saying what each server already has. None of it holds
// a key or any document's text.
package sources

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"

	"github.com/mggarofalo/memoria/cli/internal/auth"
)

const configFileName = "sources.json"

// A source's name is half of each document's identity on the server, so renaming one
// would store every document again under a new identity.
var namePattern = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`)

// Source is a local folder and the collection its documents go to.
type Source struct {
	Name       string `json:"name"`
	Path       string `json:"path"`
	Collection string `json:"collection"`
}

// Config is the list of configured sources.
type Config struct {
	Sources []Source `json:"sources"`
}

func configPath() (string, error) {
	dir, err := auth.ConfigDir()
	if err != nil {
		return "", err
	}
	return filepath.Join(dir, configFileName), nil
}

// Load reads the configured sources. No file means none are configured.
func Load() (*Config, error) {
	path, err := configPath()
	if err != nil {
		return nil, err
	}
	data, err := auth.ReadPrivateFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return &Config{}, nil
	}
	if err != nil {
		return nil, err
	}
	var c Config
	if err := json.Unmarshal(data, &c); err != nil {
		return nil, errors.New("failed to parse " + path + ": it is not valid JSON")
	}
	return &c, nil
}

// Save writes the configured sources.
func (c *Config) Save() error {
	path, err := configPath()
	if err != nil {
		return err
	}
	data, err := json.MarshalIndent(c, "", "  ")
	if err != nil {
		return fmt.Errorf("failed to encode the sources: %w", err)
	}
	return auth.WritePrivateFile(path, append(data, '\n'))
}

// Find returns the named source.
func (c *Config) Find(name string) (Source, bool) {
	for _, source := range c.Sources {
		if source.Name == name {
			return source, true
		}
	}
	return Source{}, false
}

// Add appends a source, refusing a name already in use.
func (c *Config) Add(source Source) error {
	if _, exists := c.Find(source.Name); exists {
		return errors.New("a source with that name already exists")
	}
	c.Sources = append(c.Sources, source)
	return nil
}

// Remove drops the named source, and reports whether it was there.
func (c *Config) Remove(name string) bool {
	for i, source := range c.Sources {
		if source.Name == name {
			c.Sources = append(c.Sources[:i], c.Sources[i+1:]...)
			return true
		}
	}
	return false
}

// ValidName reports whether a name can identify a source.
func ValidName(name string) bool {
	return namePattern.MatchString(name)
}

// ValidCollection applies the server's rule for collection names, for a better message.
func ValidCollection(name string) bool {
	return name != "" && name == strings.TrimSpace(name) && !strings.ContainsAny(name, "/\x00") && len(name) <= 200
}
