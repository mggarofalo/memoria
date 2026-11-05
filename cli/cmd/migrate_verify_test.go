package cmd

import (
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/migrate"
)

// verifyServer answers what a check reads, on top of the push server's ledger.
type verifyServer struct {
	*pushServer
	documents int
	bodies    map[string]string
	hits      []string
}

func newVerifyServer(t *testing.T) *verifyServer {
	t.Helper()
	v := &verifyServer{pushServer: newPushServer(t, nil), documents: 3, bodies: map[string]string{}}
	v.routes = v.route
	return v
}

func (v *verifyServer) route(w http.ResponseWriter, r *http.Request) bool {
	switch {
	case r.URL.Path == "/v1/collections":
		w.Header().Set("Content-Type", "application/json")
		_, _ = fmt.Fprintf(w, `{"collections":[{"name":"notes","documents":%d,"includeByDefault":true,"context":null,"lastModified":null}]}`, v.documents)
		return true
	case strings.HasPrefix(r.URL.Path, "/v1/docs/"):
		v.serveDocument(w, strings.TrimPrefix(r.URL.Path, "/v1/docs/"))
		return true
	case r.URL.Path == "/v1/search":
		v.serveSearch(w)
		return true
	}
	return false
}

func (v *verifyServer) serveDocument(w http.ResponseWriter, id string) {
	body, held := v.bodies[strings.TrimPrefix(id, "notes/")]
	if !held {
		writeProblem(w, pushAnswer{status: http.StatusNotFound})
		return
	}
	text, _ := json.Marshal(body)
	w.Header().Set("Content-Type", "application/json")
	_, _ = fmt.Fprintf(w, `{"body":%s,"bodyLength":1,"collection":"notes","context":null,"docid":"abc","file":"f","fromLine":null,"maxLines":null,"metadata":null,"modifiedAt":"","path":"p","title":"t"}`, text)
}

func (v *verifyServer) serveSearch(w http.ResponseWriter) {
	results := make([]string, 0, len(v.hits))
	for _, hit := range v.hits {
		results = append(results, fmt.Sprintf(`{"collection":"notes","context":null,"docid":"abc","file":"f","line":1,"metadata":null,"path":%q,"score":1,"snippet":"s","title":"t"}`, hit))
	}
	w.Header().Set("Content-Type", "application/json")
	_, _ = fmt.Fprintf(w, `{"results":[%s],"total":1,"tookMs":1,"query":{"collections":["notes"],"rerank":true,"filtered":false,"fetched":1,"limit":10}}`, strings.Join(results, ","))
}

type verifyFixture struct {
	*pushFixture
	server *verifyServer
}

// pushed is a fixture already sent, with the server holding each document's text.
func newVerifyFixture(t *testing.T) *verifyFixture {
	t.Helper()
	f := newPushFixture(t)
	server := newVerifyServer(t)
	if code := f.push(server.pushServer); code != 0 {
		t.Fatalf("push: code = %d, stderr = %q", code, f.h.stderr.String())
	}
	server.bodies = map[string]string{"a.md": "alpha text", "my dir/b+c.md": "beta text", "sub/d.md": "delta text"}
	f.h.localQMD = func([]string) ([]byte, error) { return nil, errors.New("no queries were expected") }
	return &verifyFixture{pushFixture: f, server: server}
}

func (f *verifyFixture) verify(t *testing.T, extra ...string) (migrate.VerifySummary, int, string) {
	t.Helper()
	code := f.h.run(append([]string{"migrate", "verify", "--qmd-config", f.config, "--format", "json"}, extra...)...)
	var result migrate.VerifyResult
	if err := json.Unmarshal(f.h.stdout.Bytes(), &result); err != nil {
		t.Fatalf("stdout = %q, stderr = %q: %v", f.h.stdout.String(), f.h.stderr.String(), err)
	}
	return result.Collections[0], code, result.ReportFile
}

func verifyProblems(t *testing.T, path string) []migrate.VerifyProblem {
	t.Helper()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	var problems []migrate.VerifyProblem
	if err := json.Unmarshal(data, &problems); err != nil {
		t.Fatal(err)
	}
	return problems
}

func TestVerifyConfirmsWhatTheServerHolds(t *testing.T) {
	f := newVerifyFixture(t)
	summary, code, report := f.verify(t)
	if code != 0 || summary.Confirmed != 3 || summary.LocalFiles != 3 || summary.ServerDocuments != 3 || summary.Sampled != 3 || summary.SampleSame != 3 {
		t.Fatalf("code = %d, summary = %+v, stderr = %q", code, summary, f.h.stderr.String())
	}
	if problems := verifyProblems(t, report); len(problems) != 0 {
		t.Fatalf("problems = %+v", problems)
	}
}

