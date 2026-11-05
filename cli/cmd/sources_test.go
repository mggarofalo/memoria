package cmd

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/sources"
)

const storedBody = `{"collection":"safe","path":"doc.md","file":"safe/doc.md","bytes":1,"created":true,"redaction":"enabled","redactions":{},"refusals":[],"jobId":"j"}`

// sourceFixture is a configured source with two files, and a server that takes anything.
type sourceFixture struct {
	h   *harness
	api *fakeAPI
	dir string
}

func newSourceFixture(t *testing.T, reply http.HandlerFunc) *sourceFixture {
	t.Helper()
	if reply == nil {
		reply = replyStatus(http.StatusAccepted, storedBody)
	}
	api := newFakeAPI(t, reply)
	h := newHarness(t)
	h.login(api.URL)
	dir := t.TempDir()
	f := &sourceFixture{h: h, api: api, dir: dir}
	f.write(t, "a.md", "alpha text")
	f.write(t, "sub/b.md", "beta text")
	if code := h.run("sources", "add", "notes", "--path", dir, "--collection", "safe"); code != 0 {
		t.Fatalf("add: code = %d, stderr = %q", code, h.stderr.String())
	}
	return f
}

func (f *sourceFixture) write(t *testing.T, name, text string) string {
	t.Helper()
	path := filepath.Join(f.dir, filepath.FromSlash(name))
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte(text), 0o600); err != nil {
		t.Fatal(err)
	}
	return path
}

// sent returns the sourceIds posted since the given request count.
func (f *sourceFixture) sent(t *testing.T, from int) []string {
	t.Helper()
	var ids []string
	for i, request := range f.api.requests[from:] {
		if request.Method != http.MethodPost || request.URL.Path != "/v1/documents" {
			t.Fatalf("unexpected request %s %s", request.Method, request.URL.Path)
		}
		var body map[string]any
		if err := json.Unmarshal([]byte(f.api.bodies[from+i]), &body); err != nil {
			t.Fatal(err)
		}
		ids = append(ids, body["sourceId"].(string))
	}
	return ids
}

func assertSent(t *testing.T, got []string, want ...string) {
	t.Helper()
	if strings.Join(got, ",") != strings.Join(want, ",") {
		t.Fatalf("sent %v, want %v", got, want)
	}
}

// scriptBy answers one file differently from the rest.
func scriptBy(f func() *fakeAPI, name string, status int, body string) http.HandlerFunc {
	return func(w http.ResponseWriter, _ *http.Request) {
		api := f()
		w.Header().Set("Content-Type", "application/json")
		if strings.Contains(api.bodies[len(api.bodies)-1], `"sourceId":"`+name+`"`) {
			w.WriteHeader(status)
			_, _ = io.WriteString(w, body)
			return
		}
		w.WriteHeader(http.StatusAccepted)
		_, _ = io.WriteString(w, storedBody)
	}
}

func newRefusingFixture(t *testing.T, name string, status int, body string) *sourceFixture {
	t.Helper()
	var fixture *sourceFixture
	fixture = newSourceFixture(t, scriptBy(func() *fakeAPI { return fixture.api }, name, status, body))
	return fixture
}

func TestSourcesAddRefusesBadInputWithoutRepeatingIt(t *testing.T) {
	h := newHarness(t)
	dir := t.TempDir()
	cases := map[string][]string{
		"a key as the name": {"sources", "add", testKey, "--path", dir, "--collection", "safe"},
		"no name":           {"sources", "add", "--path", dir, "--collection", "safe"},
		"no collection":     {"sources", "add", "notes", "--path", dir},
		"bad collection":    {"sources", "add", "notes", "--path", dir, "--collection", "a/b"},
		"no path":           {"sources", "add", "notes", "--collection", "safe"},
		"a missing folder":  {"sources", "add", "notes", "--path", filepath.Join(dir, testKey), "--collection", "safe"},
	}
	for name, args := range cases {
		if code := h.run(args...); code != apperr.ExitUsage {
			t.Errorf("%s: code = %d, stderr = %q", name, code, h.stderr.String())
		}
	}
	file := filepath.Join(dir, "file.md")
	if err := os.WriteFile(file, nil, 0o600); err != nil {
		t.Fatal(err)
	}
	if code := h.run("sources", "add", "notes", "--path", file, "--collection", "safe"); code != apperr.ExitUsage {
		t.Errorf("a file as the folder: code = %d", code)
	}
}

