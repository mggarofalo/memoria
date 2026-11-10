package migrate

import (
	"bytes"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/klauspost/compress/zstd"
)

const (
	rootID  = "01a0951d-0000-7000-8000-000000000001"
	childID = "01a0951d-0000-7000-8000-000000000002"
	laterID = "01a0951d-0000-7000-8000-000000000003"
)

// rollout is a synthetic Codex transcript: its session_meta, then one message for each text, user and assistant in turn.
func rollout(id, root, start string, texts ...string) string {
	session := ""
	if root != "" {
		session = `,"session_id":"` + root + `"`
	}
	text := `{"timestamp":"` + start + `","type":"session_meta","payload":{"id":"` + id + `"` + session + `,"timestamp":"` + start + `","cwd":"/w"}}` + "\n"
	for i, said := range texts {
		role := []string{"user", "assistant"}[i%2]
		text += `{"timestamp":"` + start + `","type":"response_item","payload":{"type":"message","role":"` + role + `","content":[{"type":"input_text","text":` + quoted(said) + `}]}}` + "\n"
	}
	return text
}

func quoted(text string) string {
	replacer := strings.NewReplacer(`\`, `\\`, `"`, `\"`, "\n", `\n`, "\r", `\r`)
	return `"` + replacer.Replace(text) + `"`
}

func compressed(t *testing.T, text string) []byte {
	t.Helper()
	var out bytes.Buffer
	writer, err := zstd.NewWriter(&out)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := io.WriteString(writer, text); err != nil {
		t.Fatal(err)
	}
	if err := writer.Close(); err != nil {
		t.Fatal(err)
	}
	return out.Bytes()
}

func put(t *testing.T, file string, data []byte) string {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(file), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(file, data, 0o600); err != nil {
		t.Fatal(err)
	}
	return file
}

// hookExport is what the Codex hook exporter writes for a session's threads, already in its order, before any newline translation.
func hookExport(id string, threads ...[]Message) string {
	var all []Message
	for _, thread := range threads {
		all = append(all, thread...)
	}
	title := "Codex chat"
	for _, message := range all {
		if message.Role == "user" {
			title = firstLine(message.Text)
			break
		}
	}
	lines := []string{"---", `session_id: "` + id + `"`, `date: "2026-09-12"`, "tags:", `  - "agent/codex"`, "---", "", "# " + title, ""}
	for _, message := range all {
		lines = append(lines, "## "+labelOf(message.Role), "", message.Text, "")
	}
	return strings.Join(lines, "\n")
}

func TestACompressedTranscriptIsFoundAndReadAsItsLines(t *testing.T) {
	dir := t.TempDir()
	text := rollout(testID, "", "2026-09-12T10:15:09.729Z", "please look", "looked")
	file := put(t, filepath.Join(dir, "2026", "09", "12", "rollout-2026-09-12T06-15-09-"+testID+".JSONL.ZST"), compressed(t, text))
	put(t, filepath.Join(dir, "rollout-"+testID+".zst"), compressed(t, text))
	found, err := FindTranscripts([]string{dir})
	if err != nil {
		t.Fatal(err)
	}
	if matching := found.Matching(testID); len(matching) != 1 || !strings.EqualFold(matching[0], file) {
		t.Fatalf("matching = %v", matching)
	}
	messages, err := TranscriptMessages(file)
	if err != nil || len(messages) != 2 || messages[1].Text != "looked" {
		t.Fatalf("messages = %v, err = %v", messages, err)
	}
	checkReadsAs(t, file, text)
	if raw := InventoryRaw(dir, "codex-sessions"); raw.Files != 1 {
		t.Fatalf("plan counted %d", raw.Files)
	}
}

func checkReadsAs(t *testing.T, file, text string) {
	t.Helper()
	stream, err := OpenTranscript(file)
	if err != nil {
		t.Fatal(err)
	}
	read, err := io.ReadAll(stream)
	if err != nil || string(read) != text || stream.Close() != nil {
		t.Fatalf("read %q, err = %v", read, err)
	}
}

// net/http can still be reading an upload when the server has answered and the caller closes the body, and then closes it again.
func TestACompressedUploadTheServerTurnsAwayIsClosedSafely(t *testing.T) {
	file := put(t, filepath.Join(t.TempDir(), "rollout-"+testID+".jsonl.zst"), compressed(t, rollout(testID, "", "2026-09-12T10:15:09Z", strings.Repeat("words and more words ", 1<<21))))
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Connection", "close")
		w.WriteHeader(http.StatusRequestEntityTooLarge)
	}))
	defer server.Close()
	for range 5 {
		body, err := OpenTranscript(file)
		if err != nil {
			t.Fatal(err)
		}
		if answer, err := http.Post(server.URL, "application/x-ndjson", body); err == nil {
			_ = answer.Body.Close()
		}
		if body.Close() != nil {
			t.Fatal("a second close failed")
		}
		if _, err := body.Read(make([]byte, 8)); !errors.Is(err, os.ErrClosed) {
			t.Fatalf("a read after close = %v", err)
		}
	}
}

