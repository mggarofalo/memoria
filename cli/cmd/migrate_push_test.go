package cmd

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/auth"
	"github.com/mggarofalo/memoria/cli/internal/migrate"
)

// pushCall is one request the fake server saw.
type pushCall struct {
	collection, path, updated, contentType, body string
	length                                       int64
}

// pushAnswer is what the fake server says to a call. A status of 0 answers 202.
type pushAnswer struct {
	status      int
	code        string
	retryAfter  string
	frontmatter string
	drop        bool
	collection  string
	path        string
}

type pushServer struct {
	*httptest.Server
	// ledger is what a real server would answer GET with: the updated of each path it stored.
	ledger      map[string]string
	faultAnswer pushAnswer
	faultsLeft  int
	routes      func(http.ResponseWriter, *http.Request) bool
	state       atomic.Value
	t           *testing.T
	mu          sync.Mutex
	calls       []pushCall
	running     int
	most        int
	answer      func(call pushCall, attempt int) pushAnswer
}

func newPushServer(t *testing.T, answer func(pushCall, int) pushAnswer) *pushServer {
	t.Helper()
	s := &pushServer{t: t, answer: answer, ledger: map[string]string{}}
	s.Server = httptest.NewServer(http.HandlerFunc(s.serve))
	t.Cleanup(s.Close)
	return s
}

func (s *pushServer) begin(r *http.Request) (pushCall, pushAnswer) {
	body, _ := io.ReadAll(r.Body)
	query := r.URL.Query()
	call := pushCall{query.Get("collection"), query.Get("path"), query.Get("updated"), r.Header.Get("Content-Type"), string(body), r.ContentLength}
	s.mu.Lock()
	defer s.mu.Unlock()
	attempt := 0
	for _, earlier := range s.calls {
		if earlier.collection == call.collection && earlier.path == call.path {
			attempt++
		}
	}
	s.calls = append(s.calls, call)
	s.running++
	s.most = max(s.most, s.running)
	if s.answer == nil {
		return call, pushAnswer{}
	}
	return call, s.answer(call, attempt)
}

func (s *pushServer) serve(w http.ResponseWriter, r *http.Request) {
	if s.routes != nil && s.routes(w, r) {
		return
	}
	if r.Method == http.MethodGet {
		s.serveStatus(w, r)
		return
	}
	call, answer := s.begin(r)
	time.Sleep(2 * time.Millisecond)
	s.mu.Lock()
	s.running--
	s.mu.Unlock()
	switch {
	case answer.drop:
		connection, _, err := w.(http.Hijacker).Hijack()
		if err != nil {
			s.t.Error(err)
			return
		}
		_ = connection.Close()
	case answer.status != 0:
		writeProblem(w, answer)
	default:
		s.mu.Lock()
		s.ledger[call.path] = call.updated
		s.mu.Unlock()
		writeStored(w, call, answer)
	}
}

// forget is a server that lost everything it was sent.
func (s *pushServer) forget() {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.ledger = map[string]string{}
}

func (s *pushServer) serveStatus(w http.ResponseWriter, r *http.Request) {
	if fault, due := s.takeStatusFault(); due {
		writeProblem(w, fault)
		return
	}
	path := r.URL.Query().Get("path")
	s.mu.Lock()
	updated, stored := s.ledger[path]
	s.mu.Unlock()
	state, chosen := s.state.Load().(string)
	switch {
	case chosen && state == "unanswered":
		writeProblem(w, pushAnswer{status: http.StatusBadRequest})
		return
	case !chosen && stored:
		state = "complete"
	case !chosen:
		state = "none"
	}
	version := "null"
	if stored {
		version = fmt.Sprintf("%q", updated)
	}
	w.Header().Set("Content-Type", "application/json")
	_, _ = fmt.Fprintf(w, `{"collection":"notes","path":%q,"state":%q,"updated":%s}`, path, state, version)
}

func writeProblem(w http.ResponseWriter, answer pushAnswer) {
	if answer.retryAfter != "" {
		w.Header().Set("Retry-After", answer.retryAfter)
	}
	w.Header().Set("Content-Type", "application/problem+json")
	w.WriteHeader(answer.status)
	_, _ = fmt.Fprintf(w, `{"title":"refused","code":%q}`, answer.code)
}

func writeStored(w http.ResponseWriter, call pushCall, answer pushAnswer) {
	frontmatter, collection, path := answer.frontmatter, answer.collection, answer.path
	if path == "" {
		path = call.path
	}
	if frontmatter == "" {
		frontmatter = "read"
	}
	if collection == "" {
		collection = call.collection
	}
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusAccepted)
	_, _ = fmt.Fprintf(w, `{"collection":%q,"path":%q,"file":%q,"bytes":1,"created":true,"redaction":"applied","redactions":{"github_token":1},"frontmatter":%q,"jobId":"j"}`, collection, path, path, frontmatter)
}

func (s *pushServer) callsFor(path string) int {
	s.mu.Lock()
	defer s.mu.Unlock()
	count := 0
	for _, call := range s.calls {
		if call.path == path {
			count++
		}
	}
	return count
}

func (s *pushServer) total() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.calls)
}

type pushFixture struct {
	h      *harness
	config string
	notes  string
}

// newPushFixture is three documents QMD would index, and two it would not.
func newPushFixture(t *testing.T) *pushFixture {
	t.Helper()
	dir := t.TempDir()
	notes := filepath.Join(dir, "notes")
	writeFile(t, filepath.Join(notes, "a.md"), "alpha text")
	writeFile(t, filepath.Join(notes, "my dir", "b+c.md"), "beta text")
	writeFile(t, filepath.Join(notes, "sub", "d.md"), "delta text")
	writeFile(t, filepath.Join(notes, "other.txt"), "not selected")
	writeFile(t, filepath.Join(notes, "node_modules", "e.md"), "not selected")
	config := filepath.Join(dir, "index.yml")
	writeFile(t, config, fmt.Sprintf("collections:\n  notes:\n    path: %q\n", notes))
	return &pushFixture{h: newHarness(t), config: config, notes: notes}
}

func (f *pushFixture) push(server *pushServer, extra ...string) int {
	f.h.login(server.URL)
	return f.h.run(append([]string{"migrate", "push", "--qmd-config", f.config, "--apply"}, extra...)...)
}

func (f *pushFixture) result(t *testing.T, server *pushServer, extra ...string) (migrate.PushSummary, int, string) {
	t.Helper()
	code := f.push(server, append(extra, "--format", "json")...)
	var result migrate.PushResult
	if err := json.Unmarshal(f.h.stdout.Bytes(), &result); err != nil {
		t.Fatalf("stdout = %q: %v", f.h.stdout.String(), err)
	}
	if len(result.Collections) != 1 {
		t.Fatalf("result = %+v", result)
	}
	return result.Collections[0], code, result.ReportFile
}

