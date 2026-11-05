package sources

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"time"

	"github.com/mggarofalo/memoria/cli/internal/auth"
)

const (
	stateDirName = "sources-state"

	// KindNew is a file the server has not been sent.
	KindNew = "new"
	// KindChanged is a file whose content differs from what was sent.
	KindChanged = "changed"
)

// Entry is what a server was last sent for one file. Content is compared by hash, not
// by modification time: touching a file sends nothing, and an edit inside one tick of
// the clock is still seen.
type Entry struct {
	SHA256  string    `json:"sha256"`
	Updated time.Time `json:"updated"`
}

// State is the cursor for one source on one server, so that pointing the CLI at
// another server starts from nothing rather than skipping what that server lacks.
type State struct {
	Origin     string           `json:"origin"`
	Source     string           `json:"source"`
	Collection string           `json:"collection"`
	LastSync   *time.Time       `json:"last_sync,omitempty"`
	Files      map[string]Entry `json:"files"`

	path string
}

// Change is a file that needs sending.
type Change struct {
	Path string `json:"path"`
	Kind string `json:"kind"`
}

// Plan is the difference between a folder and its cursor.
type Plan struct {
	Changes   []Change
	Unchanged int
	Missing   int
	Skipped   []Skipped
}

func statePath(origin string, source Source) (string, error) {
	dir, err := auth.ConfigDir()
	if err != nil {
		return "", err
	}
	sum := sha256.Sum256(fmt.Appendf(nil, "%d:%s%d:%s%s", len(origin), origin, len(source.Name), source.Name, source.Collection))
	return filepath.Join(dir, stateDirName, hex.EncodeToString(sum[:16])+".json"), nil
}

// LoadState reads the cursor. The boolean says whether one exists, which is what
// separates a first run from a later one. A damaged cursor is an error, not an empty
// one: treating it as empty would quietly turn a sync back into a first run.
func LoadState(origin string, source Source) (*State, bool, error) {
	path, err := statePath(origin, source)
	if err != nil {
		return nil, false, err
	}
	fresh := &State{Origin: origin, Source: source.Name, Collection: source.Collection, Files: map[string]Entry{}, path: path}
	data, err := auth.ReadPrivateFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return fresh, false, nil
	}
	if err != nil {
		return nil, false, err
	}
	if err := json.Unmarshal(data, fresh); err != nil {
		return nil, false, errors.New("the cursor at " + path + " is damaged. Delete it to start this source again; the next run will be a dry run")
	}
	if fresh.Files == nil {
		fresh.Files = map[string]Entry{}
	}
	return fresh, true, nil
}

// Save writes the cursor atomically, so an interruption leaves the last good one.
func (s *State) Save() error {
	data, err := json.MarshalIndent(s, "", "  ")
	if err != nil {
		return fmt.Errorf("failed to encode the cursor: %w", err)
	}
	if err := auth.WritePrivateFile(s.path, append(data, '\n')); err != nil {
		return fmt.Errorf("failed to write the cursor: %w", err)
	}
	return nil
}

// Plan compares the listed files with the cursor. A file the cursor knows and the
// folder lacks only counts as missing, as sync never deletes: an unmounted drive looks
// exactly like every file having been deleted.
func (s *State) Plan(tree *Tree, paths []string) Plan {
	plan := Plan{}
	seen := make(map[string]bool, len(paths))
	for _, name := range paths {
		seen[name] = true
		content, err := tree.Read(name)
		if err != nil {
			plan.Skipped = append(plan.Skipped, Skipped{Path: name, Reason: skipReason(err)})
			continue
		}
		s.classify(&plan, name, content.SHA256)
	}
	for name := range s.Files {
		if !seen[name] {
			plan.Missing++
		}
	}
	return plan
}

func (s *State) classify(plan *Plan, name, sum string) {
	entry, known := s.Files[name]
	switch {
	case !known:
		plan.Changes = append(plan.Changes, Change{Path: name, Kind: KindNew})
	case entry.SHA256 != sum:
		plan.Changes = append(plan.Changes, Change{Path: name, Kind: KindChanged})
	default:
		plan.Unchanged++
	}
}

func skipReason(err error) string {
	if errors.Is(err, errTooLarge) || errors.Is(err, errNotText) || errors.Is(err, errNotRegular) {
		return err.Error()
	}
	return "it could not be read"
}

// Version is the `updated` to send for a file. It is normally the modification time.
// Content that changed but carries an older time, such as a restored backup, is still
// the latest edit here, so it goes one second past what was sent, as the server refuses
// anything older than what it holds.
func (s *State) Version(name string, modTime time.Time) time.Time {
	version := modTime.UTC().Truncate(time.Millisecond)
	if entry, known := s.Files[name]; known && !version.After(entry.Updated) {
		return entry.Updated.Add(time.Second)
	}
	return version
}

// Record notes that the server accepted this content.
func (s *State) Record(name, sum string, version time.Time) {
	s.Files[name] = Entry{SHA256: sum, Updated: version}
}

// Report says what one source's run found and did. It holds counts and relative paths
// and never any text.
type Report struct {
	Name       string         `json:"name"`
	Collection string         `json:"collection"`
	DryRun     bool           `json:"dryRun"`
	WouldSend  []Change       `json:"wouldSend,omitempty"`
	Sent       int            `json:"sent"`
	Failed     int            `json:"failed"`
	Remaining  int            `json:"remaining"`
	Unchanged  int            `json:"unchanged"`
	Missing    int            `json:"missingLocally"`
	Skipped    []Skipped      `json:"skipped,omitempty"`
	Redactions map[string]int `json:"redactions,omitempty"`
	Unredacted int            `json:"storedUnredacted,omitempty"`
}

// Listing describes a configured source for `sources list`.
type Listing struct {
	Source
	Tracked  *int       `json:"tracked"`
	LastSync *time.Time `json:"lastSync"`
}