func TestATranscriptIsSizedAsItIsSent(t *testing.T) {
	dir := t.TempDir()
	text := rollout(testID, "", "2026-09-12T10:15:09Z", strings.Repeat("words ", 50000))
	plain := put(t, filepath.Join(dir, "rollout-a-"+testID+".jsonl"), []byte(text))
	packed := put(t, filepath.Join(dir, "rollout-b-"+childID+".jsonl.zst"), compressed(t, text))
	for _, file := range []string{plain, packed} {
		if size, err := TranscriptSize(file); err != nil || size != int64(len(text)) {
			t.Fatalf("%s: size = %d, err = %v, want %d", filepath.Base(file), size, err, len(text))
		}
	}
	for _, file := range []string{put(t, filepath.Join(dir, "rollout-c-"+laterID+".jsonl.zst"), []byte("damaged")), filepath.Join(dir, "absent.jsonl")} {
		if _, err := TranscriptSize(file); err == nil {
			t.Fatalf("%s was sized", filepath.Base(file))
		}
	}
}

// plan sizes each transcript as a session upload sends it, decompressed, and counts those over the server's default limit.
func TestPlanCountsTranscriptsOverTheSessionLimit(t *testing.T) {
	dir := t.TempDir()
	text := rollout(testID, "", "2026-09-12T10:15:09Z", "hi")
	put(t, filepath.Join(dir, "rollout-a-"+testID+".jsonl.zst"), compressed(t, text))
	huge := put(t, filepath.Join(dir, "rollout-b-"+childID+".jsonl"), nil)
	if err := os.Truncate(huge, DefaultMaxSessionBytes+1); err != nil {
		t.Fatal(err)
	}
	put(t, filepath.Join(dir, "rollout-c-"+laterID+".jsonl.zst"), []byte("damaged"))
	raw := InventoryRaw(dir, "codex-sessions")
	if raw.Files != 3 || raw.OverSessionLimit != 1 || raw.Largest != DefaultMaxSessionBytes+1 || raw.Unreadable != 1 {
		t.Fatalf("raw = %+v", raw)
	}
	if small := InventoryRaw(filepath.Dir(put(t, filepath.Join(t.TempDir(), "x", "rollout-"+testID+".jsonl.zst"), compressed(t, text))), "c"); small.Largest != int64(len(text)) || small.OverSessionLimit != 0 {
		t.Fatalf("small = %+v", small)
	}
}

func TestADamagedCompressedTranscriptIsUnreadableNotShorter(t *testing.T) {
	file := put(t, filepath.Join(t.TempDir(), "rollout-"+testID+".jsonl.zst"), []byte("not zstd at all"))
	if _, err := TranscriptMessages(file); err == nil {
		t.Fatal("a damaged file was read")
	}
	if NamesSession(file, testID) {
		t.Fatal("a damaged file named a session")
	}
	cut := compressed(t, rollout(testID, "", "2026-09-12T10:15:09Z", strings.Repeat("long text ", 10000)))
	put(t, file, cut[:len(cut)/2])
	if _, err := TranscriptMessages(file); err == nil {
		t.Fatal("a cut-off file was read as whole")
	}
	if _, err := OpenTranscript(filepath.Join(t.TempDir(), "absent.jsonl.zst")); err == nil {
		t.Fatal("a missing file opened")
	}
}