func readProblems(t *testing.T, path string) []migrate.PushProblem {
	t.Helper()
	data, err := auth.ReadPrivateFile(path)
	if err != nil {
		t.Fatal(err)
	}
	var problems []migrate.PushProblem
	if err := json.Unmarshal(data, &problems); err != nil {
		t.Fatal(err)
	}
	return problems
}

func TestPushIsADryRunUnlessAsked(t *testing.T) {
	f := newPushFixture(t)
	if code := f.h.run("migrate", "push", "--qmd-config", f.config); code != 0 {
		t.Fatalf("with no key or server: code = %d, stderr = %q", code, f.h.stderr.String())
	}
	if out := f.h.stdout.String(); !strings.Contains(out, "Dry run") || !strings.Contains(out, "WOULD SEND") {
		t.Fatalf("stdout = %q", out)
	}
	server := newPushServer(t, nil)
	f.h.login(server.URL)
	if code := f.h.run("migrate", "push", "--qmd-config", f.config); code != 0 || server.total() != 0 {
		t.Fatalf("code = %d, calls = %d", code, server.total())
	}
}

func TestPushSendsExactlyThePlansFilesAsTheyAre(t *testing.T) {
	f := newPushFixture(t)
	server := newPushServer(t, nil)
	summary, code, _ := f.result(t, server, "--target", "notes=archive")
	checkPush(t, summary, code, 0, 3, 0)
	want := map[string]string{"a.md": "alpha text", "my dir/b+c.md": "beta text", "sub/d.md": "delta text"}
	for _, call := range server.calls {
		checkCall(t, f, call, want[call.path])
	}
	if len(server.calls) != len(want) {
		t.Fatalf("calls = %+v", server.calls)
	}
}

func checkCall(t *testing.T, f *pushFixture, call pushCall, body string) {
	t.Helper()
	info, err := os.Stat(filepath.Join(f.notes, filepath.FromSlash(call.path)))
	if err != nil {
		t.Fatal(err)
	}
	updated, err := time.Parse(time.RFC3339Nano, call.updated)
	if err != nil || !updated.Equal(info.ModTime().UTC().Truncate(time.Millisecond)) {
		t.Errorf("%s: updated = %q: %v", call.path, call.updated, err)
	}
	if body != call.body || call.collection != "archive" || call.contentType != "text/markdown" || call.length != int64(len(call.body)) {
		t.Errorf("call = %+v", call)
	}
}

// checkPush holds the three counts most runs are judged by.
func checkPush(t *testing.T, summary migrate.PushSummary, code, wantCode, sent, already int) {
	t.Helper()
	if code != wantCode || summary.Sent != sent || summary.AlreadySent != already {
		t.Fatalf("code = %d, want %d, want %d sent and %d already sent: %+v", code, wantCode, sent, already, summary)
	}
}

func checkCalls(t *testing.T, server *pushServer, path string, want int) {
	t.Helper()
	if got := server.callsFor(path); got != want {
		t.Fatalf("%s was sent %d times, want %d", path, got, want)
	}
}

func checkOneProblem(t *testing.T, report, path, status, reason string) {
	t.Helper()
	problems := readProblems(t, report)
	if len(problems) != 1 || problems[0].Path != path || problems[0].Status != status || problems[0].Reason != reason {
		t.Fatalf("problems = %+v", problems)
	}
}

func TestPushLeavesSourcesAlone(t *testing.T) {
	f := newPushFixture(t)
	file := filepath.Join(f.notes, "a.md")
	before, err := os.Stat(file)
	if err != nil {
		t.Fatal(err)
	}
	if code := f.push(newPushServer(t, nil)); code != 0 {
		t.Fatalf("code = %d", code)
	}
	after, err := os.Stat(file)
	if err != nil || !after.ModTime().Equal(before.ModTime()) || after.Size() != before.Size() {
		t.Fatalf("the source changed: %v", err)
	}
	entries, err := os.ReadDir(f.notes)
	if err != nil || len(entries) != 5 {
		t.Fatalf("the folder changed: %v, %v", entries, err)
	}
}

func TestARunResumesAndSendsOnlyWhatChanged(t *testing.T) {
	f := newPushFixture(t)
	server := newPushServer(t, nil)
	summary, code, _ := f.result(t, server)
	checkPush(t, summary, code, 0, 3, 0)
	summary, code, _ = f.result(t, server)
	checkPush(t, summary, code, 0, 0, 3)
	checkCalls(t, server, "a.md", 1)
	later := time.Now().Add(time.Minute)
	if err := os.Chtimes(filepath.Join(f.notes, "a.md"), later, later); err != nil {
		t.Fatal(err)
	}
	summary, code, _ = f.result(t, server)
	checkPush(t, summary, code, 0, 1, 2)
	checkCalls(t, server, "a.md", 2)
	summary, code, _ = f.result(t, server, "--ignore-state")
	checkPush(t, summary, code, 0, 3, 0)
	checkCalls(t, server, "a.md", 3)
}

func TestAnInterruptedRunSavesItsPlaceAndFinishesLater(t *testing.T) {
	f := newPushFixture(t)
	server := newPushServer(t, func(call pushCall, attempt int) pushAnswer {
		if call.path == "sub/d.md" && attempt == 0 {
			return pushAnswer{status: http.StatusBadGateway}
		}
		return pushAnswer{}
	})
	f.h.sleepErr = errors.New("interrupted")
	summary, code, _ := f.result(t, server, "--workers", "1")
	if code == 0 || summary.NotReached != 1 || summary.Sent != 2 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
	f.h.sleepErr = nil
	summary, code, _ = f.result(t, server, "--workers", "1")
	if code != 0 || summary.AlreadySent != 2 || summary.Sent != 1 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
}