func TestSourcesAddRefusesASecondSourceWithOneName(t *testing.T) {
	f := newSourceFixture(t, nil)
	if code := f.h.run("sources", "add", "notes", "--path", f.dir, "--collection", "safe"); code != apperr.ExitUsage {
		t.Fatalf("code = %d", code)
	}
	if !strings.Contains(f.h.stderr.String(), "already exists") {
		t.Fatalf("stderr = %q", f.h.stderr.String())
	}
}

func TestSourcesListNeedsNoServerOrKey(t *testing.T) {
	f := newSourceFixture(t, nil)
	f.h.ringErr = errors.New("the keyring must not be opened")
	if code := f.h.run("sources", "list"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	out := f.h.stdout.String()
	for _, want := range []string{"notes", "safe", f.dir, "never"} {
		if !strings.Contains(out, want) {
			t.Fatalf("stdout lacks %q: %q", want, out)
		}
	}
	if len(f.api.requests) != 0 {
		t.Fatalf("list made %d requests", len(f.api.requests))
	}
}

func TestSourcesListShowsTheCursor(t *testing.T) {
	f := newSourceFixture(t, nil)
	if code := f.h.run("sources", "sync", "--apply"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	if code := f.h.run("sources", "list", "--format", "json"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	var listed []sources.Listing
	if err := json.Unmarshal(f.h.stdout.Bytes(), &listed); err != nil {
		t.Fatal(err)
	}
	if len(listed) != 1 || listed[0].Tracked == nil || *listed[0].Tracked != 2 || listed[0].LastSync == nil {
		t.Fatalf("listed = %+v", listed)
	}
	if code := f.h.run("sources", "list", "--format", "markdown"); code != 0 || !strings.Contains(f.h.stdout.String(), "2 tracked") {
		t.Fatalf("code = %d, stdout = %q", code, f.h.stdout.String())
	}
}

func TestSourcesListWithNoServerKnownSaysSo(t *testing.T) {
	h := newHarness(t)
	if code := h.run("sources", "add", "notes", "--path", t.TempDir(), "--collection", "safe"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if code := h.run("sources", "list"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if !strings.Contains(h.stdout.String(), "-") {
		t.Fatalf("stdout = %q", h.stdout.String())
	}
}

func TestSourcesRemoveKeepsTheServerAlone(t *testing.T) {
	f := newSourceFixture(t, nil)
	if code := f.h.run("sources", "remove", "notes"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if !strings.Contains(f.h.stderr.String(), "stay on the server") || len(f.api.requests) != 0 {
		t.Fatalf("stderr = %q, requests = %d", f.h.stderr.String(), len(f.api.requests))
	}
	if code := f.h.run("sources", "remove", "notes"); code != apperr.ExitNotFound {
		t.Fatalf("second remove: code = %d", code)
	}
}

func TestFirstRunIsADryRunNeedingNoKeyOrNetwork(t *testing.T) {
	f := newSourceFixture(t, nil)
	f.h.ringErr = errors.New("the keyring must not be opened")
	for run := 0; run < 2; run++ {
		if code := f.h.run("sources", "sync"); code != 0 {
			t.Fatalf("run %d: code = %d, stderr = %q", run, code, f.h.stderr.String())
		}
		out := f.h.stdout.String()
		for _, want := range []string{"dry run", "would send a.md (new)", "would send sub/b.md (new)"} {
			if !strings.Contains(out, want) {
				t.Fatalf("run %d: stdout lacks %q: %q", run, want, out)
			}
		}
		if !strings.Contains(f.h.stderr.String(), "first run") || !strings.Contains(f.h.stderr.String(), "--apply") {
			t.Fatalf("stderr = %q", f.h.stderr.String())
		}
	}
	if len(f.api.requests) != 0 {
		t.Fatalf("a dry run made %d requests", len(f.api.requests))
	}
	if strings.Contains(f.h.stdout.String(), "alpha text") {
		t.Fatal("a dry run printed a file's text")
	}
}

func TestApplySendsTheEnvelopeAndLaterRunsSendOnlyChanges(t *testing.T) {
	f := newSourceFixture(t, nil)
	if code := f.h.run("sources", "sync", "--apply"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	assertSent(t, f.sent(t, 0), "a.md", "sub/b.md")
	var body map[string]any
	if err := json.Unmarshal([]byte(f.api.bodies[0]), &body); err != nil {
		t.Fatal(err)
	}
	if body["collection"] != "safe" || body["source"] != "notes" || body["body"] != "alpha text" || body["title"] != "a.md" {
		t.Fatalf("body = %v", body)
	}
	if _, err := time.Parse(time.RFC3339Nano, fmt.Sprint(body["updated"])); err != nil {
		t.Fatalf("updated = %v", body["updated"])
	}

	// Nothing changed, so nothing connects: this works offline and with no key.
	f.h.ringErr = errors.New("the keyring must not be opened")
	before := len(f.api.requests)
	if code := f.h.run("sources", "sync"); code != 0 {
		t.Fatalf("unchanged run: code = %d, stderr = %q", code, f.h.stderr.String())
	}
	assertSent(t, f.sent(t, before))

	f.h.ringErr = nil
	f.write(t, "a.md", "alpha edited")
	if code := f.h.run("sources", "sync"); code != 0 {
		t.Fatalf("edited run: code = %d, stderr = %q", code, f.h.stderr.String())
	}
	assertSent(t, f.sent(t, before), "a.md")
}

func TestAnEditWithAnOldTimeIsStillSentAsTheLatest(t *testing.T) {
	f := newSourceFixture(t, nil)
	if code := f.h.run("sources", "sync", "--apply"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	var first map[string]any
	if err := json.Unmarshal([]byte(f.api.bodies[0]), &first); err != nil {
		t.Fatal(err)
	}
	path := f.write(t, "a.md", "restored from a backup")
	old := time.Date(2020, 1, 1, 0, 0, 0, 0, time.UTC)
	if err := os.Chtimes(path, old, old); err != nil {
		t.Fatal(err)
	}
	before := len(f.api.requests)
	if code := f.h.run("sources", "sync"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	assertSent(t, f.sent(t, before), "a.md")
	var second map[string]any
	if err := json.Unmarshal([]byte(f.api.bodies[before]), &second); err != nil {
		t.Fatal(err)
	}
	was, _ := time.Parse(time.RFC3339Nano, fmt.Sprint(first["updated"]))
	now, _ := time.Parse(time.RFC3339Nano, fmt.Sprint(second["updated"]))
	if !now.After(was) {
		t.Fatalf("updated went from %v to %v", was, now)
	}
}

func TestAFileGoneLocallyIsNeverDeleted(t *testing.T) {
	f := newSourceFixture(t, nil)
	if code := f.h.run("sources", "sync", "--apply"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if err := os.Remove(filepath.Join(f.dir, "a.md")); err != nil {
		t.Fatal(err)
	}
	before := len(f.api.requests)
	if code := f.h.run("sources", "sync", "--format", "json"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	assertSent(t, f.sent(t, before))
	var reports []sources.Report
	if err := json.Unmarshal(f.h.stdout.Bytes(), &reports); err != nil {
		t.Fatal(err)
	}
	if reports[0].Missing != 1 || reports[0].Unchanged != 1 {
		t.Fatalf("report = %+v", reports[0])
	}
}

func TestAFolderThatCannotBeOpenedLeavesTheCursorAlone(t *testing.T) {
	f := newSourceFixture(t, nil)
	if code := f.h.run("sources", "sync", "--apply"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	aside := f.dir + "-unmounted"
	if err := os.Rename(f.dir, aside); err != nil {
		t.Fatal(err)
	}
	if code := f.h.run("sources", "sync"); code != apperr.ExitUsage {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	if err := os.Rename(aside, f.dir); err != nil {
		t.Fatal(err)
	}
	before := len(f.api.requests)
	if code := f.h.run("sources", "sync"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	assertSent(t, f.sent(t, before))
}

func TestAnUnavailableServerStopsTheRunAndTheNextOneResumes(t *testing.T) {
	var fixture *sourceFixture
	down := true
	fixture = newSourceFixture(t, func(w http.ResponseWriter, r *http.Request) {
		if down && len(fixture.api.requests) >= 2 {
			replyStatus(http.StatusServiceUnavailable, `{"title":"down"}`)(w, r)
			return
		}
		replyStatus(http.StatusAccepted, storedBody)(w, r)
	})
	fixture.write(t, "c.md", "gamma")
	if code := fixture.h.run("sources", "sync", "--apply"); code != apperr.ExitUnavailable {
		t.Fatalf("code = %d, stderr = %q", code, fixture.h.stderr.String())
	}
	if !strings.Contains(fixture.h.stderr.String(), "1 sent and 2 not reached") {
		t.Fatalf("stderr = %q", fixture.h.stderr.String())
	}
	assertSent(t, fixture.sent(t, 0), "a.md", "c.md")

	// The run was asked to send, so the next one sends without --apply, and only the rest.
	down = false
	before := len(fixture.api.requests)
	if code := fixture.h.run("sources", "sync"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, fixture.h.stderr.String())
	}
	assertSent(t, fixture.sent(t, before), "c.md", "sub/b.md")
}

func TestAnUnreachableServerStopsAtOnce(t *testing.T) {
	f := newSourceFixture(t, nil)
	f.api.Close()
	if code := f.h.run("sources", "sync", "--apply"); code != apperr.ExitUnavailable {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	if !strings.Contains(f.h.stderr.String(), "0 sent and 2 not reached") {
		t.Fatalf("stderr = %q", f.h.stderr.String())
	}
}

func TestStatusesThatStopARun(t *testing.T) {
	for status, want := range map[int]int{
		http.StatusUnauthorized:        apperr.ExitAuth,
		http.StatusForbidden:           apperr.ExitAuth,
		http.StatusNotFound:            apperr.ExitNotFound,
		http.StatusInternalServerError: apperr.ExitGeneric,
		http.StatusBadGateway:          apperr.ExitUnavailable,
	} {
		f := newSourceFixture(t, replyStatus(status, `{"title":"no"}`))
		if code := f.h.run("sources", "sync", "--apply"); code != want {
			t.Errorf("HTTP %d: code = %d, stderr = %q", status, code, f.h.stderr.String())
		}
		if len(f.api.requests) != 1 {
			t.Errorf("HTTP %d: made %d requests, so it did not stop", status, len(f.api.requests))
		}
	}
}

func TestARefusedFileDoesNotStopTheOthers(t *testing.T) {
	for _, status := range []int{http.StatusBadRequest, http.StatusRequestTimeout, http.StatusConflict, http.StatusRequestEntityTooLarge, http.StatusUnprocessableEntity} {
		f := newRefusingFixture(t, "a.md", status, `{"title":"refused","detail":"not this one"}`)
		if code := f.h.run("sources", "sync", "--apply"); code == 0 {
			t.Errorf("HTTP %d: a refused file exited 0", status)
		}
		assertSent(t, f.sent(t, 0), "a.md", "sub/b.md")
		stderr := f.h.stderr.String()
		if !strings.Contains(stderr, "a.md was not sent") || !strings.Contains(stderr, "1 file(s) were not sent") {
			t.Errorf("HTTP %d: stderr = %q", status, stderr)
		}
		// It is tried again, and the one that got through is not.
		before := len(f.api.requests)
		f.h.run("sources", "sync")
		assertSent(t, f.sent(t, before), "a.md")
	}
}

func TestShortWaitsAreWaitedOut(t *testing.T) {
	for _, status := range []int{http.StatusTooManyRequests, http.StatusServiceUnavailable} {
		calls := 0
		f := newSourceFixture(t, func(w http.ResponseWriter, r *http.Request) {
			calls++
			if calls == 1 {
				w.Header().Set("Retry-After", "7")
				replyStatus(status, `{"title":"wait"}`)(w, r)
				return
			}
			replyStatus(http.StatusAccepted, storedBody)(w, r)
		})
		if code := f.h.run("sources", "sync", "--apply"); code != 0 {
			t.Fatalf("HTTP %d: code = %d, stderr = %q", status, code, f.h.stderr.String())
		}
		if len(f.h.slept) != 1 || f.h.slept[0] < 6*time.Second || f.h.slept[0] > 7*time.Second {
			t.Fatalf("HTTP %d: slept %v", status, f.h.slept)
		}
		assertSent(t, f.sent(t, 0), "a.md", "a.md", "sub/b.md")
	}
}

func alwaysRetryAfter(seconds string) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Retry-After", seconds)
		replyStatus(http.StatusTooManyRequests, `{"title":"wait"}`)(w, r)
	}
}

func TestALongWaitStopsAndResumesLater(t *testing.T) {
	f := newSourceFixture(t, alwaysRetryAfter("1800"))
	if code := f.h.run("sources", "sync", "--apply"); code != apperr.ExitRateLimited {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	if len(f.h.slept) != 0 || len(f.api.requests) != 1 {
		t.Fatalf("slept %v, requests %d", f.h.slept, len(f.api.requests))
	}
}

func TestRetriesAreBounded(t *testing.T) {
	f := newSourceFixture(t, alwaysRetryAfter("1"))
	if code := f.h.run("sources", "sync", "--apply"); code != apperr.ExitRateLimited {
		t.Fatalf("code = %d", code)
	}
	if len(f.h.slept) != maxRetries || len(f.api.requests) != maxRetries+1 {
		t.Fatalf("slept %d times, %d requests", len(f.h.slept), len(f.api.requests))
	}
}

func TestInterruptedWhileWaitingStops(t *testing.T) {
	f := newSourceFixture(t, alwaysRetryAfter("1"))
	f.h.sleepErr = errors.New("interrupted")
	if code := f.h.run("sources", "sync", "--apply"); code != apperr.ExitGeneric {
		t.Fatalf("code = %d", code)
	}
	if !strings.Contains(f.h.stderr.String(), "stopped while waiting") {
		t.Fatalf("stderr = %q", f.h.stderr.String())
	}
}

func TestASuccessThatCannotBeReadStopsAndRecordsNothing(t *testing.T) {
	for name, body := range map[string]string{"empty": `{}`, "not json": `<html>proxy</html>`} {
		f := newSourceFixture(t, replyStatus(http.StatusAccepted, body))
		if code := f.h.run("sources", "sync", "--apply"); code != apperr.ExitGeneric {
			t.Errorf("%s: code = %d, stderr = %q", name, code, f.h.stderr.String())
		}
		if len(f.api.requests) != 1 {
			t.Errorf("%s: %d requests", name, len(f.api.requests))
		}
		before := len(f.api.requests)
		f.h.run("sources", "sync")
		if len(f.api.requests) == before {
			t.Errorf("%s: nothing was sent again, so it was recorded as stored", name)
		}
	}
}

func TestAnUnexpectedSuccessStatusIsNotTakenAsStored(t *testing.T) {
	f := newSourceFixture(t, replyStatus(http.StatusOK, storedBody))
	if code := f.h.run("sources", "sync", "--apply"); code != apperr.ExitGeneric {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
}

func TestTheCursorIsSavedAlongTheWay(t *testing.T) {
	var fixture *sourceFixture
	tracked := -1
	fixture = newSourceFixture(t, func(w http.ResponseWriter, r *http.Request) {
		if len(fixture.api.requests) == saveEvery+1 {
			source := sources.Source{Name: "notes", Collection: "safe"}
			state, _, err := sources.LoadState(strings.TrimSuffix(fixture.api.URL, "/"), source)
			if err == nil {
				tracked = len(state.Files)
			}
		}
		replyStatus(http.StatusAccepted, storedBody)(w, r)
	})
	for i := range saveEvery {
		fixture.write(t, fmt.Sprintf("extra-%02d.md", i), fmt.Sprintf("text %d", i))
	}
	if code := fixture.h.run("sources", "sync", "--apply"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, fixture.h.stderr.String())
	}
	if tracked != saveEvery {
		t.Fatalf("the cursor held %d files after %d were sent", tracked, saveEvery)
	}
}

// A machine that cannot record its place should find out before it uploads anything.
func TestTheCursorExistsBeforeTheFirstSend(t *testing.T) {
	var fixture *sourceFixture
	existed := false
	fixture = newSourceFixture(t, func(w http.ResponseWriter, r *http.Request) {
		if len(fixture.api.requests) == 1 {
			source := sources.Source{Name: "notes", Collection: "safe"}
			_, found, err := sources.LoadState(fixture.api.URL, source)
			existed = found && err == nil
		}
		replyStatus(http.StatusAccepted, storedBody)(w, r)
	})
	if code := fixture.h.run("sources", "sync", "--apply"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, fixture.h.stderr.String())
	}
	if !existed {
		t.Fatal("the first document went out before the cursor existed")
	}
}

func TestAnotherServerStartsFromAFirstRun(t *testing.T) {
	f := newSourceFixture(t, nil)
	if code := f.h.run("sources", "sync", "--apply"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	other := newFakeAPI(t, replyStatus(http.StatusAccepted, storedBody))
	if code := f.h.run("sources", "sync", "--api-url", other.URL, "--api-key", testKey); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	if !strings.Contains(f.h.stdout.String(), "dry run") || len(other.requests) != 0 {
		t.Fatalf("stdout = %q, requests = %d", f.h.stdout.String(), len(other.requests))
	}
}

func TestDryRunAlwaysSendsNothing(t *testing.T) {
	f := newSourceFixture(t, nil)
	if code := f.h.run("sources", "sync", "--apply"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	f.write(t, "a.md", "edited")
	before := len(f.api.requests)
	if code := f.h.run("sources", "sync", "--dry-run"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	assertSent(t, f.sent(t, before))
	if !strings.Contains(f.h.stdout.String(), "would send a.md (changed)") || !strings.Contains(f.h.stderr.String(), "dry run, so nothing") {
		t.Fatalf("stdout = %q, stderr = %q", f.h.stdout.String(), f.h.stderr.String())
	}
	// Still owed, as the dry run recorded nothing.
	if code := f.h.run("sources", "sync"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	assertSent(t, f.sent(t, before), "a.md")
}

func TestSyncUsageErrors(t *testing.T) {
	f := newSourceFixture(t, nil)
	if code := f.h.run("sources", "sync", "--apply", "--dry-run"); code != apperr.ExitUsage {
		t.Errorf("both flags: code = %d", code)
	}
	if code := f.h.run("sources", "sync", "absent"); code != apperr.ExitNotFound {
		t.Errorf("unknown source: code = %d", code)
	}
	empty := newHarness(t)
	if code := empty.run("sources", "sync"); code != apperr.ExitUsage {
		t.Errorf("no sources: code = %d", code)
	}
	noServer := newHarness(t)
	if code := noServer.run("sources", "add", "notes", "--path", t.TempDir(), "--collection", "safe"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if code := noServer.run("sources", "sync"); code != apperr.ExitUsage {
		t.Errorf("no server: code = %d", code)
	}
	if len(f.api.requests) != 0 {
		t.Errorf("%d requests", len(f.api.requests))
	}
}

func TestNamedSourcesAloneAreSynced(t *testing.T) {
	f := newSourceFixture(t, nil)
	second := t.TempDir()
	if err := os.WriteFile(filepath.Join(second, "other.md"), []byte("other"), 0o600); err != nil {
		t.Fatal(err)
	}
	if code := f.h.run("sources", "add", "second", "--path", second, "--collection", "safe"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if code := f.h.run("sources", "sync", "second", "--apply"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	assertSent(t, f.sent(t, 0), "other.md")
	before := len(f.api.requests)
	// Each source has its own first run.
	if code := f.h.run("sources", "sync"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	assertSent(t, f.sent(t, before))
	if !strings.Contains(f.h.stdout.String(), "would send a.md") {
		t.Fatalf("stdout = %q", f.h.stdout.String())
	}
}

func TestReportsCountRedactionsWithNoValues(t *testing.T) {
	stored := `{"collection":"safe","path":"doc.md","file":"safe/doc.md","bytes":1,"created":true,"redaction":"disabled","redactions":{"github_token":2,"aws":1},"refusals":[],"jobId":"j"}`
	f := newSourceFixture(t, replyStatus(http.StatusAccepted, stored))
	if code := f.h.run("sources", "sync", "--apply", "--format", "json"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	var reports []sources.Report
	if err := json.Unmarshal(f.h.stdout.Bytes(), &reports); err != nil {
		t.Fatal(err)
	}
	r := reports[0]
	if r.Sent != 2 || r.Unredacted != 2 || r.Redactions["github_token"] != 4 || r.Redactions["aws"] != 2 {
		t.Fatalf("report = %+v", r)
	}
}

// syncedOutput edits a file, syncs, and returns stdout.
func syncedOutput(t *testing.T, f *sourceFixture, text string, args ...string) string {
	t.Helper()
	f.write(t, "a.md", text)
	if code := f.h.run(append([]string{"sources", "sync"}, args...)...); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, f.h.stderr.String())
	}
	return f.h.stdout.String()
}

func TestReportsInEveryFormatHoldNoText(t *testing.T) {
	stored := `{"collection":"safe","path":"doc.md","file":"safe/doc.md","bytes":1,"created":true,"redaction":"enabled","redactions":{"github_token":2,"aws":1},"refusals":[],"jobId":"j"}`
	f := newSourceFixture(t, replyStatus(http.StatusAccepted, stored))
	if code := f.h.run("sources", "sync", "--apply"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if out := syncedOutput(t, f, "first edit"); !strings.Contains(out, "aws: 1, github_token: 2") || strings.Contains(out, "first edit") {
		t.Fatalf("table = %q", out)
	}
	if out := syncedOutput(t, f, "second edit", "--format", "markdown"); !strings.Contains(out, "1 sent") || strings.Contains(out, "second edit") {
		t.Fatalf("markdown = %q", out)
	}
	if out := syncedOutput(t, f, "third edit", "--dry-run", "--format", "markdown"); !strings.Contains(out, "would send") || strings.Contains(out, "third edit") {
		t.Fatalf("dry run markdown = %q", out)
	}
}

func TestLeftOutFilesAreNamed(t *testing.T) {
	f := newSourceFixture(t, nil)
	f.write(t, "binary.md", "\xff\xfe")
	if code := f.h.run("sources", "sync"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if stderr := f.h.stderr.String(); !strings.Contains(stderr, "left out binary.md") || !strings.Contains(stderr, "not UTF-8") {
		t.Fatalf("stderr = %q", stderr)
	}
}

func TestAFileThatVanishesBeforeItIsSentIsReported(t *testing.T) {
	err := errFileUnreadable
	if !fileOnly(err) {
		t.Fatal("a file that cannot be read should not stop the run")
	}
	if fileOnly(errors.New("anything else")) {
		t.Fatal("an unknown failure should stop the run, which is the safe way to fail")
	}
}

func TestADamagedCursorStopsTheRun(t *testing.T) {
	f := newSourceFixture(t, nil)
	if code := f.h.run("sources", "sync", "--apply"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	state := filepath.Join(f.h.config, "sources-state")
	entries, err := os.ReadDir(state)
	if err != nil || len(entries) != 1 {
		t.Fatalf("entries = %v, err = %v", entries, err)
	}
	if err := os.WriteFile(filepath.Join(state, entries[0].Name()), []byte("{nope"), 0o600); err != nil {
		t.Fatal(err)
	}
	before := len(f.api.requests)
	if code := f.h.run("sources", "sync"); code != apperr.ExitGeneric {
		t.Fatalf("code = %d", code)
	}
	if !strings.Contains(f.h.stderr.String(), "damaged") || len(f.api.requests) != before {
		t.Fatalf("stderr = %q, requests = %d", f.h.stderr.String(), len(f.api.requests)-before)
	}
	if code := f.h.run("sources", "list"); code != apperr.ExitGeneric {
		t.Fatalf("list on a damaged cursor: code = %d", code)
	}
}

func TestSourcesAloneShowsHelp(t *testing.T) {
	h := newHarness(t)
	if code := h.run("sources"); code != 0 || !strings.Contains(h.stdout.String()+h.stderr.String(), "never deletes") {
		t.Fatalf("code = %d, stdout = %q", code, h.stdout.String())
	}
	if code := h.run("sources", "extra"); code != apperr.ExitUsage {
		t.Fatalf("code = %d", code)
	}
}

func TestSleepUntilDone(t *testing.T) {
	if err := sleepUntilDone(t.Context(), time.Millisecond); err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	if err := sleepUntilDone(ctx, time.Hour); err == nil {
		t.Fatal("a cancelled wait returned no error")
	}
}
