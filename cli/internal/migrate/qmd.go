// Package migrate reads a local QMD setup to plan moving it to a Memoria server. It only
// ever reads, and only the paths it is given: it has no default location, so nothing can
// touch a real corpus by accident. It never opens QMD's index.sqlite.
package migrate

import (
	"errors"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"go.yaml.in/yaml/v3"
)

// A QMD config is a short list of collections, so anything larger is not one.
const maxConfigBytes = 4 * 1024 * 1024

const defaultPattern = "**/*.md"

// Collection is one entry from QMD's index.yml. Its update command and contexts are not
// read: they can hold anything, and a plan has no use for them.
type Collection struct {
	Name    string
	Path    string
	Pattern string
	Ignore  []string
}

type qmdConfig struct {
	Collections map[string]struct {
		Path    string   `yaml:"path"`
		Pattern string   `yaml:"pattern"`
		Ignore  []string `yaml:"ignore"`
	} `yaml:"collections"`
}

// LoadCollections reads QMD's index.yml, in name order.
func LoadCollections(path string) ([]Collection, error) {
	file, err := os.Open(path) //nolint:gosec // G304: the QMD config the user names with --qmd-config
	if err != nil {
		return nil, errors.New("the QMD config cannot be opened")
	}
	defer func() { _ = file.Close() }()
	data, err := io.ReadAll(io.LimitReader(file, maxConfigBytes+1))
	if err != nil {
		return nil, errors.New("the QMD config cannot be read")
	}
	if len(data) > maxConfigBytes {
		return nil, errors.New("the QMD config is too large to be one")
	}
	var parsed qmdConfig
	if err := yaml.Unmarshal(data, &parsed); err != nil {
		// The parser's message can quote the file, which may hold an update command's secrets.
		return nil, errors.New("the QMD config is not valid YAML")
	}
	if len(parsed.Collections) == 0 {
		return nil, errors.New("the QMD config names no collections; check the spelling of its 'collections' key")
	}
	collections := make([]Collection, 0, len(parsed.Collections))
	for name, entry := range parsed.Collections {
		collections = append(collections, Collection{Name: name, Path: expandHome(entry.Path), Pattern: patternOr(entry.Pattern), Ignore: entry.Ignore})
	}
	sort.Slice(collections, func(i, j int) bool { return collections[i].Name < collections[j].Name })
	return collections, nil
}

func patternOr(pattern string) string {
	if pattern == "" {
		return defaultPattern
	}
	return pattern
}

func expandHome(path string) string {
	rest, ok := strings.CutPrefix(path, "~/")
	if !ok {
		return path
	}
	home, err := os.UserHomeDir()
	if err != nil {
		return path
	}
	return filepath.Join(home, filepath.FromSlash(rest))
}
