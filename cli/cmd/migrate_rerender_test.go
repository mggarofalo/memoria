package cmd

import (
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/klauspost/compress/zstd"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/auth"
	"github.com/mggarofalo/memoria/cli/internal/migrate"
)

const rerenderID = "abcdef01-2345-6789-abcd-ef0123456789"

// sessionCall is one upload the fake session route saw.
type sessionCall struct {
	collection, format, sessionID, updated, contentType, body string
}

// sessionServer is the session route: GET answers what it holds, and POST answers as told.
type sessionServer struct {
	*httptest.Server
	mu     sync.Mutex
	held   map[string]string
	calls  []sessionCall
	gets   int
	answer func(attempt int) (int, string)
}

func newSessionServer(t *testing.T) *sessionServer {
	t.Helper()
	s := &sessionServer{held: map[string]string{}}
	s.Server = httptest.NewServer(http.HandlerFunc(s.serve))
	t.Cleanup(s.Close)
	return s
}

func (s *sessionServer) serve(w http.ResponseWriter, r *http.Request) {
	if !s.handle(w, r) {
		writeProblem(w, pushAnswer{status: http.StatusNotFound})
	}
}

func (s *sessionServer) handle(w http.ResponseWriter, r *http.Request) bool {
	if r.URL.Path != "/v1/ingest/session" {
		return false
	}
	query := r.URL.Query()
	s.mu.Lock()
	defer s.mu.Unlock()
	if r.Method == http.MethodGet {
		s.gets++
		s.writeStatus(w, query.Get("sessionId"))
		return true
	}
	body, _ := io.ReadAll(r.Body)
	call := sessionCall{query.Get("collection"), query.Get("format"), query.Get("sessionId"), query.Get("updated"), r.Header.Get("Content-Type"), string(body)}
	s.calls = append(s.calls, call)
	status, code := http.StatusAccepted, ""
	if s.answer != nil {
		status, code = s.answer(len(s.calls) - 1)
	}
	s.writeUpload(w, call, status, code)
	return true
}

func (s *sessionServer) writeStatus(w http.ResponseWriter, id string) {
	state, version := "none", "null"
	if updated, held := s.held[id]; held {
		state, version = "complete", fmt.Sprintf("%q", updated)
	}
	w.Header().Set("Content-Type", "application/json")
	_, _ = fmt.Fprintf(w, `{"collection":"notes","sessionId":%q,"state":%q,"updated":%s,"paths":[]}`, id, state, version)
}

func (s *sessionServer) writeUpload(w http.ResponseWriter, call sessionCall, status int, code string) {
	if status != http.StatusAccepted && status != http.StatusMultiStatus {
		writeProblem(w, pushAnswer{status: status, code: code})
		return
	}
	complete := status == http.StatusAccepted
	if complete {
		s.held[call.sessionID] = call.updated
	}
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_, _ = fmt.Fprintf(w, `{"collection":%q,"sessionId":%q,"format":"codex","complete":%t,"parts":[],"read":{"bytesRead":1,"lines":1,"malformedLines":0,"messages":1,"messagesOverLimit":0,"oversizedLines":0,"skippedContent":false,"unterminatedLines":0},"redaction":"applied","redactions":{},"refusalCount":0,"refusals":[],"reindexState":"queued","retryable":false,"superseded":{"removed":[],"kept":[]},"jobId":null,"reindexJobId":null}`, call.collection, call.sessionID, complete)
}

func (s *sessionServer) uploads() []sessionCall {
	s.mu.Lock()
	defer s.mu.Unlock()
	return append([]sessionCall(nil), s.calls...)
}

type rerenderFixture struct {
	*pushFixture
	codex, transcript, big string
}

// bigExport is a Codex export over the default cap, naming its session in frontmatter as the exporter does.
func bigExport(id string) string {
	return "---\nsession_id: \"" + id + "\"\nmessage_count: 3\n---\n\n" + strings.Repeat("words ", migrate.DefaultMaxDocumentBytes/6+1)
}

// transcriptFor is a transcript of three messages, as many as bigExport says it reaches.
func transcriptFor(id string) string {
	return transcriptOf(id, 3)
}

func transcriptOf(id string, messages int) string {
	text := `{"timestamp":"2026-01-01T00:00:00Z","type":"session_meta","payload":{"id":"` + id + `","cwd":"/w"}}` + "\n"
	for i := range messages {
		role := []string{"user", "assistant"}[i%2]
		text += `{"timestamp":"2026-01-01T00:00:01Z","type":"response_item","payload":{"type":"message","role":"` + role + `","content":[{"type":"input_text","text":"hi"}]}}` + "\n"
	}
	return text
}

