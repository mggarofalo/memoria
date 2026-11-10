package migrate

import (
	"bufio"
	"bytes"
	"encoding/json"
	"errors"
	"io/fs"
	"os"
	"path"
	"path/filepath"
	"regexp"
	"strings"
)

// The ways a re-render can end for one document. Each is a count in RerenderSummary.
const (
	RerenderSent        = "sent"
	RerenderAlready     = "already_sent"
	RerenderPartial     = "partial"
	RerenderRefused     = "refused"
	RerenderNoSessionID = "no_session_id"
	// RerenderNoTranscript is a document whose raw transcript is not here. The over-cap export is then its only copy.
	RerenderNoTranscript = "no_transcript"
	// RerenderSeveral is more than one transcript naming the session, which this does not choose between.
	RerenderSeveral    = "several_transcripts"
	RerenderUnreadable = "unreadable"
	RerenderNotReached = "not_reached"
)

// maxMetaRecords is how far into a transcript its session_meta is looked for. Codex writes it first.
const maxMetaRecords = 50

// frontmatterLimit is how much of a document is read for its session_id.
const frontmatterLimit = 64 * 1024

var uuidPattern = regexp.MustCompile(`(?i)[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}`)

// SessionIDOf is the session_id in a document's frontmatter, or "" if it names none.
func SessionIDOf(data []byte) string {
	data = data[:min(len(data), frontmatterLimit)]
	scanner := bufio.NewScanner(bytes.NewReader(data))
	scanner.Buffer(make([]byte, 0, 4096), frontmatterLimit)
	if !scanner.Scan() || strings.TrimSpace(scanner.Text()) != "---" {
		return ""
	}
	for scanner.Scan() {
		line := strings.TrimRight(scanner.Text(), "\r")
		if strings.TrimSpace(line) == "---" {
			return ""
		}
		if value, found := strings.CutPrefix(line, "session_id:"); found {
			return strings.Trim(strings.TrimSpace(value), `"'`)
		}
	}
	return ""
}

// Transcripts is the raw Codex transcripts found, by the session id in their file names, in lower case.
type Transcripts struct {
	ByID       map[string][]string
	Unreadable int
}

// FindTranscripts lists the .jsonl files under each folder, as plan counts them, by the id in each name.
func FindTranscripts(dirs []string) (*Transcripts, error) {
	found := &Transcripts{ByID: map[string][]string{}}
	for _, dir := range dirs {
		if err := found.add(dir); err != nil {
			return nil, err
		}
	}
	return found, nil
}

func (t *Transcripts) add(dir string) error {
	root, err := os.OpenRoot(dir)
	if err != nil {
		return errors.New("the raw sessions folder " + dir + " cannot be opened")
	}
	defer func() { _ = root.Close() }()
	return walk(root, func(name string, _ fs.FileInfo) {
		id := uuidPattern.FindString(path.Base(name))
		if id != "" && strings.EqualFold(path.Ext(name), ".jsonl") {
			key := strings.ToLower(id)
			t.ByID[key] = append(t.ByID[key], filepath.Join(dir, filepath.FromSlash(name)))
		}
	}, func(string) { t.Unreadable++ })
}

// Matching is the transcripts whose session_meta names this very session. A file only named like it is not one.
func (t *Transcripts) Matching(id string) []string {
	var matching []string
	for _, file := range t.ByID[strings.ToLower(id)] {
		if NamesSession(file, id) {
			matching = append(matching, file)
		}
	}
	return matching
}

// NamesSession reads a transcript's session_meta, as the server does, and says whether its id is this one.
func NamesSession(file, id string) bool {
	handle, err := os.Open(file)
	if err != nil {
		return false
	}
	defer func() { _ = handle.Close() }()
	decoder := json.NewDecoder(handle)
	for range maxMetaRecords {
		var record struct {
			Type    string `json:"type"`
			Payload struct {
				ID string `json:"id"`
			} `json:"payload"`
		}
		if decoder.Decode(&record) != nil {
			return false
		}
		if record.Type == "session_meta" {
			return record.Payload.ID == id
		}
	}
	return false
}

// RerenderProblem is one document in the private report, the only record that names one.
type RerenderProblem struct {
	Collection string `json:"collection"`
	Path       string `json:"path"`
	Status     string `json:"status"`
	Reason     string `json:"reason,omitempty"`
}

// RerenderSummary is one collection's re-render. It names no file.
type RerenderSummary struct {
	Name   string `json:"name"`
	Target string `json:"target"`
	// OverCap is the documents too large to send as they are, which are what this re-renders.
	OverCap   int `json:"overCap"`
	WouldSend int `json:"wouldSend,omitempty"`
	Sent      int `json:"sent"`
	Already   int `json:"alreadySent"`
	// Partial is a session the server stored only some of, which it says why for in the report.
	Partial      int            `json:"partial"`
	Refused      map[string]int `json:"refused"`
	NoSessionID  int            `json:"noSessionId"`
	NoTranscript int            `json:"noTranscript"`
	Several      int            `json:"severalTranscripts"`
	Unreadable   int            `json:"unreadable"`
	NotReached   int            `json:"notReached"`
}

// RefusedTotal is every session the server refused.
func (s *RerenderSummary) RefusedTotal() int {
	total := 0
	for _, count := range s.Refused {
		total += count
	}
	return total
}

// NotHeld is every over-cap document that is not now held through its session.
func (s *RerenderSummary) NotHeld() int {
	return s.Partial + s.RefusedTotal() + s.NoSessionID + s.NoTranscript + s.Several + s.Unreadable + s.NotReached
}

// Accounted reports whether every over-cap document is in exactly one count.
func (s *RerenderSummary) Accounted() bool {
	return s.WouldSend+s.Sent+s.Already+s.NotHeld() == s.OverCap
}

// RerenderResult is what a run says on stdout.
type RerenderResult struct {
	DryRun      bool              `json:"dryRun"`
	Collections []RerenderSummary `json:"collections"`
	ReportFile  string            `json:"reportFile,omitempty"`
}
