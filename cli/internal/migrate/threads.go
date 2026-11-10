package migrate

import (
	"bufio"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math"
	"os"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/klauspost/compress/zstd"
)

// compressedSuffix is how Codex names a rollout it has compressed with zstd, as it does to older ones, archived or not.
const compressedSuffix = ".jsonl.zst"

// maxZstdWindow bounds what one compressed transcript may ask for to decode. It is zstd's own default limit for a decoder.
const maxZstdWindow = 128 << 20

// IsTranscriptName says whether a file name is a raw Codex transcript: .jsonl, or .jsonl.zst once Codex has compressed it.
func IsTranscriptName(name string) bool {
	lower := strings.ToLower(name)
	return strings.HasSuffix(lower, ".jsonl") || strings.HasSuffix(lower, compressedSuffix)
}

// OpenTranscript opens a raw transcript as JSON lines. A compressed one is decompressed as it is read, never whole.
func OpenTranscript(file string) (io.ReadCloser, error) {
	handle, err := os.Open(file) //nolint:gosec // G304: a transcript under the folders the user names with --codex-sessions
	if err != nil {
		return nil, err
	}
	if !strings.HasSuffix(strings.ToLower(file), compressedSuffix) {
		return handle, nil
	}
	decoder, err := zstd.NewReader(handle, zstd.WithDecoderConcurrency(1), zstd.WithDecoderMaxWindow(maxZstdWindow), zstd.WithDecoderLowmem(true))
	if err != nil {
		_ = handle.Close()
		return nil, err
	}
	return &zstdTranscript{decoder: decoder, file: handle}, nil
}

// zstdTranscript is safe to close while it is being read, and more than once. net/http reads a request body on its own goroutine
// and can still be reading when the server has answered and the caller closes it, and a zstd decoder closed under a read crashes.
type zstdTranscript struct {
	mu      sync.Mutex
	decoder *zstd.Decoder
	file    *os.File
	closed  bool
}

func (z *zstdTranscript) Read(p []byte) (int, error) {
	z.mu.Lock()
	defer z.mu.Unlock()
	if z.closed {
		return 0, os.ErrClosed
	}
	return z.decoder.Read(p)
}

func (z *zstdTranscript) Close() error {
	z.mu.Lock()
	defer z.mu.Unlock()
	if z.closed {
		return nil
	}
	z.closed = true
	z.decoder.Close()
	return z.file.Close()
}

// DefaultMaxSessionBytes is the server's default Memoria:Ingest:MaxSessionBytes: the largest transcript one session upload may
// send. A larger one is refused with 413, so it is not sent at all.
const DefaultMaxSessionBytes int64 = 256 << 20

// TranscriptSize is how many bytes sending a transcript sends: its size, or, for a compressed one, what it decompresses to,
// counted as it streams.
func TranscriptSize(file string) (int64, error) {
	if !strings.HasSuffix(strings.ToLower(file), compressedSuffix) {
		info, err := os.Stat(file)
		if err != nil {
			return 0, err
		}
		return info.Size(), nil
	}
	stream, err := OpenTranscript(file)
	if err != nil {
		return 0, err
	}
	defer func() { _ = stream.Close() }()
	return io.Copy(io.Discard, stream)
}

// TranscriptSizeUpTo is TranscriptSize, without decompressing more than one byte past limit. exact is false when a compressed
// one was cut short there, and then the size is limit plus one: all that is known is that it is over.
func TranscriptSizeUpTo(file string, limit int64) (int64, bool, error) {
	if !strings.HasSuffix(strings.ToLower(file), compressedSuffix) || limit >= math.MaxInt64-1 {
		size, err := TranscriptSize(file)
		return size, true, err
	}
	stream, err := OpenTranscript(file)
	if err != nil {
		return 0, false, err
	}
	defer func() { _ = stream.Close() }()
	size, err := io.CopyN(io.Discard, stream, max(limit, 0)+1)
	if errors.Is(err, io.EOF) {
		return size, true, nil
	}
	return size, false, err
}

// ErrSeveralThreads is two files holding the same thread of a session, which this does not choose between.
var ErrSeveralThreads = errors.New("more than one transcript holds the same thread")

// Thread is one raw transcript of a Codex session: the session's own, or a subagent's, which Codex writes to a file of its own whose
// session_meta.id is the subagent's and whose session_meta.session_id is the session it serves. Each goes to the server as its own
// session, under its own id, as the server checks.
type Thread struct {
	ID, File string
	Messages []Message
	// order is where the Codex hook exporter puts this thread's messages in a session's export: its start time, then its path.
	order [2]string
	// key is the session the exporter files this thread under.
	key string
}

// threadHead is what a transcript's first session_meta says: its own id, and the session it belongs to.
type threadHead struct {
	id, root, file string
}

// headOf reads a transcript's first session_meta, as the server does.
func headOf(file string) (threadHead, bool) {
	stream, err := OpenTranscript(file)
	if err != nil {
		return threadHead{}, false
	}
	defer func() { _ = stream.Close() }()
	decoder := json.NewDecoder(stream)
	for range maxMetaRecords {
		var record struct {
			Type    string `json:"type"`
			Payload struct {
				ID        any `json:"id"`
				SessionID any `json:"session_id"`
			} `json:"payload"`
		}
		if decoder.Decode(&record) != nil {
			return threadHead{}, false
		}
		if record.Type == "session_meta" {
			id, _ := record.Payload.ID.(string)
			return threadHead{id: id, root: sessionKey(record.Payload.SessionID, record.Payload.ID), file: file}, true
		}
	}
	return threadHead{}, false
}