func TestADamagedProgressFileIsNotAFreshStart(t *testing.T) {
	f := newPushFixture(t)
	server := newPushServer(t, nil)
	if code := f.push(server); code != 0 {
		t.Fatalf("code = %d", code)
	}
	state := onlyStateFile(t)
	if err := auth.WritePrivateFile(state, []byte("not json")); err != nil {
		t.Fatal(err)
	}
	if code := f.push(server); code == 0 || !strings.Contains(f.h.stderr.String(), "damaged") {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	if code := f.push(server, "--ignore-state"); code != 0 {
		t.Fatalf("code = %d", code)
	}
}

func onlyStateFile(t *testing.T) string {
	t.Helper()
	dir, err := auth.ConfigDir()
	if err != nil {
		t.Fatal(err)
	}
	found, err := filepath.Glob(filepath.Join(dir, "migrate-state", "*.json"))
	if err != nil {
		t.Fatal(err)
	}
	for _, file := range found {
		if !strings.HasSuffix(file, "-report.json") {
			return file
		}
	}
	t.Fatalf("no progress file in %v", found)
	return ""
}

func TestTheProgressFileIsPrivateAndHoldsNoText(t *testing.T) {
	f := newPushFixture(t)
	if code := f.push(newPushServer(t, nil)); code != 0 {
		t.Fatalf("code = %d", code)
	}
	data, err := auth.ReadPrivateFile(onlyStateFile(t))
	if err != nil {
		t.Fatal(err)
	}
	text := string(data)
	if strings.Contains(text, "alpha text") || !strings.Contains(text, "a.md") {
		t.Fatalf("state = %s", text)
	}
}

func TestARefusalIsReportedForThatDocumentAndNotRetried(t *testing.T) {
	for _, c := range []struct {
		status int
		code   string
		reason string
	}{
		{http.StatusBadRequest, "", "http_400"},
		{http.StatusConflict, "stale_version", "stale_version"},
		{http.StatusConflict, "path_owned", "path_owned"},
		{http.StatusConflict, "case_conflict", "case_conflict"},
		{http.StatusConflict, "deleted_during_upload", "deleted_during_upload"},
		{http.StatusUnprocessableEntity, "", "http_422"},
	} {
		t.Run(c.reason, func(t *testing.T) { checkRefusal(t, c.status, c.code, c.reason) })
	}
}

func checkRefusal(t *testing.T, status int, code, reason string) {
	t.Helper()
	f := newPushFixture(t)
	server := newPushServer(t, func(call pushCall, _ int) pushAnswer {
		if call.path == "a.md" {
			return pushAnswer{status: status, code: code}
		}
		return pushAnswer{}
	})
	summary, exit, report := f.result(t, server)
	checkPush(t, summary, exit, apperr.ExitGeneric, 2, 0)
	checkCalls(t, server, "a.md", 1)
	if summary.Refused[reason] != 1 {
		t.Fatalf("summary = %+v", summary)
	}
	checkOneProblem(t, report, "a.md", migrate.ProblemRefused, reason)
	if strings.Contains(f.h.stdout.String()+f.h.stderr.String(), "a.md") {
		t.Fatalf("a name reached the output: %q %q", f.h.stdout.String(), f.h.stderr.String())
	}
}

func TestARefusedDocumentIsTriedAgainNextRun(t *testing.T) {
	f := newPushFixture(t)
	refuse := true
	var mu sync.Mutex
	server := newPushServer(t, func(call pushCall, _ int) pushAnswer {
		mu.Lock()
		defer mu.Unlock()
		if refuse && call.path == "a.md" {
			return pushAnswer{status: http.StatusBadRequest}
		}
		return pushAnswer{}
	})
	if _, code, _ := f.result(t, server); code != apperr.ExitGeneric {
		t.Fatalf("code = %d", code)
	}
	mu.Lock()
	refuse = false
	mu.Unlock()
	summary, code, report := f.result(t, server)
	if code != 0 || summary.Sent != 1 || summary.AlreadySent != 2 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
	if problems := readProblems(t, report); len(problems) != 0 {
		t.Fatalf("an old problem stayed in the report: %+v", problems)
	}
}

func TestAStopEndsTheRunClearlyAndResumes(t *testing.T) {
	for _, c := range []struct {
		status int
		code   string
	}{
		{http.StatusForbidden, ""},
		{http.StatusUnauthorized, ""},
		{http.StatusNotFound, ""},
		{http.StatusConflict, "ingest_not_enabled"},
		{http.StatusConflict, "collection_not_empty"},
	} {
		t.Run(fmt.Sprintf("%d %s", c.status, c.code), func(t *testing.T) { checkStop(t, c.status, c.code) })
	}
}

func checkStop(t *testing.T, status int, reason string) {
	t.Helper()
	f := newPushFixture(t)
	var refusing atomic.Bool
	refusing.Store(true)
	server := newPushServer(t, func(pushCall, int) pushAnswer {
		if refusing.Load() {
			return pushAnswer{status: status, code: reason}
		}
		return pushAnswer{}
	})
	summary, code, _ := f.result(t, server, "--workers", "1")
	if code == 0 || server.total() != 1 || summary.NotReached != 3 || summary.Sent != 0 {
		t.Fatalf("code = %d, calls = %d, summary = %+v", code, server.total(), summary)
	}
	if !strings.Contains(f.h.stderr.String(), "collection notes") || !strings.Contains(f.h.stderr.String(), "Nothing else was sent") {
		t.Fatalf("stderr = %q", f.h.stderr.String())
	}
	refusing.Store(false)
	summary, code, _ = f.result(t, server)
	checkPush(t, summary, code, 0, 3, 0)
}

func TestAnAnswerThatAsksForATimeIsWaitedFor(t *testing.T) {
	for _, c := range []struct {
		name   string
		answer pushAnswer
		want   time.Duration
		says   string
	}{
		{"in progress", pushAnswer{status: http.StatusConflict, code: "ingest_in_progress", retryAfter: "7"}, 7 * time.Second, "A write of this document is still running on the server; waiting 7s."},
		{"in progress, no time named", pushAnswer{status: http.StatusConflict, code: "ingest_in_progress"}, time.Second, "still running on the server; waiting 1s."},
		{"busy", pushAnswer{status: http.StatusServiceUnavailable, code: "ingest_busy", retryAfter: "9"}, 0, "The server is busy with other large documents, so every worker waits; its Retry-After asks for 9s; waiting"},
		{"rate limited", pushAnswer{status: http.StatusTooManyRequests, retryAfter: "11"}, 0, "Rate limited (HTTP 429): this key has used its requests for now, so every worker waits; its Retry-After asks for 11s; waiting 11s."},
		{"unavailable", pushAnswer{status: http.StatusBadGateway}, time.Second, "The server is unavailable; waiting 1s."},
	} {
		t.Run(c.name, func(t *testing.T) { checkWait(t, c.answer, c.want, c.says) })
	}
}

func checkWait(t *testing.T, answer pushAnswer, want time.Duration, says string) {
	t.Helper()
	f := newPushFixture(t)
	server := newPushServer(t, func(call pushCall, attempt int) pushAnswer {
		if call.path == "a.md" && attempt == 0 {
			return answer
		}
		return pushAnswer{}
	})
	summary, code, _ := f.result(t, server, "--workers", "1")
	checkPush(t, summary, code, 0, 3, 0)
	checkCalls(t, server, "a.md", 2)
	checkSlept(t, f.h.slept, want)
	// A wait says why, so a rate limit never reads as a server that is down.
	if out := f.h.stderr.String(); !strings.Contains(out, says) || strings.Contains(out, "not ready") {
		t.Fatalf("stderr = %q, want it to say %q", out, says)
	}
}

// checkSlept takes a want of 0 for a pause, which is held until its time passes, and this fake sleep returns at once.
func checkSlept(t *testing.T, slept []time.Duration, want time.Duration) {
	t.Helper()
	if len(slept) == 0 {
		t.Fatal("it did not wait")
	}
	if want == 0 {
		if slept[0] <= 7*time.Second {
			t.Fatalf("a pause did not honour the time named: %v", slept)
		}
		return
	}
	if len(slept) != 1 || slept[0] != want {
		t.Fatalf("slept = %v, want %v", slept, want)
	}
}

func TestABusyServerPausesEveryWorker(t *testing.T) {
	f := newPushFixture(t)
	f.h.login("http://127.0.0.1:1")
	a := f.h.app()
	run, cancel := newPushRun(t.Context(), a, nil, migratePushOptions{workers: 2})
	defer cancel()
	run.pauseFor(time.Hour, "held for the test")
	if err := run.waitForPause(); err != nil {
		t.Fatal(err)
	}
	if len(f.h.slept) != 1 || f.h.slept[0] < 59*time.Minute {
		t.Fatalf("another worker did not wait: %v", f.h.slept)
	}
}

func TestALostAnswerIsSentAgainAndCountedOnce(t *testing.T) {
	for _, c := range []struct {
		name   string
		answer pushAnswer
	}{
		{"connection dropped", pushAnswer{drop: true}},
		{"502", pushAnswer{status: http.StatusBadGateway}},
		{"503", pushAnswer{status: http.StatusServiceUnavailable}},
		{"504", pushAnswer{status: http.StatusGatewayTimeout}},
		{"500", pushAnswer{status: http.StatusInternalServerError}},
		{"408", pushAnswer{status: http.StatusRequestTimeout}},
	} {
		t.Run(c.name, func(t *testing.T) {
			f := newPushFixture(t)
			var updated sync.Map
			server := newPushServer(t, func(call pushCall, attempt int) pushAnswer {
				if earlier, seen := updated.LoadOrStore(call.path, call.updated); seen && earlier != call.updated {
					t.Errorf("%s was sent with a different updated", call.path)
				}
				if attempt == 0 {
					return c.answer
				}
				return pushAnswer{}
			})
			summary, code, _ := f.result(t, server)
			if code != 0 || summary.Sent != 3 || server.total() != 6 {
				t.Fatalf("code = %d, summary = %+v, calls = %d, stderr = %q", code, summary, server.total(), f.h.stderr.String())
			}
		})
	}
}

func TestAServerThatNeverAnswersEndsTheRunAndSaysSo(t *testing.T) {
	f := newPushFixture(t)
	server := newPushServer(t, func(pushCall, int) pushAnswer { return pushAnswer{status: http.StatusBadGateway} })
	summary, code, _ := f.result(t, server, "--workers", "1")
	if code != apperr.ExitUnavailable || summary.NotReached != 3 || server.total() != resendAttempts {
		t.Fatalf("code = %d, summary = %+v, calls = %d", code, summary, server.total())
	}
}

func TestAFailureOfOneDocumentCannotEndTheRunForever(t *testing.T) {
	f := newPushFixture(t)
	server := newPushServer(t, func(call pushCall, _ int) pushAnswer {
		if call.path == "a.md" {
			return pushAnswer{status: http.StatusInternalServerError}
		}
		return pushAnswer{}
	})
	summary, code, _ := f.result(t, server)
	if code != apperr.ExitGeneric || summary.Refused["http_500"] != 1 || summary.Sent != 2 || server.callsFor("a.md") != reportAttempts {
		t.Fatalf("code = %d, summary = %+v, calls = %d", code, summary, server.callsFor("a.md"))
	}
}

func TestADocumentThatStaysInProgressIsReported(t *testing.T) {
	f := newPushFixture(t)
	server := newPushServer(t, func(call pushCall, _ int) pushAnswer {
		if call.path == "a.md" {
			return pushAnswer{status: http.StatusConflict, code: "ingest_in_progress", retryAfter: "60"}
		}
		return pushAnswer{}
	})
	summary, code, _ := f.result(t, server)
	// 6 minutes of 60-second waits outlasts the server's 5-minute lease.
	if code != apperr.ExitGeneric || summary.Refused["ingest_in_progress"] != 1 || summary.Sent != 2 || server.callsFor("a.md") != 7 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
}

func TestAServerThatStaysBusyEndsTheRun(t *testing.T) {
	f := newPushFixture(t)
	server := newPushServer(t, func(pushCall, int) pushAnswer {
		return pushAnswer{status: http.StatusServiceUnavailable, code: "ingest_busy", retryAfter: "3600"}
	})
	summary, code, _ := f.result(t, server, "--workers", "1")
	if code != apperr.ExitUnavailable || summary.NotReached != 3 || server.total() != 1 {
		t.Fatalf("a long wait held the terminal: code = %d, summary = %+v, calls = %d", code, summary, server.total())
	}
}

func TestAnOverCapDocumentFailsTheExitUnlessAcknowledged(t *testing.T) {
	f := newPushFixture(t)
	server := newPushServer(t, func(call pushCall, _ int) pushAnswer {
		if call.path == "a.md" {
			return pushAnswer{status: http.StatusRequestEntityTooLarge}
		}
		return pushAnswer{}
	})
	summary, code, report := f.result(t, server)
	checkPush(t, summary, code, apperr.ExitGeneric, 2, 0)
	checkCalls(t, server, "a.md", 1)
	checkOneProblem(t, report, "a.md", migrate.ProblemOverCap, "")
	if summary.OverCap != 1 {
		t.Fatalf("summary = %+v", summary)
	}
	summary, code, _ = f.result(t, server, "--accept-over-cap")
	checkPush(t, summary, code, 0, 0, 2)
	if summary.OverCap != 1 {
		t.Fatalf("acknowledged, it stopped being counted: %+v", summary)
	}
	if code := f.push(server, "--accept-over-cap"); code != 0 || !strings.Contains(f.h.stdout.String(), "NOT everything was stored") {
		t.Fatalf("an acknowledged gap read as clean: %q", f.h.stdout.String())
	}
}

func TestAcceptingOverCapDoesNotExcuseARefusal(t *testing.T) {
	f := newPushFixture(t)
	server := newPushServer(t, func(call pushCall, _ int) pushAnswer {
		if call.path == "a.md" {
			return pushAnswer{status: http.StatusBadRequest}
		}
		return pushAnswer{}
	})
	if code := f.push(server, "--accept-over-cap", "--accept-skips"); code != apperr.ExitGeneric {
		t.Fatalf("code = %d", code)
	}
}

func TestFrontmatterIssuesAreCountedEvenOnALaterRun(t *testing.T) {
	f := newPushFixture(t)
	server := newPushServer(t, func(call pushCall, _ int) pushAnswer {
		if call.path == "a.md" {
			return pushAnswer{frontmatter: "past_window"}
		}
		return pushAnswer{}
	})
	for range 2 {
		summary, code, report := f.result(t, server)
		if code != 0 || summary.Frontmatter["past_window"] != 1 {
			t.Fatalf("code = %d, summary = %+v", code, summary)
		}
		checkOneProblem(t, report, "a.md", migrate.ProblemFrontmatter, "past_window")
	}
	if code := f.push(server); code != 0 || !strings.Contains(f.h.stdout.String(), "match no date") || !strings.Contains(f.h.stdout.String(), "past_window") {
		t.Fatalf("stdout = %q", f.h.stdout.String())
	}
}

func TestA202ForAnotherCollectionIsNotRecordedAsStored(t *testing.T) {
	f := newPushFixture(t)
	server := newPushServer(t, func(pushCall, int) pushAnswer { return pushAnswer{collection: "elsewhere"} })
	summary, code, _ := f.result(t, server, "--workers", "1")
	if code == 0 || summary.Sent != 0 || summary.NotReached != 3 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
}

func TestAFileThatCannotBeReadIsCountedAndFails(t *testing.T) {
	f := newPushFixture(t)
	server := newPushServer(t, nil)
	f.h.login(server.URL)
	s, err := f.h.app().connect()
	if err != nil {
		t.Fatal(err)
	}
	run, cancel := newPushRun(t.Context(), f.h.app(), s, migratePushOptions{workers: 1})
	defer cancel()
	root, err := os.OpenRoot(f.notes)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = root.Close() }()
	state, err := migrate.LoadPushState("o", "notes", "notes", true)
	if err != nil {
		t.Fatal(err)
	}
	c := &pushCollection{job: pushJob{target: "notes", paths: []string{"gone.md"}}, root: root, state: state, summary: migrate.PushSummary{Selected: 1, Refused: map[string]int{}}, finished: map[string]bool{}}
	run.document(c, "gone.md")
	if c.summary.Unreadable != 1 || !c.summary.Accounted() || c.problems[0].Status != migrate.ProblemUnreadable {
		t.Fatalf("summary = %+v, problems = %+v", c.summary, c.problems)
	}
}