func newRerenderFixture(t *testing.T) *rerenderFixture {
	t.Helper()
	f := &rerenderFixture{pushFixture: newPushFixture(t), codex: t.TempDir()}
	f.big = filepath.Join(f.notes, "2026-01-01_abcdef01.md")
	writeFile(t, f.big, bigExport(rerenderID))
	f.transcript = filepath.Join(f.codex, "2026", "01", "01", "rollout-2026-01-01T00-00-00-"+rerenderID+".jsonl")
	writeFile(t, f.transcript, transcriptFor(rerenderID))
	return f
}

func (f *rerenderFixture) run(t *testing.T, server *sessionServer, extra ...string) (migrate.RerenderSummary, int, string) {
	t.Helper()
	args := append([]string{"migrate", "rerender", "--qmd-config", f.config, "--codex-sessions", f.codex, "--format", "json"}, extra...)
	if server != nil {
		f.h.login(server.URL)
	}
	code := f.h.run(args...)
	var result migrate.RerenderResult
	if err := json.Unmarshal(f.h.stdout.Bytes(), &result); err != nil || len(result.Collections) != 1 {
		t.Fatalf("stdout = %q, stderr = %q: %v", f.h.stdout.String(), f.h.stderr.String(), err)
	}
	return result.Collections[0], code, result.ReportFile
}

func rerenderProblems(t *testing.T, path string) []migrate.RerenderProblem {
	t.Helper()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	var problems []migrate.RerenderProblem
	if err := json.Unmarshal(data, &problems); err != nil {
		t.Fatal(err)
	}
	return problems
}

func TestRerenderIsADryRunThatNeedsNoServer(t *testing.T) {
	f := newRerenderFixture(t)
	summary, code, _ := f.run(t, nil)
	if code != 0 || summary.OverCap != 1 || summary.WouldSend != 1 || summary.Sent != 0 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
	if strings.Contains(f.h.stdout.String()+f.h.stderr.String(), "abcdef01") {
		t.Fatalf("a name reached the output: %q", f.h.stdout.String())
	}
}

func TestRerenderSendsTheRawTranscriptAsCodex(t *testing.T) {
	f := newRerenderFixture(t)
	server := newSessionServer(t)
	summary, code, _ := f.run(t, server, "--apply")
	if code != 0 || summary.Sent != 1 {
		t.Fatalf("code = %d, summary = %+v, stderr = %q", code, summary, f.h.stderr.String())
	}
	calls := server.uploads()
	if len(calls) != 1 || calls[0].format != "codex" || calls[0].sessionID != rerenderID || calls[0].collection != "notes" || calls[0].body != transcriptFor(rerenderID) || calls[0].contentType != "application/x-ndjson" {
		t.Fatalf("calls = %+v", calls)
	}
	checkVersion(t, f.transcript, calls[0].updated)
}

// checkVersion is the transcript's own time, as the route orders uploads by it.
func checkVersion(t *testing.T, transcript, sent string) {
	t.Helper()
	info, err := os.Stat(transcript)
	if err != nil {
		t.Fatal(err)
	}
	updated, err := time.Parse(time.RFC3339Nano, sent)
	if err != nil || !updated.Equal(info.ModTime().UTC().Truncate(time.Millisecond)) {
		t.Fatalf("updated = %q, err = %v", sent, err)
	}
}

