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
	"strconv"
	"strings"
	"unicode"
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
	// RerenderShorter is a transcript with fewer messages than its exports reach. The exports are the only copy of the rest.
	RerenderShorter = "transcript_shorter"
	// RerenderUncounted is an export that does not say how many messages it reaches, so nothing can show the transcript has them all.
	RerenderUncounted = "export_uncounted"
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

var (
	messageStartField = regexp.MustCompile(`(?m)^message_start:[ \t]*(\d{1,18})[ \t]*\r?$`)
	messageEndField   = regexp.MustCompile(`(?m)^message_end:[ \t]*(\d{1,18})[ \t]*\r?$`)
	messageCountField = regexp.MustCompile(`(?m)^message_count:[ \t]*(\d{1,18})[ \t]*\r?$`)
)

// ReachOf is how many of its session's messages come up to the end of an export, read the way the server reads it: message_start
// plus message_count, or else message_end plus one. False when the export does not say.
func ReachOf(data []byte) (int64, bool) {
	block := frontmatterBlock(data[:min(len(data), frontmatterLimit)])
	if count := messageCountField.FindSubmatch(block); count != nil {
		return number(messageStartField.FindSubmatch(block)) + number(count), true
	}
	if end := messageEndField.FindSubmatch(block); end != nil {
		return number(end) + 1, true
	}
	return 0, false
}

func number(match [][]byte) int64 {
	if match == nil {
		return 0
	}
	value, _ := strconv.ParseInt(string(match[1]), 10, 64)
	return value
}

func frontmatterBlock(data []byte) []byte {
	text := bytes.ReplaceAll(data, []byte("\r\n"), []byte("\n"))
	if !bytes.HasPrefix(text, []byte("---\n")) {
		return nil
	}
	end := bytes.Index(text[4:], []byte("\n---"))
	if end < 0 {
		return nil
	}
	return text[:4+end+1]
}

// CountMessages counts a Codex transcript's messages the way the server does: a response_item message from the user or the
// assistant with text in it. A line it cannot read is not counted, so a damaged transcript reads as shorter, never longer.
func CountMessages(file string) (int64, error) {
	handle, err := os.Open(file)
	if err != nil {
		return 0, err
	}
	defer func() { _ = handle.Close() }()
	scanner := bufio.NewScanner(handle)
	scanner.Buffer(make([]byte, 0, 64*1024), maxTranscriptLine)
	var count int64
	for scanner.Scan() {
		if isMessage(scanner.Bytes()) {
			count++
		}
	}
	return count, scanner.Err()
}

// maxTranscriptLine is the server's own limit on a line. A longer one is dropped there too.
const maxTranscriptLine = 16 * 1024 * 1024

func isMessage(line []byte) bool {
	var record struct {
		Type    string `json:"type"`
		Payload struct {
			Type    string          `json:"type"`
			Role    string          `json:"role"`
			Content json.RawMessage `json:"content"`
		} `json:"payload"`
	}
	if json.Unmarshal(line, &record) != nil || record.Type != "response_item" || record.Payload.Type != "message" {
		return false
	}
	return (record.Payload.Role == "user" || record.Payload.Role == "assistant") && hasText(record.Payload.Content)
}

func hasText(content json.RawMessage) bool {
	var text string
	if json.Unmarshal(content, &text) == nil {
		return strip(text) != ""
	}
	var blocks []struct {
		Type string `json:"type"`
		Text string `json:"text"`
	}
	if json.Unmarshal(content, &blocks) != nil {
		return false
	}
	for _, block := range blocks {
		if (block.Type == "input_text" || block.Type == "output_text" || block.Type == "text") && strip(block.Text) != "" {
			return true
		}
	}
	return false
}

// strip trims what Python's str.strip trims, which is more than Go's TrimSpace, so a message of only those is not counted.
func strip(text string) string {
	return strings.TrimFunc(text, func(r rune) bool { return unicode.IsSpace(r) || (r >= 0x1c && r <= 0x1f) })
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
			t.note(strings.ToLower(id), filepath.Join(dir, filepath.FromSlash(name)))
		}
	}, func(string) { t.Unreadable++ })
}

// note keeps one entry per real file, so a folder given twice, or a folder and its parent, finds a transcript once.
func (t *Transcripts) note(key, file string) {
	real, err := filepath.EvalSymlinks(file)
	if err != nil {
		real = file
	}
	if absolute, err := filepath.Abs(real); err == nil {
		real = absolute
	}
	for _, known := range t.ByID[key] {
		if strings.EqualFold(known, real) {
			return
		}
	}
	t.ByID[key] = append(t.ByID[key], real)
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
	// Shorter is transcripts with fewer messages than their exports reach, which are not sent.
	Shorter int `json:"transcriptShorter"`
	// Uncounted is exports that do not say how many messages they reach, which are not sent.
	Uncounted int `json:"exportUncounted"`
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
	return s.Partial + s.RefusedTotal() + s.NoSessionID + s.NoTranscript + s.Several + s.Unreadable + s.NotReached + s.Shorter + s.Uncounted
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
