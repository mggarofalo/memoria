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

func TestARerenderSummaryAddsUp(t *testing.T) {
	s := RerenderSummary{OverCap: 9, Sent: 1, Already: 1, Partial: 1, Refused: map[string]int{"a": 1}, NoSessionID: 1, NoTranscript: 1, Several: 1, Unreadable: 1, NotReached: 1}
	if !s.Accounted() || s.NotHeld() != 7 || s.RefusedTotal() != 1 {
		t.Fatalf("summary = %+v", s)
	}
	s.WouldSend = 1
	if s.Accounted() {
		t.Fatal("an extra count was accounted for")
	}
}