// sessionKey is the session a session_meta files its thread under, as the exporter reads it: session_id, or else id. Only a string
// is read, so a value of another type, which the exporter would print, is no key at all and its thread is left out.
func sessionKey(sessionID, id any) string {
	if text, ok := sessionID.(string); ok && text != "" {
		return text
	}
	text, _ := id.(string)
	return text
}

// Family is a session's threads in the order the Codex hook exporter wrote them into one export: own is the session's own
// transcript, and the rest are every transcript whose first session_meta names the session as theirs. The exporter files a thread by
// its last session_meta, so one whose last names another session is left out, as the exporter left it out. A thread that names the
// session only in a later session_meta is not looked for, so an export holding one is not covered, and is not sent.
func (t *Transcripts) Family(id, own string) ([]Thread, error) {
	files := []string{own}
	for _, head := range t.heads()[id] {
		if !strings.EqualFold(head.file, own) {
			files = append(files, head.file)
		}
	}
	var threads []Thread
	seen := map[string]bool{}
	for _, file := range files {
		thread, err := readThread(file)
		if err != nil {
			return nil, err
		}
		if file != own && thread.key != id {
			continue
		}
		if seen[thread.ID] {
			return nil, ErrSeveralThreads
		}
		seen[thread.ID] = true
		threads = append(threads, thread)
	}
	sort.SliceStable(threads, func(i, j int) bool {
		return threads[i].order[0] < threads[j].order[0] || (threads[i].order[0] == threads[j].order[0] && threads[i].order[1] < threads[j].order[1])
	})
	return threads, nil
}

// heads reads every transcript's first session_meta once, by the session each belongs to.
func (t *Transcripts) heads() map[string][]threadHead {
	if t.byRoot != nil {
		return t.byRoot
	}
	t.byRoot = map[string][]threadHead{}
	for _, files := range t.ByID {
		for _, file := range files {
			if head, ok := headOf(file); ok && head.root != "" {
				t.byRoot[head.root] = append(t.byRoot[head.root], head)
			}
		}
	}
	return t.byRoot
}

// readThread reads a whole transcript the way the Codex hook exporter does: its messages, the session it files them under, and
// where they go among that session's other threads.
func readThread(file string) (Thread, error) {
	stream, err := OpenTranscript(file)
	if err != nil {
		return Thread{}, err
	}
	defer func() { _ = stream.Close() }()
	scanner := bufio.NewScanner(stream)
	scanner.Buffer(make([]byte, 0, 64*1024), maxTranscriptLine)
	var reading threadReading
	for scanner.Scan() {
		reading.add(scanner.Bytes())
	}
	if err := scanner.Err(); err != nil {
		return Thread{}, err
	}
	head, _ := headOf(file)
	start := reading.metaStart
	if start == "" && !reading.first.IsZero() {
		start = pythonISO(reading.first)
	}
	return Thread{ID: head.id, File: file, Messages: reading.messages, order: [2]string{start, file}, key: reading.key}, nil
}

type threadReading struct {
	messages  []Message
	key       string
	metaStart string
	first     time.Time
}

func (r *threadReading) add(line []byte) {
	var record struct {
		Type      string          `json:"type"`
		Timestamp any             `json:"timestamp"`
		Payload   json.RawMessage `json:"payload"`
	}
	if json.Unmarshal(line, &record) != nil {
		return
	}
	if stamp, ok := parseStamp(record.Timestamp); ok && (r.first.IsZero() || stamp.Before(r.first)) {
		r.first = stamp
	}
	if record.Type == "session_meta" {
		r.meta(record.Payload)
		return
	}
	if message, ok := messageOf(line); ok {
		r.messages = append(r.messages, message)
	}
}

// meta keeps the last session_meta, as the exporter does: the session it files the thread under, and the start time it sorts by.
func (r *threadReading) meta(payload json.RawMessage) {
	var meta struct {
		ID        any `json:"id"`
		SessionID any `json:"session_id"`
		Timestamp any `json:"timestamp"`
	}
	if json.Unmarshal(payload, &meta) != nil {
		return
	}
	r.key = sessionKey(meta.SessionID, meta.ID)
	r.metaStart = ""
	if stamp, ok := parseStamp(meta.Timestamp); ok {
		r.metaStart = pythonISO(stamp)
	}
}

// parseStamp reads a timestamp as the exporter's datetime.fromisoformat does for what Codex writes. One it reads otherwise only
// changes where a thread sorts, and a thread out of place makes the export's body not match, so it is not sent.
func parseStamp(value any) (time.Time, bool) {
	text, ok := value.(string)
	if !ok || text == "" {
		return time.Time{}, false
	}
	stamp, err := time.Parse(time.RFC3339Nano, text)
	return stamp, err == nil
}

// pythonISO is Python's datetime.isoformat for an aware time: microseconds only when there are some, and the offset as +HH:MM.
func pythonISO(t time.Time) string {
	text := t.Format("2006-01-02T15:04:05")
	if micro := t.Nanosecond() / 1000; micro != 0 {
		text += fmt.Sprintf(".%06d", micro)
	}
	_, offset := t.Zone()
	sign := "+"
	if offset < 0 {
		sign, offset = "-", -offset
	}
	text += fmt.Sprintf("%s%02d:%02d", sign, offset/3600, offset/60%60)
	if offset%60 != 0 {
		text += fmt.Sprintf(":%02d", offset%60)
	}
	return text
}
