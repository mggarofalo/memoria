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
	// RerenderTooLarge is a transcript larger than the server's limit on one session upload, so it is not sent: the server would
	// refuse it whole, and the export stays the only copy.
	RerenderTooLarge = "transcript_too_large"
	// RerenderUncounted is an export that does not say how many messages it reaches, so nothing can show the transcript has them all.
	RerenderUncounted = "export_uncounted"
	// RerenderAcceptedUncounted is an uncounted export sent anyway on the owner's --accept-uncounted. It is named in the report.
	RerenderAcceptedUncounted = "accepted_uncounted"
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

// Message is one message of a transcript, as the exporter and the server both read it.
type Message struct {
	Role, Text string
}

// TranscriptMessages reads a Codex transcript's messages the way its exporter does, which is also how the server counts them: a
// response_item message from the user or the assistant with text in it, the text of each block stripped and joined by a blank
// line. A line it cannot read is left out, so a damaged transcript reads as shorter, never longer.
func TranscriptMessages(file string) ([]Message, error) {
	handle, err := OpenTranscript(file)
	if err != nil {
		return nil, err
	}
	defer func() { _ = handle.Close() }()
	scanner := bufio.NewScanner(handle)
	scanner.Buffer(make([]byte, 0, 64*1024), maxTranscriptLine)
	var messages []Message
	for scanner.Scan() {
		if message, ok := messageOf(scanner.Bytes()); ok {
			messages = append(messages, message)
		}
	}
	return messages, scanner.Err()
}

// CountMessages is how many messages the server will find in a transcript.
func CountMessages(file string) (int64, error) {
	messages, err := TranscriptMessages(file)
	return int64(len(messages)), err
}

// maxTranscriptLine is the server's own limit on a line. A longer one is dropped there too.
const maxTranscriptLine = 16 * 1024 * 1024

func messageOf(line []byte) (Message, bool) {
	var record struct {
		Type    string `json:"type"`
		Payload struct {
			Type    string          `json:"type"`
			Role    string          `json:"role"`
			Content json.RawMessage `json:"content"`
		} `json:"payload"`
	}
	if json.Unmarshal(line, &record) != nil || record.Type != "response_item" || record.Payload.Type != "message" {
		return Message{}, false
	}
	if record.Payload.Role != "user" && record.Payload.Role != "assistant" {
		return Message{}, false
	}
	text := blockText(record.Payload.Content)
	return Message{Role: record.Payload.Role, Text: text}, text != ""
}

// blockText is the exporter's text_blocks: a string stripped, or the stripped text blocks joined by a blank line.
func blockText(content json.RawMessage) string {
	var text string
	if json.Unmarshal(content, &text) == nil {
		return strip(text)
	}
	var blocks []struct {
		Type string `json:"type"`
		Text string `json:"text"`
	}
	if json.Unmarshal(content, &blocks) != nil {
		return ""
	}
	var parts []string
	for _, block := range blocks {
		if (block.Type == "input_text" || block.Type == "output_text" || block.Type == "text") && strip(block.Text) != "" {
			parts = append(parts, strip(block.Text))
		}
	}
	return strings.Join(parts, "\n\n")
}

// ExporterFormat is the export body this can read: render_chunk in the Codex exporter of agent-skills at 3478baa, in
// codex/skills/recall/scripts/export_sessions.py. Each message is "## User" or "## Assistant", a blank line, its text and a blank line,
// after the frontmatter and a "# " title. The Codex hook exporter that writes ~/.codex/exports/sessions/<date>_<session>.md writes the
// same body, with the messages of every thread of the session, its subagents' too, one thread after another.
const ExporterFormat = "agent-skills 3478baa codex/skills/recall/scripts/export_sessions.py render_chunk"

// BodyCovered says whether an export's body is, exactly, its threads' first messages as those exporters render them: each thread's
// messages in turn, as far as the export reaches in each. Matching the text itself is what makes a heading-like line inside a message
// part of that message, not one of its own. Anything else, such as another heading, a message no thread holds, a title no exporter
// would write for those messages, or text left over, is not shown to be covered.
func BodyCovered(export []byte, threads ...[]Message) bool {
	title, rest, found := bodyOf(exportText(export))
	if !found {
		return false
	}
	var matched []Message
	for _, messages := range threads {
		for _, message := range messages {
			block := "## " + labelOf(message.Role) + "\n\n" + message.Text + "\n"
			if rest == "" || !strings.HasPrefix(rest, block) {
				break
			}
			rest = strings.TrimPrefix(rest[len(block):], "\n")
			matched = append(matched, message)
		}
	}
	return rest == "" && titleFits(title, matched)
}