func TestACollectionThatCannotBeCountedStopsBeforeAnythingIsSent(t *testing.T) {
	f := newPushFixture(t)
	writeFile(t, f.config, fmt.Sprintf("collections:\n  notes:\n    path: %q\n    pattern: \"**/*.@(md)\"\n", f.notes))
	server := newPushServer(t, nil)
	if code := f.push(server); code != apperr.ExitUsage || server.total() != 0 {
		t.Fatalf("code = %d, calls = %d", code, server.total())
	}
}

func TestAFolderThatCouldNotBeReadFailsUnlessAcknowledged(t *testing.T) {
	f := newPushFixture(t)
	writeFile(t, filepath.Join(f.notes, "secretfolder", "hidden.md"), "text")
	denyFolder(t, filepath.Join(f.notes, "secretfolder"))
	server := newPushServer(t, nil)
	summary, code, _ := f.result(t, server)
	if code != apperr.ExitGeneric || summary.FoldersSkipped != 1 || summary.Sent != 3 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
	if code := f.push(server, "--accept-skips"); code != 0 || !strings.Contains(f.h.stdout.String(), "NOT everything") {
		t.Fatalf("code = %d, stdout = %q", code, f.h.stdout.String())
	}
	if strings.Contains(f.h.stdout.String()+f.h.stderr.String(), "secretfolder") {
		t.Fatalf("named: %q", f.h.stderr.String())
	}
	_, _, report := f.result(t, server, "--accept-skips")
	checkOneProblem(t, report, "secretfolder", migrate.ProblemUnreadable, "")
}