func TestARerenderRunAgainSendsNothingTheServerHolds(t *testing.T) {
	f := newRerenderFixture(t)
	server := newSessionServer(t)
	if _, code, _ := f.run(t, server, "--apply"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	summary, code, _ := f.run(t, server, "--apply")
	if code != 0 || summary.Already != 1 || len(server.uploads()) != 1 {
		t.Fatalf("code = %d, summary = %+v, uploads = %d", code, summary, len(server.uploads()))
	}
	later := time.Now().Add(time.Hour)
	if err := os.Chtimes(f.transcript, later, later); err != nil {
		t.Fatal(err)
	}
	if summary, code, _ := f.run(t, server, "--apply"); code != 0 || summary.Sent != 1 || len(server.uploads()) != 2 {
		t.Fatalf("a newer transcript: code = %d, summary = %+v", code, summary)
	}
}

func TestAMissingTranscriptIsSaidPlainlyAndFails(t *testing.T) {
	f := newRerenderFixture(t)
	if err := os.Remove(f.transcript); err != nil {
		t.Fatal(err)
	}
	for _, apply := range [][]string{nil, {"--apply"}} {
		checkMissing(t, f, apply)
	}
}

func checkMissing(t *testing.T, f *rerenderFixture, apply []string) {
	t.Helper()
	server := newSessionServer(t)
	summary, code, report := f.run(t, server, apply...)
	if code != apperr.ExitGeneric || summary.NoTranscript != 1 || !strings.Contains(f.h.stderr.String(), "export is their only copy") || len(server.uploads()) != 0 {
		t.Fatalf("%v: code = %d, summary = %+v, stderr = %q", apply, code, summary, f.h.stderr.String())
	}
	if problems := rerenderProblems(t, report); len(problems) != 1 || problems[0].Status != migrate.RerenderNoTranscript || problems[0].Path != "2026-01-01_abcdef01.md" {
		t.Fatalf("problems = %+v", problems)
	}
}

func TestATranscriptOnlyNamedLikeTheSessionIsNotIt(t *testing.T) {
	f := newRerenderFixture(t)
	writeFile(t, f.transcript, transcriptFor("00000000-0000-0000-0000-000000000000"))
	if summary, code, _ := f.run(t, nil); code != apperr.ExitGeneric || summary.NoTranscript != 1 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
	writeFile(t, f.transcript, "not json\n")
	if summary, _, _ := f.run(t, nil); summary.NoTranscript != 1 {
		t.Fatalf("summary = %+v", summary)
	}
}

func TestTwoTranscriptsForOneSessionAreNotChosenBetween(t *testing.T) {
	f := newRerenderFixture(t)
	writeFile(t, filepath.Join(f.codex, "archived", "rollout-x-"+rerenderID+".jsonl"), transcriptFor(rerenderID))
	server := newSessionServer(t)
	if summary, code, _ := f.run(t, server, "--apply"); code != apperr.ExitGeneric || summary.Several != 1 || len(server.uploads()) != 0 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
}

func TestAnExportNamingNoSessionCannotBeReRendered(t *testing.T) {
	f := newRerenderFixture(t)
	writeFile(t, f.big, strings.Repeat("words ", migrate.DefaultMaxDocumentBytes/6+1))
	if summary, code, _ := f.run(t, nil); code != apperr.ExitGeneric || summary.NoSessionID != 1 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
}

func TestSessionRefusalsAndPartsAreReported(t *testing.T) {
	for _, c := range []struct {
		status       int
		code, reason string
		partial      bool
	}{
		{http.StatusMultiStatus, "", "partial", true},
		{http.StatusConflict, "session_shrank", "session_shrank", false},
		{http.StatusBadRequest, "session_id_mismatch", "session_id_mismatch", false},
		{http.StatusRequestEntityTooLarge, "", "http_413", false},
	} {
		f := newRerenderFixture(t)
		server := newSessionServer(t)
		server.answer = func(int) (int, string) { return c.status, c.code }
		summary, code, report := f.run(t, server, "--apply")
		got := summary.Refused[c.reason]
		if c.partial {
			got = summary.Partial
		}
		if code != apperr.ExitGeneric || got != 1 || summary.Sent != 0 {
			t.Fatalf("%d %s: code = %d, summary = %+v", c.status, c.code, code, summary)
		}
		if problems := rerenderProblems(t, report); len(problems) != 1 || problems[0].Reason != c.reason {
			t.Fatalf("problems = %+v", problems)
		}
	}
}

func TestALostAnswerIsSentAgain(t *testing.T) {
	f := newRerenderFixture(t)
	server := newSessionServer(t)
	server.answer = func(attempt int) (int, string) {
		if attempt == 0 {
			return http.StatusServiceUnavailable, "sidecar_unreachable"
		}
		return http.StatusAccepted, ""
	}
	if summary, code, _ := f.run(t, server, "--apply"); code != 0 || summary.Sent != 1 || len(server.uploads()) != 2 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
}

func TestAKeyItCannotUseStopsTheRunAndCountsTheRest(t *testing.T) {
	f := newRerenderFixture(t)
	id := "12345678-1234-1234-1234-123456789abc"
	writeFile(t, filepath.Join(f.notes, "2026-01-02_12345678.md"), bigExport(id))
	writeFile(t, filepath.Join(f.codex, "rollout-"+id+".jsonl"), transcriptFor(id))
	server := newSessionServer(t)
	server.answer = func(int) (int, string) { return http.StatusForbidden, "" }
	summary, code, report := f.run(t, server, "--apply")
	// The refused one is not reached either: nothing was stored, and the run stopped.
	if code == 0 || summary.NotReached != 2 || len(server.uploads()) != 1 || !summary.Accounted() {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
	if problems := rerenderProblems(t, report); len(problems) != 2 || problems[1].Status != migrate.RerenderNotReached {
		t.Fatalf("problems = %+v", problems)
	}
}

func TestWhatTheLastPushNamedOverTheCapIsReRenderedToo(t *testing.T) {
	f := newRerenderFixture(t)
	server := newSessionServer(t)
	small := filepath.Join(f.notes, "small.md")
	id := "12345678-1234-1234-1234-123456789abc"
	writeFile(t, small, "---\nsession_id: "+id+"\nmessage_count: 1\n---\n\nsmall\n")
	writeFile(t, filepath.Join(f.codex, "rollout-"+id+".jsonl"), transcriptFor(id))
	report, err := migrate.PushReportPath(server.URL)
	if err != nil {
		t.Fatal(err)
	}
	pushed, _ := json.Marshal([]migrate.PushProblem{{Collection: "notes", Target: "notes", Path: "small.md", Status: migrate.ProblemOverCap}, {Collection: "notes", Target: "notes", Path: "a.md", Status: migrate.ProblemRefused}})
	if err := auth.WritePrivateFile(report, pushed); err != nil {
		t.Fatal(err)
	}
	if summary, code, _ := f.run(t, server, "--apply"); code != 0 || summary.OverCap != 2 || summary.Sent != 2 {
		t.Fatalf("code = %d, summary = %+v, stderr = %q", code, summary, f.h.stderr.String())
	}
}

func TestRerenderNeedsItsFolderOfTranscripts(t *testing.T) {
	f := newRerenderFixture(t)
	if code := f.h.run("migrate", "rerender", "--qmd-config", f.config); code != apperr.ExitUsage {
		t.Fatalf("code = %d", code)
	}
	if code := f.h.run("migrate", "rerender", "--qmd-config", f.config, "--codex-sessions", filepath.Join(f.codex, "absent")); code != apperr.ExitUsage {
		t.Fatalf("code = %d", code)
	}
}

func TestTheTextFormsSayWhatIsHeldAndNameNoFile(t *testing.T) {
	f := newRerenderFixture(t)
	server := newSessionServer(t)
	f.h.login(server.URL)
	if code := f.h.run("migrate", "rerender", "--qmd-config", f.config, "--codex-sessions", f.codex, "--apply"); code != 0 || !strings.Contains(f.h.stdout.String(), "All 1 over-cap document(s) are held through their session") || strings.Contains(f.h.stdout.String(), "abcdef01") {
		t.Fatalf("code = %d, stdout = %q", code, f.h.stdout.String())
	}
	if code := f.h.run("migrate", "rerender", "--qmd-config", f.config, "--codex-sessions", f.codex, "--format", "markdown"); code != 0 || !strings.Contains(f.h.stdout.String(), "1 would send") {
		t.Fatalf("code = %d, stdout = %q", code, f.h.stdout.String())
	}
}

// After a re-render the export is not at its path, but its session is, and verify says so rather than calling it not held.
func TestVerifyCountsADocumentHeldThroughItsSession(t *testing.T) {
	f := newVerifyFixture(t)
	writeFile(t, filepath.Join(f.notes, "a.md"), "---\nsession_id: "+rerenderID+"\n---\n\nalpha text")
	f.server.forget()
	f.server.documents = 7
	sessions := &sessionServer{held: map[string]string{rerenderID: time.Now().UTC().Format(time.RFC3339Nano)}}
	route := f.server.routes
	f.server.routes = func(w http.ResponseWriter, r *http.Request) bool {
		return sessions.handle(w, r) || route(w, r)
	}
	summary, _, report := f.verify(t)
	if summary.HeldBySession != 1 || summary.NotHeld != 2 || summary.CountDiffers {
		t.Fatalf("summary = %+v", summary)
	}
	for _, problem := range verifyProblems(t, report) {
		if problem.Name == "a.md" {
			t.Fatalf("a.md was reported: %+v", problem)
		}
	}
}

func TestATranscriptShorterThanItsExportIsNotSent(t *testing.T) {
	f := newRerenderFixture(t)
	writeFile(t, f.transcript, transcriptOf(rerenderID, 2)+`{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"  \u001c "}]}}`+"\n")
	for _, apply := range [][]string{nil, {"--apply"}} {
		server := newSessionServer(t)
		summary, code, report := f.run(t, server, apply...)
		if code != apperr.ExitGeneric || summary.Shorter != 1 || len(server.uploads()) != 0 {
			t.Fatalf("%v: code = %d, summary = %+v", apply, code, summary)
		}
		if problems := rerenderProblems(t, report); len(problems) != 1 || problems[0].Status != migrate.RerenderShorter || problems[0].Reason != "1 messages" {
			t.Fatalf("problems = %+v", problems)
		}
	}
}

func TestAnExportThatDoesNotCountItsMessagesIsNotSent(t *testing.T) {
	f := newRerenderFixture(t)
	writeFile(t, f.big, strings.Replace(bigExport(rerenderID), "message_count: 3\n", "", 1))
	if summary, code, _ := f.run(t, nil); code != apperr.ExitGeneric || summary.Uncounted != 1 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
}

// A chunk of the session already stored carries its export's time, so the upload must not be older than that, nor than the transcript.
func TestTheVersionSentIsTheLaterOfTheTranscriptAndItsExports(t *testing.T) {
	transcriptTime, exportTime := time.Date(2025, 1, 1, 0, 0, 0, 0, time.UTC), time.Date(2025, 6, 1, 0, 0, 0, 0, time.UTC)
	for name, c := range map[string]struct{ transcript, export, want time.Time }{
		"transcript older": {transcriptTime, exportTime, exportTime},
		"transcript newer": {exportTime.Add(time.Hour), exportTime, exportTime.Add(time.Hour)},
	} {
		f := newRerenderFixture(t)
		sibling := filepath.Join(f.notes, "2026-01-01_abcdef01_part1.md")
		writeFile(t, sibling, "---\nsession_id: "+rerenderID+"\nmessage_start: 0\nmessage_count: 1\n---\n\nhi\n")
		for _, file := range []string{f.big, sibling} {
			if err := os.Chtimes(file, c.export, c.export); err != nil {
				t.Fatal(err)
			}
		}
		if err := os.Chtimes(f.transcript, c.transcript, c.transcript); err != nil {
			t.Fatal(err)
		}
		server := newSessionServer(t)
		if _, code, _ := f.run(t, server, "--apply"); code != 0 {
			t.Fatalf("%s: code = %d, stderr = %q", name, code, f.h.stderr.String())
		}
		if sent, err := time.Parse(time.RFC3339Nano, server.uploads()[0].updated); err != nil || !sent.Equal(c.want) {
			t.Fatalf("%s: sent %v, want %v (%v)", name, sent, c.want, err)
		}
	}
}

func TestAWriteStillRunningIsWaitedOut(t *testing.T) {
	f := newRerenderFixture(t)
	server := newSessionServer(t)
	server.answer = func(attempt int) (int, string) {
		if attempt < 2 {
			return http.StatusConflict, "ingest_in_progress"
		}
		return http.StatusAccepted, ""
	}
	if summary, code, _ := f.run(t, server, "--apply"); code != 0 || summary.Sent != 1 || len(server.uploads()) != 3 {
		t.Fatalf("code = %d, summary = %+v, uploads = %d", code, summary, len(server.uploads()))
	}
}

func TestOneFolderGivenTwiceFindsATranscriptOnce(t *testing.T) {
	f := newRerenderFixture(t)
	server := newSessionServer(t)
	if summary, code, _ := f.run(t, server, "--apply", "--codex-sessions", f.codex, "--codex-sessions", filepath.Join(f.codex, "2026")); code != 0 || summary.Sent != 1 {
		t.Fatalf("code = %d, summary = %+v, stderr = %q", code, summary, f.h.stderr.String())
	}
}

// The over-cap chunk may not be the session's last, so how far the messages reach is the furthest of all its exports here.
func TestTheFurthestChunkSetsHowManyMessagesAreNeeded(t *testing.T) {
	f := newRerenderFixture(t)
	// It sorts before the over-cap chunk, so the furthest is not simply the last one read.
	writeFile(t, filepath.Join(f.notes, "2026-01-01_0later.md"), "---\nsession_id: "+rerenderID+"\nmessage_start: 3\nmessage_count: 2\n---\n\nlater\n")
	writeFile(t, f.transcript, transcriptOf(rerenderID, 4))
	if summary, code, _ := f.run(t, nil); code != apperr.ExitGeneric || summary.Shorter != 1 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
	writeFile(t, f.transcript, transcriptOf(rerenderID, 5))
	if summary, code, _ := f.run(t, nil); code != 0 || summary.WouldSend != 1 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
}

// checkAccepted is an export sent on --accept-uncounted, named in the report and counted in the summary.
func checkAccepted(t *testing.T, f *rerenderFixture, report string) {
	t.Helper()
	if problems := rerenderProblems(t, report); len(problems) != 1 || problems[0].Status != migrate.RerenderAcceptedUncounted {
		t.Fatalf("problems = %+v", problems)
	}
	if !strings.Contains(f.h.stdout.String(), "acceptedUncounted") {
		t.Fatalf("stdout = %q", f.h.stdout.String())
	}
}

// An older export with no counts, in the exporter's own format, is sent when its body is the transcript's, and not otherwise
// unless the owner passes --accept-uncounted, which the report and the summary both say.
func TestAnUncountedExportGoesWhenItsBodyIsTheTranscripts(t *testing.T) {
	fixture := filepath.Join("..", "internal", "migrate", "testdata", "exporter")
	id := "5e55ad6a-1111-4222-8333-444455556666"
	export, err := os.ReadFile(filepath.Join(fixture, "2026-01-15_codex_"+id+"_chunk-00001.md"))
	if err != nil {
		t.Fatal(err)
	}
	transcript, err := os.ReadFile(filepath.Join(fixture, "rollout-2026-01-15T10-00-00-"+id+".jsonl"))
	if err != nil {
		t.Fatal(err)
	}
	uncounted := strings.NewReplacer("message_start: 0\n", "", "message_end: 2\n", "", "message_count: 3\n", "").Replace(strings.ReplaceAll(string(export), "\r\n", "\n"))
	// A long frontmatter field puts it over the cap and leaves its body as the exporter wrote it.
	big := strings.Replace(uncounted, "root_thread_id:", `padding: "`+strings.Repeat("x", migrate.DefaultMaxDocumentBytes)+"\"\nroot_thread_id:", 1)
	cases := []struct {
		name, transcript string
		extra            []string
		want             func(migrate.RerenderSummary) bool
	}{
		{"covered", string(transcript), nil, func(s migrate.RerenderSummary) bool { return s.WouldSend == 1 && s.AcceptedUncounted == 0 }},
		{"short", strings.Join(strings.Split(string(transcript), "\n")[:3], "\n") + "\n", nil, func(s migrate.RerenderSummary) bool { return s.Uncounted == 1 }},
		{"short, accepted", strings.Join(strings.Split(string(transcript), "\n")[:3], "\n") + "\n", []string{"--accept-uncounted"}, func(s migrate.RerenderSummary) bool { return s.WouldSend == 1 && s.AcceptedUncounted == 1 }},
	}
	for _, c := range cases {
		f := newRerenderFixture(t)
		writeFile(t, f.big, big)
		writeFile(t, f.transcript, "")
		writeFile(t, filepath.Join(f.codex, "rollout-2026-01-15T10-00-00-"+id+".jsonl"), c.transcript)
		summary, _, report := f.run(t, nil, c.extra...)
		if !c.want(summary) {
			t.Fatalf("%s: summary = %+v", c.name, summary)
		}
		if c.name == "short, accepted" {
			checkAccepted(t, f, report)
		}
	}
}

func codexMessage(role, text string) string {
	quoted, _ := json.Marshal(text)
	return `{"timestamp":"2026-01-01T00:00:01Z","type":"response_item","payload":{"type":"message","role":"` + role + `","content":[{"type":"input_text","text":` + string(quoted) + `}]}}` + "\n"
}

func codexMeta(id, root, start string) string {
	return `{"timestamp":"` + start + `","type":"session_meta","payload":{"id":"` + id + `","session_id":"` + root + `","timestamp":"` + start + `","cwd":"/w"}}` + "\n"
}

func zstdOf(t *testing.T, text string) string {
	t.Helper()
	var out strings.Builder
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
	return out.String()
}

const subagentID = "12345678-1234-1234-1234-123456789abc"

// subagentFixture is a session whose subagent thread Codex has archived, compressed, and the hook exporter's one export of both, as
// Python writes it on Windows, padded over the cap in its frontmatter.
func subagentFixture(t *testing.T) (*rerenderFixture, string, string) {
	t.Helper()
	f := newRerenderFixture(t)
	writeFile(t, f.transcript, codexMeta(rerenderID, rerenderID, "2026-01-01T00:00:00Z")+codexMessage("user", "please\r\nlook")+codexMessage("assistant", "looked"))
	sub := codexMeta(subagentID, rerenderID, "2026-01-01T00:05:00Z") + codexMessage("user", "a sub task, longer than anything of its session's own") + codexMessage("assistant", "sub done")
	archived := t.TempDir()
	writeFile(t, filepath.Join(archived, "rollout-2026-01-01T00-05-00-"+subagentID+".jsonl.zst"), zstdOf(t, sub))
	body := "---\nsession_id: \"" + rerenderID + "\"\npadding: \"" + strings.Repeat("x", migrate.DefaultMaxDocumentBytes) + "\"\n---\n\n# please\n\n" +
		"## User\n\nplease\r\nlook\n\n## Assistant\n\nlooked\n\n## User\n\na sub task, longer than anything of its session's own\n\n## Assistant\n\nsub done\n"
	writeFile(t, f.big, strings.ReplaceAll(body, "\n", "\r\n"))
	return f, archived, sub
}

// The hook exporter wrote a session's subagent threads into its export too, so each of them goes, as its own session, and only
// when every one of them is found.
func TestAnExportOfASessionAndItsSubagentsSendsEveryThread(t *testing.T) {
	f, archived, sub := subagentFixture(t)
	if summary, code, _ := f.run(t, nil); code != apperr.ExitGeneric || summary.Uncounted != 1 {
		t.Fatalf("without the archive: code = %d, summary = %+v", code, summary)
	}
	server := newSessionServer(t)
	summary, code, _ := f.run(t, server, "--apply", "--codex-sessions", archived)
	if code != 0 || summary.Sent != 1 || summary.Threads != 2 {
		t.Fatalf("code = %d, summary = %+v, stderr = %q", code, summary, f.h.stderr.String())
	}
	checkThreadsSent(t, server, sub)
	if summary, code, _ := f.run(t, server, "--apply", "--codex-sessions", archived); code != 0 || summary.Already != 1 || len(server.uploads()) != 2 {
		t.Fatalf("again: code = %d, summary = %+v", code, summary)
	}
}

// checkThreadsSent is each thread sent under its own id, the compressed one as its JSON lines.
func checkThreadsSent(t *testing.T, server *sessionServer, sub string) {
	t.Helper()
	calls := server.uploads()
	if len(calls) != 2 || calls[0].sessionID != rerenderID || calls[1].sessionID != subagentID || calls[1].body != sub {
		t.Fatalf("calls = %+v", calls)
	}
}

func TestAThreadThatDoesNotGoIsNamedAndTheRestStillGo(t *testing.T) {
	f, archived, _ := subagentFixture(t)
	server := newSessionServer(t)
	server.answer = func(attempt int) (int, string) {
		if attempt == 0 {
			return http.StatusRequestEntityTooLarge, ""
		}
		return http.StatusAccepted, ""
	}
	summary, code, report := f.run(t, server, "--apply", "--codex-sessions", archived)
	if code != apperr.ExitGeneric || summary.Refused["http_413"] != 1 || summary.Sent != 0 || len(server.uploads()) != 2 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
	checkThreadProblem(t, report, "", "http_413")
	server.answer = func(int) (int, string) { return http.StatusMultiStatus, "" }
	server.held = map[string]string{rerenderID: time.Now().Add(time.Hour).UTC().Format(time.RFC3339Nano)}
	if summary, _, report = f.run(t, server, "--apply", "--codex-sessions", archived); summary.Partial != 1 {
		t.Fatalf("summary = %+v", summary)
	}
	checkThreadProblem(t, report, subagentID, "partial")
}

func checkThreadProblem(t *testing.T, report, thread, reason string) {
	t.Helper()
	if problems := rerenderProblems(t, report); len(problems) != 1 || problems[0].Thread != thread || problems[0].Reason != reason {
		t.Fatalf("problems = %+v", problems)
	}
}

func TestCodexsArchiveBesideTheSessionsGivenIsAskedFor(t *testing.T) {
	home := t.TempDir()
	sessions, archive := filepath.Join(home, "sessions"), filepath.Join(home, "archived_sessions")
	if missingArchive([]string{sessions}) != "" {
		t.Fatal("an archive that is not there was asked for")
	}
	for _, dir := range []string{sessions, archive} {
		if err := os.MkdirAll(dir, 0o700); err != nil {
			t.Fatal(err)
		}
	}
	if got := missingArchive([]string{sessions + string(filepath.Separator)}); got != archive {
		t.Fatalf("got %q", got)
	}
	if got := missingArchive([]string{sessions, archive}); got != "" {
		t.Fatalf("an archive given was asked for: %q", got)
	}
	f := newRerenderFixture(t)
	f.codex = sessions
	writeFile(t, filepath.Join(sessions, "rollout-"+rerenderID+".jsonl"), transcriptFor(rerenderID))
	if _, code, _ := f.run(t, nil); code != 0 || !strings.Contains(f.h.stderr.String(), "--codex-sessions "+archive) {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
}

// A sibling thread that cannot be read does not stop an export its own transcript holds whole, and an empty one is not sent.
func TestOnlyTheThreadsAnExportNeedsCanStopIt(t *testing.T) {
	f, archived, _ := subagentFixture(t)
	body := "---\nsession_id: \"" + rerenderID + "\"\npadding: \"" + strings.Repeat("x", migrate.DefaultMaxDocumentBytes) + "\"\n---\n\n# please\n\n## User\n\nplease\r\nlook\n\n## Assistant\n\nlooked\n"
	writeFile(t, f.big, body)
	writeFile(t, filepath.Join(archived, "rollout-copy-"+subagentID+".jsonl"), codexMeta(subagentID, rerenderID, "2026-01-01T00:05:00Z"))
	if summary, code, _ := f.run(t, nil, "--codex-sessions", archived); code != 0 || summary.WouldSend != 1 || summary.Threads != 1 {
		t.Fatalf("two copies of a thread it does not need: code = %d, summary = %+v", code, summary)
	}
	empty := "98765432-1234-1234-1234-123456789abc"
	writeFile(t, filepath.Join(archived, "rollout-copy-"+subagentID+".jsonl"), codexMeta(empty, rerenderID, "2026-01-01T00:06:00Z"))
	server := newSessionServer(t)
	if summary, code, _ := f.run(t, server, "--apply", "--codex-sessions", archived); code != 0 || summary.Sent != 1 || summary.Threads != 2 || len(server.uploads()) != 2 {
		t.Fatalf("an empty thread: code = %d, summary = %+v, uploads = %d", code, summary, len(server.uploads()))
	}
	for _, call := range server.uploads() {
		if call.sessionID == empty {
			t.Fatal("a thread with no messages was sent")
		}
	}
}

// A transcript over the server's limit on one session upload is not sent, in a dry run or not: the server would refuse it whole.
func TestATranscriptOverTheSessionLimitIsNotSent(t *testing.T) {
	f := newRerenderFixture(t)
	limit := strconv.Itoa(len(transcriptFor(rerenderID)) - 1)
	for _, apply := range [][]string{{"--max-session-bytes", limit}, {"--max-session-bytes", limit, "--apply"}} {
		server := newSessionServer(t)
		summary, code, report := f.run(t, server, apply...)
		if code != apperr.ExitGeneric || summary.TooLarge != 1 || len(server.uploads()) != 0 || !summary.Accounted() {
			t.Fatalf("%v: code = %d, summary = %+v", apply, code, summary)
		}
		checkThreadProblem(t, report, "", strconv.Itoa(len(transcriptFor(rerenderID)))+" bytes, over the "+limit+" byte limit")
	}
	if summary, code, _ := f.run(t, nil, "--max-session-bytes", strconv.Itoa(len(transcriptFor(rerenderID)))); code != 0 || summary.WouldSend != 1 {
		t.Fatalf("at the limit: code = %d, summary = %+v", code, summary)
	}
	if code := f.h.run("migrate", "rerender", "--qmd-config", f.config, "--codex-sessions", f.codex, "--max-session-bytes", "0"); code != apperr.ExitUsage {
		t.Fatalf("a zero limit: code = %d", code)
	}
}

// The size that counts is a compressed thread's decompressed one, and the thread over it is named.
func TestACompressedThreadOverTheLimitIsNamed(t *testing.T) {
	f, archived, sub := subagentFixture(t)
	own, err := os.ReadFile(f.transcript)
	if err != nil || len(own) >= len(sub) {
		t.Fatalf("the fixture's own transcript is not the smaller: %v", err)
	}
	limit := strconv.Itoa(len(own))
	summary, code, report := f.run(t, nil, "--codex-sessions", archived, "--max-session-bytes", limit)
	if code != apperr.ExitGeneric || summary.TooLarge != 1 || !strings.Contains(f.h.stdout.String(), "transcriptTooLarge") {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
	checkThreadProblem(t, report, subagentID, strconv.Itoa(len(sub))+" bytes, over the "+limit+" byte limit")
}

func TestASubagentThreadThatCannotBeReadStopsItsExport(t *testing.T) {
	f, archived, sub := subagentFixture(t)
	writeFile(t, filepath.Join(archived, "rollout-copy-"+subagentID+".jsonl"), sub)
	if summary, code, _ := f.run(t, nil, "--codex-sessions", archived); code != apperr.ExitGeneric || summary.Several != 1 {
		t.Fatalf("two copies: code = %d, summary = %+v", code, summary)
	}
	writeFile(t, filepath.Join(archived, "rollout-copy-"+subagentID+".jsonl"), codexMeta("98765432-1234-1234-1234-123456789abc", rerenderID, "2026-01-01T00:06:00Z")+`{"type":"response_item","payload":`+strings.Repeat(" ", 16*1024*1024)+"}\n")
	if summary, code, _ := f.run(t, nil, "--codex-sessions", archived); code != apperr.ExitGeneric || summary.Unreadable != 1 {
		t.Fatalf("a line too long: code = %d, summary = %+v", code, summary)
	}
}