func TestVerifySaysWhatItDoesNotShow(t *testing.T) {
	f := newVerifyFixture(t)
	if code := f.h.run("migrate", "verify", "--qmd-config", f.config); code != 0 {
		t.Fatalf("code = %d", code)
	}
	for _, want := range []string{"does not show every document", "Overlap is a number to read", "not run, as no --queries"} {
		if !strings.Contains(f.h.stdout.String(), want) {
			t.Errorf("%q is missing from %q", want, f.h.stdout.String())
		}
	}
	if code := f.h.run("migrate", "verify", "--qmd-config", f.config, "--format", "markdown"); code != 0 || !strings.Contains(f.h.stdout.String(), "3 confirmed") {
		t.Fatalf("markdown: code = %d, stdout = %q", code, f.h.stdout.String())
	}
}

func TestVerifyComparesTheVersionNotJustTheState(t *testing.T) {
	f := newVerifyFixture(t)
	f.server.mu.Lock()
	f.server.ledger["a.md"] = "2001-01-01T00:00:00Z"
	f.server.mu.Unlock()
	summary, code, report := f.verify(t)
	if code != apperr.ExitGeneric || summary.VersionDiffers != 1 || summary.Confirmed != 2 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
	if problems := verifyProblems(t, report); len(problems) != 1 || problems[0].Name != "a.md" || problems[0].Kind != "version_differs" {
		t.Fatalf("problems = %+v", problems)
	}
	if strings.Contains(f.h.stdout.String()+f.h.stderr.String(), "a.md") {
		t.Fatalf("a name reached the output: %q", f.h.stdout.String())
	}
}