// exportText undoes what a Python exporter writing in text mode on Windows does, which is to write each "\n" as "\r\n". An export is
// read that way only when every "\n" in it is written so, and then undoing it is exact: the file is the rendering, byte for byte, with
// each "\n" written as "\r\n", so a "\r\n" in a message's own text was written as "\r\r\n" and reads back as "\r\n". Any other export
// is read as it is.
func exportText(export []byte) string {
	text := string(export)
	if strings.Count(text, "\n") == strings.Count(text, "\r\n") {
		return strings.ReplaceAll(text, "\r\n", "\n")
	}
	return text
}

// titleFits says whether a title is one an exporter writes for these messages, so it holds nothing they do not. The hook exporter
// writes the first line of the first user message, up to 100 characters, or "Codex chat" if there is none. render_chunk writes that
// line with each "#" taken out and stripped, or no title if there is no user message.
func titleFits(title string, messages []Message) bool {
	for _, message := range messages {
		if message.Role == "user" {
			line := firstLine(message.Text)
			return title == "# "+line || title == "# "+strip(strings.ReplaceAll(line, "#", ""))
		}
	}
	return title == "" || title == "# Codex chat"
}

// firstLine is Python's text.splitlines()[0][:100]: up to the first line boundary Python knows, then at most 100 characters.
func firstLine(text string) string {
	if end := strings.IndexFunc(text, isPythonLineBreak); end >= 0 {
		text = text[:end]
	}
	if runes := []rune(text); len(runes) > 100 {
		return string(runes[:100])
	}
	return text
}

func isPythonLineBreak(r rune) bool {
	switch r {
	case '\n', '\r', '\v', '\f', 0x1c, 0x1d, 0x1e, 0x85, 0x2028, 0x2029:
		return true
	}
	return false
}

func labelOf(role string) string {
	if role == "user" {
		return "User"
	}
	return "Assistant"
}

// bodyOf is the title the exporter writes before the first message, if there is one, and the text after it and the frontmatter.
func bodyOf(text string) (string, string, bool) {
	if !strings.HasPrefix(text, "---\n") {
		return "", "", false
	}
	end := strings.Index(text[4:], "\n---\n")
	if end < 0 {
		return "", "", false
	}
	body := strings.TrimPrefix(text[4+end+len("\n---\n"):], "\n")
	if !strings.HasPrefix(body, "# ") {
		return "", body, true
	}
	title, rest, found := strings.Cut(body, "\n")
	if !found {
		return "", "", false
	}
	return title, strings.TrimPrefix(rest, "\n"), true
}

// strip trims what Python's str.strip trims, which is more than Go's TrimSpace, so a message of only those is not counted.
func strip(text string) string {
	return strings.TrimFunc(text, func(r rune) bool { return unicode.IsSpace(r) || (r >= 0x1c && r <= 0x1f) })
}

// Transcripts is the raw Codex transcripts found, by the session id in their file names, in lower case.
type Transcripts struct {
	ByID       map[string][]string
	Unreadable int
	// byRoot is every transcript by the session its first session_meta files it under, read when Family first needs it.
	byRoot map[string][]threadHead
}

// FindTranscripts lists the .jsonl and .jsonl.zst files under each folder, as plan counts them, by the id in each name.
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
		if id != "" && IsTranscriptName(name) {
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
	head, ok := headOf(file)
	return ok && head.id == id
}

// RerenderProblem is one document in the private report, the only record that names one.
type RerenderProblem struct {
	Collection string `json:"collection"`
	Path       string `json:"path"`
	Status     string `json:"status"`
	Reason     string `json:"reason,omitempty"`
	// Thread is the subagent thread of the session that did not go, when it is not the session's own.
	Thread string `json:"thread,omitempty"`
}

// RerenderSummary is one collection's re-render. It names no file.
type RerenderSummary struct {
	Name   string `json:"name"`
	Target string `json:"target"`
	// OverCap is the documents too large to send as they are, which are what this re-renders.
	OverCap   int `json:"overCap"`
	WouldSend int `json:"wouldSend,omitempty"`
	// Threads is the raw transcripts that go for the documents that go: one each, and one more for each subagent thread of a
	// session whose export holds that thread's messages too.
	Threads int `json:"threads"`
	Sent    int `json:"sent"`
	Already int `json:"alreadySent"`
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
	// TooLarge is documents with a transcript over the session upload limit, which are not sent.
	TooLarge int `json:"transcriptTooLarge"`
	// Uncounted is exports that do not say how many messages they reach, and whose body does not show the transcript has them all.
	Uncounted int `json:"exportUncounted"`
	// AcceptedUncounted is those sent anyway, because the owner passed --accept-uncounted after looking. They are in Sent too.
	AcceptedUncounted int `json:"acceptedUncounted"`
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
	return s.Partial + s.RefusedTotal() + s.NoSessionID + s.NoTranscript + s.Several + s.Unreadable + s.NotReached + s.Shorter + s.TooLarge + s.Uncounted
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
