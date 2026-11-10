package migrate

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestSessionIDOfReadsOnlyTheFrontmatter(t *testing.T) {
	for text, want := range map[string]string{
		"---\nsession_id: \"abc\"\n---\n":             "abc",
		"---\r\ntitle: x\r\nsession_id: 'abc'\r\n---": "abc",
		"---\nsession_id: abc\n":                      "abc",
		"---\ntitle: x\n---\nsession_id: abc\n":       "",
		"session_id: abc\n":                           "",
		"":                                            "",
		"---\n  session_id: abc\n---\n":               "",
	} {
		if got := SessionIDOf([]byte(text)); got != want {
			t.Errorf("%q = %q, want %q", text, got, want)
		}
	}
	if got := SessionIDOf([]byte("---\n" + strings.Repeat("x: y\n", 20000) + "session_id: late\n---\n")); got != "" {
		t.Fatalf("a session_id past the limit was read: %q", got)
	}
}

const testID = "abcdef01-2345-6789-abcd-ef0123456789"

func meta(id string) string {
	return `{"type":"session_meta","payload":{"id":"` + id + `"}}` + "\n"
}

func TestATranscriptIsFoundByItsNameAndConfirmedByItsMeta(t *testing.T) {
	dir := t.TempDir()
	write := func(name, text string) string {
		path := filepath.Join(dir, filepath.FromSlash(name))
		if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, []byte(text), 0o600); err != nil {
			t.Fatal(err)
		}
		return path
	}
	right := write("a/rollout-"+strings.ToUpper(testID)+".jsonl", `{"type":"turn_context","payload":{}}`+"\n"+meta(testID))
	write("b/rollout-"+testID+".jsonl", meta("other"))
	write("c/rollout-"+testID+".txt", meta(testID))
	write("c/nothing-here.jsonl", meta(testID))
	found, err := FindTranscripts([]string{dir})
	if err != nil {
		t.Fatal(err)
	}
	if len(found.ByID[testID]) != 2 {
		t.Fatalf("by id = %v", found.ByID)
	}
	if matching := found.Matching(testID); len(matching) != 1 || matching[0] != right {
		t.Fatalf("matching = %v", matching)
	}
	if _, err := FindTranscripts([]string{filepath.Join(dir, "absent")}); err == nil {
		t.Fatal("a missing folder was accepted")
	}
}

func TestSessionMetaMustComeEarly(t *testing.T) {
	path := filepath.Join(t.TempDir(), "x.jsonl")
	if err := os.WriteFile(path, []byte(strings.Repeat(`{"type":"event_msg"}`+"\n", maxMetaRecords)+meta(testID)), 0o600); err != nil {
		t.Fatal(err)
	}
	if NamesSession(path, testID) || NamesSession(filepath.Join(t.TempDir(), "absent"), testID) {
		t.Fatal("a late or missing meta was taken")
	}
}

func TestReachIsReadAsTheServerReadsIt(t *testing.T) {
	for text, want := range map[string]int64{
		"---\nmessage_start: 10\nmessage_count: 5\n---\n":      15,
		"---\nmessage_count: 5\n---\n":                         5,
		"---\nmessage_end: 9\n---\n":                           10,
		"---\r\nmessage_start: 2\r\nmessage_end: 4\r\n---\r\n": 5,
		"---\nmessage_count: 5\nmessage_end: 99\n---\n":        5,
	} {
		if got, known := ReachOf([]byte(text)); !known || got != want {
			t.Errorf("%q = %d %v, want %d", text, got, known, want)
		}
	}
	for _, text := range []string{"---\ntitle: x\n---\n", "message_count: 5\n", "---\nmessage_count: 5\n", "---\nmessage_count: x\n---\n"} {
		if _, known := ReachOf([]byte(text)); known {
			t.Errorf("%q was read as known", text)
		}
	}
}

func TestMessagesAreCountedAsTheServerCountsThem(t *testing.T) {
	path := filepath.Join(t.TempDir(), "t.jsonl")
	lines := []string{
		meta(testID),
		`{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"a"}]}}`,
		`{"type":"response_item","payload":{"type":"message","role":"assistant","content":"b"}}`,
		`{"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"c"},{"type":"image","text":"x"}]}}`,
		`{"type":"response_item","payload":{"type":"message","role":"developer","content":"d"}}`,
		`{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"image","text":"e"}]}}`,
		`{"type":"response_item","payload":{"type":"message","role":"user","content":"  \n\u001f "}}`,
		`{"type":"response_item","payload":{"type":"function_call","role":"user","content":"f"}}`,
		`{"type":"event_msg","payload":{"type":"message","role":"user","content":"g"}}`,
		`not json`,
	}
	if err := os.WriteFile(path, []byte(strings.Join(lines, "\n")+"\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	if count, err := CountMessages(path); err != nil || count != 3 {
		t.Fatalf("count = %d, err = %v", count, err)
	}
	if _, err := CountMessages(filepath.Join(t.TempDir(), "absent")); err == nil {
		t.Fatal("a missing file was counted")
	}
}

func TestOneFileIsKeptOnceHoweverItIsReached(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "a", "rollout-"+testID+".jsonl")
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte(meta(testID)), 0o600); err != nil {
		t.Fatal(err)
	}
	found, err := FindTranscripts([]string{dir, filepath.Join(dir, "a"), dir + string(filepath.Separator)})
	if err != nil || len(found.ByID[testID]) != 1 {
		t.Fatalf("found = %v, err = %v", found, err)
	}
}

func TestARerenderSummaryAddsUp(t *testing.T) {
	s := RerenderSummary{OverCap: 11, Shorter: 1, Uncounted: 1, Sent: 1, Already: 1, Partial: 1, Refused: map[string]int{"a": 1}, NoSessionID: 1, NoTranscript: 1, Several: 1, Unreadable: 1, NotReached: 1}
	if !s.Accounted() || s.NotHeld() != 9 || s.RefusedTotal() != 1 {
		t.Fatalf("summary = %+v", s)
	}
	s.WouldSend = 1
	if s.Accounted() {
		t.Fatal("an extra count was accounted for")
	}
}

func TestALinkToATranscriptIsTheSameFile(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "a", "rollout-"+testID+".jsonl")
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte(meta(testID)), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(filepath.Join(dir, "a"), filepath.Join(dir, "b")); err != nil {
		t.Skipf("this host cannot make links: %v", err)
	}
	found, err := FindTranscripts([]string{filepath.Join(dir, "a"), filepath.Join(dir, "b")})
	if err != nil || len(found.ByID[testID]) != 1 {
		t.Fatalf("found = %v, err = %v", found, err)
	}
}