func TestVerifySaysWhatIsNotHeld(t *testing.T) {
	f := newVerifyFixture(t)
	f.server.forget()
	f.server.documents = 0
	summary, code, report := f.verify(t)
	if code != apperr.ExitGeneric || summary.NotHeld != 3 || summary.Sampled != 0 || summary.CountDiffers {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
	if problems := verifyProblems(t, report); len(problems) != 3 || problems[0].Detail != "none" {
		t.Fatalf("problems = %+v", problems)
	}
}

func TestVerifyFetchesTheTextAndAllowsOnlyRedaction(t *testing.T) {
	f := newVerifyFixture(t)
	f.server.bodies["a.md"] = "[REDACTED:token]"
	f.server.bodies["sub/d.md"] = "something else"
	delete(f.server.bodies, "my dir/b+c.md")
	summary, code, report := f.verify(t)
	if code != apperr.ExitGeneric || summary.SampleRedacted != 1 || summary.SampleDifferent != 1 || summary.SampleFailed != 1 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
	if problems := verifyProblems(t, report); len(problems) != 2 {
		t.Fatalf("problems = %+v", problems)
	}
}

func TestVerifyFetchesOnlyASampleAndRefusesNone(t *testing.T) {
	f := newVerifyFixture(t)
	summary, code, _ := f.verify(t, "--sample", "1")
	if code != 0 || summary.Sampled != 1 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
	for _, sample := range []string{"0", "-1"} {
		if code := f.h.run("migrate", "verify", "--qmd-config", f.config, "--sample", sample); code != apperr.ExitUsage {
			t.Errorf("--sample %s: code = %d", sample, code)
		}
	}
}

func TestASampleSpreadsOverTheListAndVaries(t *testing.T) {
	names := make([]string, 39)
	for i := range names {
		names[i] = fmt.Sprintf("%02d", i)
	}
	first := sampleOf(names, 20, 1)
	if len(first) != 20 || first[len(first)-1] <= "19" {
		t.Fatalf("it took the start of the list: %v", first)
	}
	if strings.Join(first, "") != strings.Join(sampleOf(names, 20, 1), "") {
		t.Fatal("the same seed gave another sample")
	}
	if strings.Join(first, "") == strings.Join(sampleOf(names, 20, 2), "") {
		t.Fatal("another seed gave the same sample")
	}
	if got := sampleOf(names, 100, 1); len(got) != 39 {
		t.Fatalf("got %d", len(got))
	}
}

func TestTheSeedIsPrintedAndCanBeGiven(t *testing.T) {
	f := newVerifyFixture(t)
	if code := f.h.run("migrate", "verify", "--qmd-config", f.config, "--seed", "42"); code != 0 || !strings.Contains(f.h.stdout.String(), "Sample seed: 42") {
		t.Fatalf("code = %d, stdout = %q", code, f.h.stdout.String())
	}
}

func TestCountsThatDifferAreNotClean(t *testing.T) {
	f := newVerifyFixture(t)
	f.server.documents = 2
	summary, code, report := f.verify(t)
	if code != apperr.ExitGeneric || !summary.CountDiffers {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
	if problems := verifyProblems(t, report); len(problems) != 1 || problems[0].Kind != "count_differs" {
		t.Fatalf("problems = %+v", problems)
	}
	if code := f.h.run("migrate", "verify", "--qmd-config", f.config); code != apperr.ExitGeneric || !strings.Contains(f.h.stdout.String(), "the counts differ") {
		t.Fatalf("code = %d, stdout = %q", code, f.h.stdout.String())
	}
}

func TestTakenOverIsCountedApartAndNotCalledConfirmed(t *testing.T) {
	f := newVerifyFixture(t)
	f.server.state.Store(migrate.StateTakenOver)
	f.server.documents = 9
	summary, code, _ := f.verify(t)
	if code != 0 || summary.CountDiffers || summary.TakenOver != 3 || summary.Confirmed != 0 || summary.Sampled != 0 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
}

func TestAStopClaimsNothingItDidNotCheck(t *testing.T) {
	f := newVerifyFixture(t)
	f.server.statusFaults(pushAnswer{status: http.StatusForbidden}, 100)
	summary, code, _ := f.verify(t)
	if code == 0 || summary.Confirmed != 0 || summary.NotChecked != 3 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
}

func writeQueries(t *testing.T, text string) string {
	t.Helper()
	path := filepath.Join(t.TempDir(), "queries.txt")
	writeFile(t, path, text)
	return path
}

func TestOverlapIsReportedAndNeverFailsTheCheck(t *testing.T) {
	f := newVerifyFixture(t)
	var seen [][]string
	f.h.localQMD = func(args []string) ([]byte, error) {
		seen = append(seen, args)
		return []byte(`[{"file":"qmd://notes/a.md"},{"file":"qmd://notes/sub/d.md"}]`), nil
	}
	f.server.hits = []string{"sub/d.md", "elsewhere.md"}
	summary, code, _ := f.verify(t, "--queries", writeQueries(t, "secretquery\n\nanother\n"))
	if code != 0 || summary.Overlap.Queries != 2 || summary.Overlap.Mean != 0.5 || summary.Overlap.Lowest != 0.5 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
	if len(seen) != 2 || strings.Join(seen[0], " ") != "search --json -n 10 -c notes -- secretquery" {
		t.Fatalf("args = %v", seen)
	}
	if strings.Contains(f.h.stdout.String()+f.h.stderr.String(), "secretquery") {
		t.Fatalf("a query reached the output: %q", f.h.stdout.String())
	}
}

func TestLocalQMDFailingIsSaidAndAQueryWithNoLocalResultsIsLeftOut(t *testing.T) {
	f := newVerifyFixture(t)
	f.h.localQMD = func([]string) ([]byte, error) { return nil, errors.New("no qmd") }
	summary, code, report := f.verify(t, "--queries", writeQueries(t, "one\n"))
	if code != apperr.ExitGeneric || summary.Overlap.Failed != 1 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
	if problems := verifyProblems(t, report); len(problems) != 1 || problems[0].Name != "query 1" {
		t.Fatalf("problems = %+v", problems)
	}
	f.h.localQMD = func([]string) ([]byte, error) { return []byte("No results found."), nil }
	summary, code, _ = f.verify(t, "--queries", writeQueries(t, "one\n"))
	if code != 0 || summary.Overlap.NoLocalResults != 1 || summary.Overlap.Mean != 0 {
		t.Fatalf("code = %d, summary = %+v", code, summary)
	}
	if code := f.h.run("migrate", "verify", "--qmd-config", f.config, "--queries", filepath.Join(t.TempDir(), "absent")); code != apperr.ExitUsage {
		t.Fatalf("code = %d", code)
	}
	if code := f.h.run("migrate", "verify", "--qmd-config", f.config, "--local-mode", "nonsense"); code != apperr.ExitUsage {
		t.Fatalf("code = %d", code)
	}
}

func TestVerifyChangesNothingOnTheServer(t *testing.T) {
	f := newVerifyFixture(t)
	before := f.server.total()
	if _, code, _ := f.verify(t); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if f.server.total() != before {
		t.Fatalf("it sent %d documents", f.server.total()-before)
	}
}