// Codex moves a rollout it archives into one flat folder, compressed, so a session's threads can be split between the two.
func TestASessionsThreadsAreFoundInTheArchiveTooAndPutInTheExportersOrder(t *testing.T) {
	sessions, archived := t.TempDir(), t.TempDir()
	own := put(t, filepath.Join(sessions, "2026", "09", "12", "rollout-a-"+rootID+".jsonl"), []byte(rollout(rootID, rootID, "2026-09-12T10:15:09.729Z", "start", "started")))
	put(t, filepath.Join(archived, "rollout-b-"+laterID+".jsonl.zst"), compressed(t, rollout(laterID, rootID, "2026-09-12T11:00:00Z", "later child", "done")))
	put(t, filepath.Join(sessions, "2026", "09", "12", "rollout-c-"+childID+".jsonl"), []byte(rollout(childID, rootID, "2026-09-12T10:30:00.5+00:00", "child", "child done")))
	// Another session's thread, and one whose last session_meta files it under another session, as the exporter reads it.
	put(t, filepath.Join(sessions, "rollout-d-"+testID+".jsonl"), []byte(rollout(testID, testID, "2026-09-12T10:00:00Z", "other")))
	put(t, filepath.Join(sessions, "rollout-e-"+strings.Replace(childID, "0002", "0004", 1)+".jsonl"), []byte(rollout(strings.Replace(childID, "0002", "0004", 1), rootID, "2026-09-12T10:40:00Z", "moved")+`{"type":"session_meta","payload":{"id":"x","session_id":"`+testID+`"}}`+"\n"))
	found, err := FindTranscripts([]string{sessions, archived})
	if err != nil {
		t.Fatal(err)
	}
	family, err := found.Family(rootID, own)
	if err != nil {
		t.Fatal(err)
	}
	var ids []string
	for _, thread := range family {
		ids = append(ids, thread.ID)
	}
	if strings.Join(ids, " ") != rootID+" "+childID+" "+laterID {
		t.Fatalf("family = %v", ids)
	}
	if family[2].Messages[0].Text != "later child" {
		t.Fatalf("the archived thread was not read: %+v", family[2])
	}
}

func TestTwoFilesOfOneThreadAreNotChosenBetween(t *testing.T) {
	dir := t.TempDir()
	own := put(t, filepath.Join(dir, "rollout-a-"+rootID+".jsonl"), []byte(rollout(rootID, "", "2026-09-12T10:00:00Z", "start")))
	child := rollout(childID, rootID, "2026-09-12T10:30:00Z", "child")
	put(t, filepath.Join(dir, "rollout-b-"+childID+".jsonl"), []byte(child))
	put(t, filepath.Join(dir, "archived", "rollout-b-"+childID+".jsonl.zst"), compressed(t, child))
	found, err := FindTranscripts([]string{dir})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := found.Family(rootID, own); !errors.Is(err, ErrSeveralThreads) {
		t.Fatalf("err = %v", err)
	}
	put(t, filepath.Join(dir, "archived", "rollout-b-"+childID+".jsonl.zst"), []byte("damaged"))
	put(t, filepath.Join(dir, "rollout-c-"+laterID+".jsonl"), []byte(rollout(laterID, rootID, "2026-09-12T10:31:00Z", strings.Repeat("x", maxTranscriptLine))))
	found, err = FindTranscripts([]string{dir})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := found.Family(rootID, own); err == nil || errors.Is(err, ErrSeveralThreads) {
		t.Fatalf("a thread that cannot be read was not an error: %v", err)
	}
}

func TestAnExportOfEveryThreadIsCoveredByThemAllInTurn(t *testing.T) {
	own := []Message{{"user", "start"}, {"assistant", "started"}}
	child := []Message{{"user", "child"}, {"assistant", "## User\n\nquoted, not a message"}}
	export := []byte(hookExport(rootID, own, child))
	if !BodyCovered(export, own, child) {
		t.Fatal("the export of both threads was not covered by them")
	}
	if !BodyCovered(export, append(own, Message{"user", "said after the export"}), child, []Message{{"user", "a thread begun after it"}}) {
		t.Fatal("threads that have gone on since were not read as covering it")
	}
	for name, threads := range map[string][][]Message{
		"its own thread alone": {own},
		"the threads swapped":  {child, own},
		"a child cut short":    {own, child[:1]},
		"a message changed":    {own, {child[0], {"assistant", "## User\n\nquoted, changed"}}},
	} {
		if BodyCovered(export, threads...) {
			t.Errorf("%s was read as covered", name)
		}
	}
}