func TestPushRunsFewWorkersWithNoFlag(t *testing.T) {
	f := newPushFixture(t)
	for i := range 40 {
		writeFile(t, filepath.Join(f.notes, fmt.Sprintf("many-%d.md", i)), "text")
	}
	server := newPushServer(t, nil)
	if code := f.push(server); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if got := newMigratePushCmd(f.h.app()).Flags().Lookup("workers").DefValue; got != "4" {
		t.Fatalf("the default is %s workers", got)
	}
	if server.most > defaultPushWorkers || server.most < 2 {
		t.Fatalf("at most %d at once", server.most)
	}
	for _, workers := range []string{"0", "17"} {
		if code := f.h.run("migrate", "push", "--qmd-config", f.config, "--apply", "--workers", workers); code != apperr.ExitUsage {
			t.Errorf("--workers %s: code = %d", workers, code)
		}
	}
}

func TestVerboseNeverNamesAFile(t *testing.T) {
	f := newPushFixture(t)
	if code := f.push(newPushServer(t, nil), "--verbose"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if text := f.h.stderr.String(); !strings.Contains(text, "> POST /v1/ingest/rendered") || strings.Contains(text, "a.md") {
		t.Fatalf("stderr = %q", text)
	}
}

func TestTheSummarySaysWhenEverythingIsStoredAndNotOtherwise(t *testing.T) {
	f := newPushFixture(t)
	server := newPushServer(t, nil)
	if code := f.push(server); code != 0 || !strings.Contains(f.h.stdout.String(), "Every selected document is stored") {
		t.Fatalf("stdout = %q", f.h.stdout.String())
	}
	if code := f.push(server, "--format", "markdown"); code != 0 || !strings.Contains(f.h.stdout.String(), "3 already sent") {
		t.Fatalf("stdout = %q", f.h.stdout.String())
	}
}

func TestCountsThatDoNotAddUpAreAnError(t *testing.T) {
	result := &migrate.PushResult{Collections: []migrate.PushSummary{{Name: "n", Selected: 2, Sent: 1}}}
	if err := pushOutcome(result, migratePushOptions{}); err == nil || !strings.Contains(err.Error(), "do not add up") {
		t.Fatalf("err = %v", err)
	}
}

func TestPlanCountsWhatCanNeverBeStoredAndFails(t *testing.T) {
	f := newPushFixture(t)
	writeFile(t, filepath.Join(f.notes, "x~1.md"), "text")
	writeFile(t, filepath.Join(f.notes, "aux.md"), "text")
	if code := f.h.run("migrate", "plan", "--qmd-config", f.config, "--format", "json"); code != apperr.ExitGeneric || !strings.Contains(f.h.stderr.String(), "2 selected") {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	var result migratePlanResult
	if err := json.Unmarshal(f.h.stdout.Bytes(), &result); err != nil {
		t.Fatal(err)
	}
	if got := result.Collections[0].Unstorable; got[migrate.ReasonShortName] != 1 || got[migrate.ReasonDeviceName] != 1 {
		t.Fatalf("unstorable = %v", got)
	}
}

func TestPlansTextFormsSayWhyAndNameNoFile(t *testing.T) {
	f := newPushFixture(t)
	writeFile(t, filepath.Join(f.notes, "x~1.md"), "text")
	if code := f.h.run("migrate", "plan", "--qmd-config", f.config, "--accept-skips"); code != apperr.ExitGeneric || !strings.Contains(f.h.stdout.String(), "windows_short_name") {
		t.Fatalf("code = %d, stdout = %q", code, f.h.stdout.String())
	}
	if strings.Contains(f.h.stdout.String()+f.h.stderr.String(), "x~1") {
		t.Fatalf("named: %q", f.h.stdout.String())
	}
	if code := f.h.run("migrate", "plan", "--qmd-config", f.config, "--format", "markdown"); code != apperr.ExitGeneric || !strings.Contains(f.h.stdout.String(), "cannot be stored") {
		t.Fatalf("markdown: code = %d, stdout = %q", code, f.h.stdout.String())
	}
}

func TestADryRunSaysWhatCannotBeStored(t *testing.T) {
	f := newPushFixture(t)
	writeFile(t, filepath.Join(f.notes, "x~1.md"), "text")
	if code := f.h.run("migrate", "push", "--qmd-config", f.config); code != apperr.ExitGeneric || !strings.Contains(f.h.stdout.String(), "windows_short_name") {
		t.Fatalf("code = %d, stdout = %q", code, f.h.stdout.String())
	}
}

func TestAPathTheServerWouldRefuseIsStillItsToRefuse(t *testing.T) {
	f := newPushFixture(t)
	writeFile(t, filepath.Join(f.notes, "x~1.md"), "text")
	server := newPushServer(t, func(call pushCall, _ int) pushAnswer {
		if call.path == "x~1.md" {
			return pushAnswer{status: http.StatusBadRequest, code: "invalid_path"}
		}
		return pushAnswer{}
	})
	summary, code, _ := f.result(t, server)
	if code != apperr.ExitGeneric || summary.Refused["invalid_path"] != 1 || server.callsFor("x~1.md") != 1 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
}

func TestAReSendReadsTheNewestCopy(t *testing.T) {
	f := newPushFixture(t)
	file := filepath.Join(f.notes, "a.md")
	newest := time.Now().Add(-time.Minute).UTC().Truncate(time.Millisecond)
	server := newPushServer(t, func(call pushCall, attempt int) pushAnswer {
		if call.path != "a.md" || attempt != 0 {
			return pushAnswer{}
		}
		replaceFile(t, file, "the newest copy", newest)
		return pushAnswer{drop: true}
	})
	summary, code, _ := f.result(t, server, "--workers", "1")
	checkPush(t, summary, code, 0, 3, 0)
	last := server.calls[1]
	updated, err := time.Parse(time.RFC3339Nano, last.updated)
	if err != nil || last.path != "a.md" || last.body != "the newest copy" || !updated.Equal(newest) {
		t.Fatalf("the re-send was not the newest copy: %+v, %v", last, err)
	}
	summary, code, _ = f.result(t, server)
	checkPush(t, summary, code, 0, 0, 3)
}

func replaceFile(t *testing.T, file, text string, when time.Time) {
	t.Helper()
	if err := os.WriteFile(file, []byte(text), 0o600); err != nil {
		t.Error(err)
	}
	if err := os.Chtimes(file, when, when); err != nil {
		t.Error(err)
	}
}

func TestACopyOlderThanTheOneSentIsNotSent(t *testing.T) {
	f := newPushFixture(t)
	server := newPushServer(t, nil)
	if _, code, _ := f.result(t, server); code != 0 {
		t.Fatalf("code = %d", code)
	}
	older := time.Date(2020, 1, 1, 0, 0, 0, 0, time.UTC)
	if err := os.Chtimes(filepath.Join(f.notes, "a.md"), older, older); err != nil {
		t.Fatal(err)
	}
	summary, code, report := f.result(t, server)
	if code != apperr.ExitGeneric || summary.Refused[migrate.ReasonOlderThanSent] != 1 || server.callsFor("a.md") != 1 {
		t.Fatalf("code = %d, summary = %+v, calls = %d", code, summary, server.callsFor("a.md"))
	}
	if problems := readProblems(t, report); len(problems) != 1 || problems[0].Reason != migrate.ReasonOlderThanSent {
		t.Fatalf("problems = %+v", problems)
	}
}

func TestReportFailsOnANameThatLooksLikeACredential(t *testing.T) {
	f := newMigrateFixture(t)
	named := `{"files":1,"changed":0,"refused":0,"skipped":0,"credentialNames":1,"replacements":{},"filesByRule":{},"gitleaksRan":false,"gitleaksFindings":{}}`
	f.fakeCheck("["+named+","+cleanCheck+"]", nil)
	if code := f.report(); code != apperr.ExitGeneric || !strings.Contains(f.h.stderr.String(), "looks like a credential") {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
}

func TestATakenOverPathCountsAsMigratedOnlyWhenTheServerSaysSo(t *testing.T) {
	for _, reason := range []string{"path_owned", "stale_version"} {
		t.Run(reason, func(t *testing.T) { checkTakeover(t, reason) })
	}
}

func checkTakeover(t *testing.T, reason string) {
	t.Helper()
	f := newPushFixture(t)
	server := newPushServer(t, func(call pushCall, _ int) pushAnswer {
		if call.path == "a.md" {
			return pushAnswer{status: http.StatusConflict, code: reason}
		}
		return pushAnswer{}
	})
	server.state.Store(migrate.StateTakenOver)
	summary, code, report := f.result(t, server)
	checkPush(t, summary, code, 0, 2, 0)
	if summary.TakenOver != 1 || summary.RefusedTotal() != 0 {
		t.Fatalf("summary = %+v", summary)
	}
	if problems := readProblems(t, report); len(problems) != 0 {
		t.Fatalf("problems = %+v", problems)
	}
	out := ""
	if code := f.push(server); code != 0 {
		t.Fatalf("code = %d", code)
	}
	out = f.h.stdout.String()
	if strings.Contains(out, "Every selected document is stored") || !strings.Contains(out, "0 stored") || !strings.Contains(out, "3 taken over") {
		t.Fatalf("stdout = %q", out)
	}
}

func TestAnOwnedPathThatWasNotTakenOverStaysAProblem(t *testing.T) {
	for name, state := range map[string]any{"complete": "complete", "none": "none", "no answer": "unanswered"} {
		t.Run(name, func(t *testing.T) {
			f := newPushFixture(t)
			server := newPushServer(t, func(call pushCall, _ int) pushAnswer {
				if call.path == "a.md" {
					return pushAnswer{status: http.StatusConflict, code: "path_owned"}
				}
				return pushAnswer{}
			})
			server.state.Store(state)
			summary, code, report := f.result(t, server)
			checkPush(t, summary, code, apperr.ExitGeneric, 2, 0)
			checkOneProblem(t, report, "a.md", migrate.ProblemRefused, "path_owned")
			if summary.TakenOver != 0 {
				t.Fatalf("summary = %+v", summary)
			}
		})
	}
}

func TestOtherRefusalsDoNotAskWhetherAPathWasTakenOver(t *testing.T) {
	f := newPushFixture(t)
	server := newPushServer(t, func(call pushCall, _ int) pushAnswer {
		if call.path == "a.md" {
			return pushAnswer{status: http.StatusConflict, code: "case_conflict"}
		}
		return pushAnswer{}
	})
	server.state.Store(migrate.StateTakenOver)
	summary, code, _ := f.result(t, server)
	checkPush(t, summary, code, apperr.ExitGeneric, 2, 0)
	if summary.TakenOver != 0 {
		t.Fatalf("summary = %+v", summary)
	}
}

func TestADecomposedNameIsSentInComposedForm(t *testing.T) {
	f := newPushFixture(t)
	writeFile(t, filepath.Join(f.notes, "cafe\u0301.md"), "text")
	entries, err := os.ReadDir(f.notes)
	if err != nil {
		t.Fatal(err)
	}
	if !slices.ContainsFunc(entries, func(entry os.DirEntry) bool { return entry.Name() == "cafe\u0301.md" }) {
		t.Skip("this filesystem composes names itself")
	}
	server := newPushServer(t, nil)
	summary, code, _ := f.result(t, server)
	checkPush(t, summary, code, 0, 4, 0)
	checkCalls(t, server, "caf\u00e9.md", 1)
}

func TestASecondSpellingIsRefusedAndOverwritesNothing(t *testing.T) {
	f := newPushFixture(t)
	writeFile(t, filepath.Join(f.notes, "cafe\u0301.md"), "decomposed copy")
	writeFile(t, filepath.Join(f.notes, "caf\u00e9.md"), "composed copy")
	if entries := namesIn(t, f.notes); !slices.Contains(entries, "cafe\u0301.md") || !slices.Contains(entries, "caf\u00e9.md") {
		t.Skip("this filesystem keeps one spelling")
	}
	server := newPushServer(t, nil)
	summary, code, report := f.result(t, server)
	checkPush(t, summary, code, apperr.ExitGeneric, 4, 0)
	checkCalls(t, server, "caf\u00e9.md", 1)
	if summary.Refused["second_spelling"] != 1 {
		t.Fatalf("summary = %+v", summary)
	}
	problems := readProblems(t, report)
	if len(problems) != 1 || problems[0].Reason != "second_spelling" {
		t.Fatalf("problems = %+v", problems)
	}
}

func namesIn(t *testing.T, dir string) []string {
	t.Helper()
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	names := make([]string, 0, len(entries))
	for _, entry := range entries {
		names = append(names, entry.Name())
	}
	return names
}

func TestAServerThatLostItAllGetsEverythingAgain(t *testing.T) {
	f := newPushFixture(t)
	server := newPushServer(t, nil)
	summary, code, _ := f.result(t, server)
	checkPush(t, summary, code, 0, 3, 0)
	server.forget()
	summary, code, _ = f.result(t, server)
	checkPush(t, summary, code, 0, 3, 0)
	checkCalls(t, server, "a.md", 2)
}

func TestAHandEditedOrDeletedDocumentIsNotCalledStored(t *testing.T) {
	f := newPushFixture(t)
	server := newPushServer(t, nil)
	summary, code, _ := f.result(t, server)
	checkPush(t, summary, code, 0, 3, 0)
	server.mu.Lock()
	server.ledger["a.md"] = "2001-01-01T00:00:00Z"
	delete(server.ledger, "sub/d.md")
	server.mu.Unlock()
	summary, code, _ = f.result(t, server)
	checkPush(t, summary, code, 0, 2, 1)
	checkCalls(t, server, "a.md", 2)
	checkCalls(t, server, "sub/d.md", 2)
	checkCalls(t, server, "my dir/b+c.md", 1)
}

func TestAStateCheckRidesOutTheServer(t *testing.T) {
	for name, answer := range map[string]pushAnswer{
		"lost":  {status: http.StatusBadGateway},
		"limit": {status: http.StatusTooManyRequests, retryAfter: "3"},
	} {
		t.Run(name, func(t *testing.T) {
			f := newPushFixture(t)
			server := newPushServer(t, nil)
			summary, code, _ := f.result(t, server)
			checkPush(t, summary, code, 0, 3, 0)
			server.statusFaults(answer, 2)
			summary, code, _ = f.result(t, server, "--workers", "1")
			checkPush(t, summary, code, 0, 0, 3)
			checkCalls(t, server, "a.md", 1)
		})
	}
}

func TestAStateCheckRefusedStopsTheRunAndClaimsNothing(t *testing.T) {
	f := newPushFixture(t)
	server := newPushServer(t, nil)
	summary, code, _ := f.result(t, server)
	checkPush(t, summary, code, 0, 3, 0)
	server.statusFaults(pushAnswer{status: http.StatusForbidden}, 100)
	summary, code, _ = f.result(t, server, "--workers", "1")
	if code == 0 || summary.AlreadySent != 0 || summary.NotReached != 3 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
}

func TestAStopClaimsNothingAboutWhatItDidNotCheck(t *testing.T) {
	f := newPushFixture(t)
	server := newPushServer(t, nil)
	summary, code, _ := f.result(t, server)
	checkPush(t, summary, code, 0, 3, 0)
	server.statusFaults(pushAnswer{status: http.StatusBadGateway}, 1000)
	summary, code, _ = f.result(t, server, "--workers", "1")
	if code != apperr.ExitUnavailable || summary.AlreadySent != 0 || summary.NotReached != 3 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
}

func TestA202ForAnotherPathIsNotRecordedAsStored(t *testing.T) {
	f := newPushFixture(t)
	server := newPushServer(t, func(pushCall, int) pushAnswer { return pushAnswer{path: "elsewhere.md"} })
	summary, code, _ := f.result(t, server, "--workers", "1")
	if code == 0 || summary.Sent != 0 || summary.NotReached != 3 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
}

func TestEachKindOfAnswerHasItsOwnBudget(t *testing.T) {
	for name, script := range map[string][]pushAnswer{
		"limits then a 500":              {{status: 429}, {status: 429}, {status: 429}, {status: 500}, {}},
		"writes in progress then a loss": repeatThen(pushAnswer{status: 409, code: "ingest_in_progress"}, 8, pushAnswer{drop: true}),
		"limits past the resend budget":  repeatThen(pushAnswer{status: 429}, 20, pushAnswer{status: 502}),
	} {
		t.Run(name, func(t *testing.T) { checkBudget(t, script) })
	}
}

func repeatThen(answer pushAnswer, times int, last pushAnswer) []pushAnswer {
	script := make([]pushAnswer, 0, times+1)
	for range times {
		script = append(script, answer)
	}
	return append(script, last)
}

func checkBudget(t *testing.T, script []pushAnswer) {
	t.Helper()
	f := newPushFixture(t)
	server := newPushServer(t, func(call pushCall, attempt int) pushAnswer {
		if call.path == "a.md" && attempt < len(script) {
			return script[attempt]
		}
		return pushAnswer{}
	})
	summary, code, _ := f.result(t, server, "--workers", "1")
	checkPush(t, summary, code, 0, 3, 0)
}

func TestNothingSelectedSaysSo(t *testing.T) {
	dir := t.TempDir()
	writeFile(t, filepath.Join(dir, "notes", "a.txt"), "not markdown")
	config := filepath.Join(dir, "index.yml")
	writeFile(t, config, fmt.Sprintf("collections:\n  notes:\n    path: %q\n", filepath.Join(dir, "notes")))
	h := newHarness(t)
	h.login(newPushServer(t, nil).URL)
	if code := h.run("migrate", "push", "--qmd-config", config, "--apply"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if out := h.stdout.String(); !strings.Contains(out, "Nothing was selected") || strings.Contains(out, "Every selected") {
		t.Fatalf("stdout = %q", out)
	}
}

func TestPlanCountsWhatOnlyThisMachineCanSee(t *testing.T) {
	f := newPushFixture(t)
	writeFile(t, filepath.Join(f.notes, "latin.md"), "caf\xe9")
	ahead := filepath.Join(f.notes, "ahead.md")
	writeFile(t, ahead, "text")
	later := time.Now().Add(2 * time.Hour)
	if err := os.Chtimes(ahead, later, later); err != nil {
		t.Fatal(err)
	}
	if code := f.h.run("migrate", "plan", "--qmd-config", f.config, "--format", "json"); code != apperr.ExitGeneric {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	var result migratePlanResult
	if err := json.Unmarshal(f.h.stdout.Bytes(), &result); err != nil {
		t.Fatal(err)
	}
	if got := result.Collections[0].Unstorable; got[migrate.ReasonNotUTF8] != 1 || got[migrate.ReasonFutureTime] != 1 {
		t.Fatalf("unstorable = %v", got)
	}
}

func TestABlankFileIsCountedAndNeitherSentNorAFailure(t *testing.T) {
	f := newPushFixture(t)
	writeFile(t, filepath.Join(f.notes, "empty.md"), "")
	writeFile(t, filepath.Join(f.notes, "spaces.md"), " \n\t\n")
	if code := f.h.run("migrate", "plan", "--qmd-config", f.config); code != 0 || !strings.Contains(f.h.stdout.String(), "2 blank file(s)") {
		t.Fatalf("plan: code = %d, stdout = %q", code, f.h.stdout.String())
	}
	server := newPushServer(t, nil)
	summary, code, report := f.result(t, server)
	checkPush(t, summary, code, 0, 3, 0)
	if summary.Blank != 2 || summary.Selected != 3 || server.callsFor("empty.md") != 0 {
		t.Fatalf("summary = %+v", summary)
	}
	if problems := readProblems(t, report); len(problems) != 2 || problems[0].Status != migrate.ProblemBlank {
		t.Fatalf("problems = %+v", problems)
	}
}

func TestALocalEditAfterATakeoverIsNotCalledStored(t *testing.T) {
	f := newPushFixture(t)
	server := newPushServer(t, nil)
	summary, code, _ := f.result(t, server)
	checkPush(t, summary, code, 0, 3, 0)
	server.state.Store(migrate.StateTakenOver)
	summary, code, _ = f.result(t, server)
	if code != 0 || summary.TakenOver != 3 || summary.TakenOverEdited != 0 || server.total() != 3 {
		t.Fatalf("unchanged, and taken over: code = %d, calls = %d, summary = %+v", code, server.total(), summary)
	}
	checkEditAfterTakeover(t, f, server)
}

func checkEditAfterTakeover(t *testing.T, f *pushFixture, server *pushServer) {
	t.Helper()
	later := time.Now().Add(time.Minute)
	if err := os.Chtimes(filepath.Join(f.notes, "a.md"), later, later); err != nil {
		t.Fatal(err)
	}
	server.answer = func(call pushCall, _ int) pushAnswer {
		if call.path == "a.md" {
			return pushAnswer{status: http.StatusConflict, code: "path_owned"}
		}
		return pushAnswer{}
	}
	summary, code, report := f.result(t, server)
	if code != 0 || summary.TakenOver != 3 || summary.TakenOverEdited != 1 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
	checkOneProblem(t, report, "a.md", migrate.ProblemLocalEdits, "")
	if code := f.push(server); code != 0 || !strings.Contains(f.h.stdout.String(), "1 of those have local changes") {
		t.Fatalf("code = %d, stdout = %q", code, f.h.stdout.String())
	}
}

// statusFaults makes the next GETs answer this.
func (s *pushServer) statusFaults(answer pushAnswer, times int) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.faultAnswer, s.faultsLeft = answer, times
}

func (s *pushServer) takeStatusFault() (pushAnswer, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.faultsLeft == 0 {
		return pushAnswer{}, false
	}
	s.faultsLeft--
	return s.faultAnswer, true
}

func TestAnOlderCopyGoesWhenTheServerHoldsNothing(t *testing.T) {
	f := newPushFixture(t)
	server := newPushServer(t, nil)
	summary, code, _ := f.result(t, server)
	checkPush(t, summary, code, 0, 3, 0)
	older := time.Date(2020, 1, 1, 0, 0, 0, 0, time.UTC)
	if err := os.Chtimes(filepath.Join(f.notes, "a.md"), older, older); err != nil {
		t.Fatal(err)
	}
	server.forget()
	summary, code, _ = f.result(t, server)
	checkPush(t, summary, code, 0, 3, 0)
	checkCalls(t, server, "a.md", 2)
}

func TestAWaitIsShownRounded(t *testing.T) {
	for wait, want := range map[time.Duration]string{
		10*time.Second + 999*time.Millisecond: "11s",
		11*time.Second - 3*time.Microsecond:   "11s",
		time.Minute:                           "1m0s",
		250*time.Millisecond + 40:             "250ms",
	} {
		if got := shownWait(wait); got != want {
			t.Errorf("shownWait(%v) = %q, want %q", wait, got, want)
		}
	}
}

func TestEveryWaitSaysWhatItIsFor(t *testing.T) {
	for _, c := range []struct {
		action migrate.Action
		named  time.Duration
		want   string
	}{
		{migrate.ActionHold, 0, "Rate limited (HTTP 429): this key has used its requests for now, so every worker waits"},
		{migrate.ActionHold, time.Minute, "Rate limited (HTTP 429): this key has used its requests for now, so every worker waits; its Retry-After asks for 1m0s"},
		{migrate.ActionPause, 0, "The server is busy with other large documents, so every worker waits"},
		{migrate.ActionResendThenReport, 0, "The server failed this request, or it arrived too slowly"},
		{migrate.ActionResend, time.Second, "The server is unavailable; its Retry-After asks for 1s"},
	} {
		if got := waitReason(c.action, c.named); got != c.want {
			t.Errorf("waitReason(%v, %v) = %q, want %q", c.action, c.named, got, c.want)
		}
	}
}