// A Python exporter in text mode on Windows writes each "\n" as "\r\n", so a message's own "\r\n" is written as "\r\r\n".
func TestAnExportWrittenOnWindowsIsReadBackExactly(t *testing.T) {
	own := []Message{{"user", "line one\r\nline two"}, {"assistant", "a lone \r stays"}}
	rendered := hookExport(rootID, own)
	windows := []byte(strings.ReplaceAll(rendered, "\n", "\r\n"))
	if !BodyCovered(windows, own) || !BodyCovered([]byte(rendered), own) {
		t.Fatal("the exporter's output was not covered")
	}
	folded := []Message{{"user", "line one\nline two"}, own[1]}
	if BodyCovered(windows, folded) || BodyCovered([]byte(rendered), folded) {
		t.Fatal("a message's \\r\\n and \\n were taken as the same text")
	}
	// One "\n" written bare means the file was not translated, so it is read as it is, and its "\r\n" is the message's own.
	mixed := []byte(strings.Replace(string(windows), "\r\n", "\n", 1))
	if BodyCovered(mixed, own) {
		t.Fatal("a file with both line endings was read as translated")
	}
}

func TestATitleNoExporterWouldWriteIsNotCovered(t *testing.T) {
	own := []Message{{"user", "# Fix the build\nsecond line"}, {"assistant", "fixed"}}
	export := hookExport(rootID, own)
	if !BodyCovered([]byte(export), own) {
		t.Fatal("the hook exporter's title was not read")
	}
	chunkTitle := strings.Replace(export, "# # Fix the build\n", "# Fix the build\n", 1)
	if !BodyCovered([]byte(chunkTitle), own) {
		t.Fatal("render_chunk's title was not read")
	}
	for name, text := range map[string]string{
		"a title of its own": strings.Replace(export, "# # Fix the build\n", "# something only the export holds\n", 1),
		"no title":           strings.Replace(export, "# # Fix the build\n\n", "", 1),
	} {
		if BodyCovered([]byte(text), own) {
			t.Errorf("%s was read as covered", name)
		}
	}
	assistantOnly := []Message{{"assistant", "only me"}}
	if !BodyCovered([]byte(hookExport(rootID, assistantOnly)), assistantOnly) {
		t.Fatal("Codex chat, the title for a session with no user message, was not read")
	}
	long := []Message{{"user", strings.Repeat("é", 120) + " after"}}
	if !BodyCovered([]byte(hookExport(rootID, long)), long) || firstLine(long[0].Text) != strings.Repeat("é", 100) {
		t.Fatal("a long first line was not cut as Python cuts it")
	}
}

func TestTimesSortAsTheExporterWritesThem(t *testing.T) {
	for text, want := range map[string]string{
		"2026-09-12T10:15:09.729Z":         "2026-09-12T10:15:09.729000+00:00",
		"2026-09-12T10:15:09Z":             "2026-09-12T10:15:09+00:00",
		"2026-09-12T10:15:09.000001-05:30": "2026-09-12T10:15:09.000001-05:30",
	} {
		stamp, ok := parseStamp(text)
		if !ok || pythonISO(stamp) != want {
			t.Errorf("%s = %s", text, pythonISO(stamp))
		}
	}
	if _, ok := parseStamp(12); ok {
		t.Fatal("a number was read as a time")
	}
	if got := pythonISO(time.Date(2026, 1, 1, 0, 0, 0, 0, time.FixedZone("", 3600+30))); got != "2026-01-01T00:00:00+01:00:30" {
		t.Fatalf("got %s", got)
	}
}

// A thread with no session_meta time sorts by its earliest record, as the exporter does.
func TestAThreadWithNoMetaTimeSortsByItsFirstRecord(t *testing.T) {
	dir := t.TempDir()
	own := put(t, filepath.Join(dir, "rollout-a-"+rootID+".jsonl"), []byte(rollout(rootID, "", "2026-09-12T10:00:00Z", "start")))
	child := `{"timestamp":"2026-09-12T12:00:00Z","type":"session_meta","payload":{"id":"` + childID + `","session_id":"` + rootID + `"}}` + "\n" +
		`{"timestamp":"2026-09-12T09:00:00Z","type":"response_item","payload":{"type":"message","role":"user","content":"early"}}` + "\n"
	put(t, filepath.Join(dir, "rollout-b-"+childID+".jsonl"), []byte(child))
	found, err := FindTranscripts([]string{dir})
	if err != nil {
		t.Fatal(err)
	}
	family, err := found.Family(rootID, own)
	if err != nil || len(family) != 2 || family[0].ID != childID {
		t.Fatalf("family = %+v, err = %v", family, err)
	}
}
