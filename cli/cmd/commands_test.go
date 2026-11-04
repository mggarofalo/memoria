package cmd

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"
	"testing"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/auth"
	"github.com/mggarofalo/memoria/cli/internal/redact"
	"github.com/mggarofalo/memoria/cli/internal/selfupdate"
)

// hostile holds escape sequences a transcript could carry: OSC 52 clipboard
// write, clear screen, and a carriage return overwrite.
const hostile = "\u001b]52;c;ZXZpbA==\u0007\u001b[2Jvisible\rEVIL"

func hostileJSON() string {
	data, _ := json.Marshal(hostile)
	return string(data)
}

func searchBody() string {
	return `{"results":[{"docid":"f6feba","file":` + hostileJSON() + `,"collection":"c","path":"a.md","title":` + hostileJSON() + `,
"score":0.91,"line":42,"snippet":` + hostileJSON() + `,"context":null,"metadata":null}],
"total":1,"tookMs":3,"query":{"collections":["c"],"rerank":true,"filtered":true,"fetched":40,"limit":10}}`
}

func documentBody() string {
	return `{"docid":"f6feba","file":"c/a.md","collection":"c","path":"a.md","title":` + hostileJSON() + `,"context":null,
"modifiedAt":"2026-01-01","bodyLength":10,"fromLine":null,"maxLines":null,"body":` + hostileJSON() + `,"metadata":null}`
}

func assertNoEscapes(t *testing.T, text string) {
	t.Helper()
	for _, bad := range []string{"\x1b", "\x07", "\r", "]52;", "[2J"} {
		if strings.Contains(text, bad) {
			t.Fatalf("output kept %q: %q", bad, text)
		}
	}
	if !strings.Contains(text, "visible") {
		t.Fatalf("output lost the text: %q", text)
	}
}

func TestQuerySendsEveryFlag(t *testing.T) {
	api := newFakeAPI(t, jsonReply(searchBody()))
	h := newHarness(t)
	h.login(api.URL)
	code := h.run("query", "api", "key", "rotation",
		"--collection", "a", "--collection", "b", "--limit", "5", "--min-score", "0.25", "--no-rerank",
		"--intent", "why", "--candidate-limit", "50", "--project", "P", "--branch", "main", "--tag", "x", "--tag", "y",
		"--since", "2026-01-01", "--until", "2026-02-01")
	if code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	req := api.requests[0]
	if req.URL.Path != "/v1/search" {
		t.Fatalf("path = %q", req.URL.Path)
	}
	q := req.URL.Query()
	for name, want := range map[string]string{
		"query": "api key rotation", "limit": "5", "minScore": "0.25", "rerank": "false", "intent": "why",
		"candidateLimit": "50", "project": "P", "branch": "main", "since": "2026-01-01", "until": "2026-02-01",
	} {
		if q.Get(name) != want {
			t.Errorf("%s = %q, want %q", name, q.Get(name), want)
		}
	}
	if strings.Join(q["collection"], ",") != "a,b" || strings.Join(q["tag"], ",") != "x,y" {
		t.Errorf("repeated params: %q", req.URL.RawQuery)
	}
}

func TestQueryOmitsUnsetFlags(t *testing.T) {
	api := newFakeAPI(t, jsonReply(searchBody()))
	h := newHarness(t)
	h.login(api.URL)
	if code := h.run("query", "x"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if got := api.requests[0].URL.RawQuery; got != "query=x" {
		t.Fatalf("query string = %q; server defaults should stay in charge", got)
	}
}

func TestZeroValuedFlagsAreStillSent(t *testing.T) {
	api := newFakeAPI(t, jsonReply(searchBody()))
	h := newHarness(t)
	h.login(api.URL)
	h.run("query", "x", "--min-score", "0")
	if api.requests[0].URL.Query().Get("minScore") != "0" {
		t.Fatalf("query string = %q", api.requests[0].URL.RawQuery)
	}
}

func TestQueryFormats(t *testing.T) {
	cases := map[string]string{"": "SCORE", "table": "SCORE", "markdown": "## 1."}
	for format, marker := range cases {
		t.Run("format "+format, func(t *testing.T) {
			api := newFakeAPI(t, jsonReply(searchBody()))
			h := newHarness(t)
			h.login(api.URL)
			args := []string{"query", "x"}
			if format != "" {
				args = append(args, "--format", format)
			}
			if code := h.run(args...); code != 0 {
				t.Fatalf("code = %d", code)
			}
			out := h.stdout.String()
			if !strings.Contains(out, marker) || !strings.Contains(out, "f6feba") {
				t.Fatalf("stdout = %q", out)
			}
			assertNoEscapes(t, out)
		})
	}
}

func TestFilteredShortResultExplainsItself(t *testing.T) {
	api := newFakeAPI(t, jsonReply(searchBody()))
	h := newHarness(t)
	h.login(api.URL)
	h.run("query", "x")
	if !strings.Contains(h.stderr.String(), "more matches may exist") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	h.run("query", "x", "--quiet")
	if h.stderr.Len() != 0 {
		t.Fatalf("--quiet stderr = %q", h.stderr.String())
	}
}

func TestJSONFormatIsTheExactResponse(t *testing.T) {
	cases := []struct {
		args []string
		body string
	}{
		{[]string{"query", "x"}, searchBody()},
		{[]string{"get", "a.md"}, documentBody()},
		{[]string{"multi-get", "a.md"}, `{"docs":[` + documentBody() + `],"skipped":[],"errors":[]}`},
		{[]string{"collections"}, `{"collections":[{"name":` + hostileJSON() + `,"documents":1,"lastModified":null,"includeByDefault":true,"context":null}],"extra":"  spacing  "}`},
		{[]string{"status"}, `{"totalDocuments":1,"needsEmbedding":0,"hasVectorIndex":true,"collections":[],"name":` + hostileJSON() + `}`},
	}
	for _, c := range cases {
		t.Run(c.args[0], func(t *testing.T) {
			api := newFakeAPI(t, jsonReply(c.body))
			h := newHarness(t)
			h.login(api.URL)
			if code := h.run(append(c.args, "--format", "json")...); code != 0 {
				t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
			}
			if h.stdout.String() != c.body {
				t.Fatalf("stdout differs from the response:\n%q\n%q", h.stdout.String(), c.body)
			}
		})
	}
}

func TestJSONFormatScrubsOnlyTheKeyInUse(t *testing.T) {
	body := `{"collections":[{"name":"` + testKey + `","documents":1,"lastModified":null,"includeByDefault":true,"context":"mem_someone_else"}]}`
	api := newFakeAPI(t, jsonReply(body))
	h := newHarness(t)
	h.login(api.URL)
	if code := h.run("collections", "--format", "json"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	want := strings.ReplaceAll(body, testKey, redact.Placeholder)
	if h.stdout.String() != want {
		t.Fatalf("stdout = %q", h.stdout.String())
	}
}

func TestGetSendsParameters(t *testing.T) {
	api := newFakeAPI(t, jsonReply(documentBody()))
	h := newHarness(t)
	h.login(api.URL)
	code := h.run("get", "claude-sessions/2026 05.md", "--from-line", "10", "--max-lines", "5", "--line-numbers", "--no-body")
	if code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	req := api.requests[0]
	if req.URL.Path != "/v1/docs/claude-sessions/2026 05.md" {
		t.Fatalf("path = %q", req.URL.Path)
	}
	q := req.URL.Query()
	if q.Get("fromLine") != "10" || q.Get("maxLines") != "5" || q.Get("lineNumbers") != "true" || q.Get("body") != "false" {
		t.Fatalf("query = %q", req.URL.RawQuery)
	}
}

func TestGetOutput(t *testing.T) {
	t.Run("markdown is the sanitized body", func(t *testing.T) {
		api := newFakeAPI(t, jsonReply(documentBody()))
		h := newHarness(t)
		h.login(api.URL)
		if code := h.run("get", "a.md"); code != 0 {
			t.Fatalf("code = %d", code)
		}
		assertNoEscapes(t, h.stdout.String())
		if !strings.HasSuffix(h.stdout.String(), "EVIL\n") {
			t.Fatalf("stdout = %q", h.stdout.String())
		}
	})
	t.Run("table", func(t *testing.T) {
		api := newFakeAPI(t, jsonReply(documentBody()))
		h := newHarness(t)
		h.login(api.URL)
		if code := h.run("get", "a.md", "--format", "table"); code != 0 {
			t.Fatalf("code = %d", code)
		}
		assertNoEscapes(t, h.stdout.String())
		if !strings.Contains(h.stdout.String(), "DOCID") {
			t.Fatalf("stdout = %q", h.stdout.String())
		}
	})
	t.Run("metadata only", func(t *testing.T) {
		body := strings.Replace(documentBody(), `"body":`+hostileJSON(), `"body":null`, 1)
		api := newFakeAPI(t, jsonReply(body))
		h := newHarness(t)
		h.login(api.URL)
		if code := h.run("get", "a.md", "--no-body"); code != 0 {
			t.Fatalf("code = %d", code)
		}
		assertNoEscapes(t, h.stdout.String())
		if !strings.Contains(h.stdout.String(), "10 bytes") {
			t.Fatalf("stdout = %q", h.stdout.String())
		}
	})
}

func TestMultiGetSendsBody(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"docs":[`+documentBody()+`],"skipped":[],"errors":[]}`))
	h := newHarness(t)
	h.login(api.URL)
	code := h.run("multi-get", "a", "b", "--pattern", "c/*.md", "--max-bytes", "100", "--max-lines", "9", "--line-numbers", "--no-body")
	if code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if api.requests[0].URL.Path != "/v1/docs:multiGet" || api.requests[0].Method != http.MethodPost {
		t.Fatalf("request = %s %s", api.requests[0].Method, api.requests[0].URL.Path)
	}
	assertMultiGetBody(t, api.bodies[0])
	assertNoEscapes(t, h.stdout.String())
}

func assertMultiGetBody(t *testing.T, body string) {
	t.Helper()
	var got struct {
		Ids         []string `json:"ids"`
		Pattern     string   `json:"pattern"`
		MaxBytes    int      `json:"maxBytes"`
		MaxLines    int      `json:"maxLines"`
		LineNumbers bool     `json:"lineNumbers"`
		IncludeBody *bool    `json:"includeBody"`
	}
	if err := json.Unmarshal([]byte(body), &got); err != nil {
		t.Fatal(err)
	}
	if strings.Join(got.Ids, ",") != "a,b" || got.Pattern != "c/*.md" || got.MaxBytes != 100 || got.MaxLines != 9 {
		t.Fatalf("body = %s", body)
	}
	if !got.LineNumbers || got.IncludeBody == nil || *got.IncludeBody {
		t.Fatalf("body = %s", body)
	}
}

func TestMultiGetOutcomes(t *testing.T) {
	doc := documentBody()
	cases := []struct {
		name, body string
		want       int
		stderr     string
	}{
		{"all found", `{"docs":[` + doc + `],"skipped":[],"errors":[]}`, 0, ""},
		{"skipped warns", `{"docs":[` + doc + `],"skipped":[{"file":` + hostileJSON() + `,"reason":"too large"}],"errors":[]}`, 0, "too large"},
		{"only skipped", `{"docs":[],"skipped":[{"file":"a","reason":"too large"}],"errors":[]}`, 0, "too large"},
		{"partial failure", `{"docs":[` + doc + `],"skipped":[],"errors":["b.md: missing"]}`, apperr.ExitNotFound, "b.md: missing"},
		{"nothing matched", `{"docs":[],"skipped":[],"errors":[]}`, apperr.ExitNotFound, "no documents matched"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			api := newFakeAPI(t, jsonReply(c.body))
			h := newHarness(t)
			h.login(api.URL)
			if code := h.run("multi-get", "a"); code != c.want {
				t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
			}
			if !strings.Contains(h.stderr.String(), c.stderr) {
				t.Fatalf("stderr = %q", h.stderr.String())
			}
			if strings.Contains(h.stderr.String(), "\x1b") {
				t.Fatalf("stderr kept an escape: %q", h.stderr.String())
			}
		})
	}
}

func TestMultiGetTableFormat(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"docs":[`+documentBody()+`],"skipped":[],"errors":[]}`))
	h := newHarness(t)
	h.login(api.URL)
	if code := h.run("multi-get", "a", "--format", "table"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	assertNoEscapes(t, h.stdout.String())
}

func TestCollectionsFormats(t *testing.T) {
	body := `{"collections":[{"name":` + hostileJSON() + `,"documents":7,"lastModified":null,"includeByDefault":true,"context":"notes"}]}`
	for format, marker := range map[string]string{"table": "NAME", "markdown": "**"} {
		t.Run(format, func(t *testing.T) {
			api := newFakeAPI(t, jsonReply(body))
			h := newHarness(t)
			h.login(api.URL)
			if code := h.run("collections", "--format", format); code != 0 {
				t.Fatalf("code = %d", code)
			}
			assertNoEscapes(t, h.stdout.String())
			if !strings.Contains(h.stdout.String(), marker) || !strings.Contains(h.stdout.String(), "7") {
				t.Fatalf("stdout = %q", h.stdout.String())
			}
		})
	}
}

func TestStatusFormats(t *testing.T) {
	body := `{"totalDocuments":12,"needsEmbedding":3,"hasVectorIndex":true,"collections":[{"name":` + hostileJSON() + `,"documents":12,"lastUpdated":"2026-01-01"}]}`
	for format, marker := range map[string]string{"table": "COLLECTION", "markdown": "- documents: 12"} {
		t.Run(format, func(t *testing.T) {
			api := newFakeAPI(t, jsonReply(body))
			h := newHarness(t)
			h.login(api.URL)
			if code := h.run("status", "--format", format); code != 0 {
				t.Fatalf("code = %d", code)
			}
			assertNoEscapes(t, h.stdout.String())
			if !strings.Contains(h.stdout.String(), marker) {
				t.Fatalf("stdout = %q", h.stdout.String())
			}
		})
	}
}

func TestErrorsFromTheServerAreSanitized(t *testing.T) {
	api := newFakeAPI(t, func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/problem+json")
		w.WriteHeader(500)
		_ = json.NewEncoder(w).Encode(map[string]string{"title": hostile, "detail": hostile, "traceId": hostile})
	})
	h := newHarness(t)
	h.login(api.URL)
	h.run("status")
	if strings.Contains(h.stderr.String(), "\x1b") || strings.Contains(h.stderr.String(), "\r") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
}

func TestMCPMissingCredentials(t *testing.T) {
	h := newHarness(t)
	h.stdin = strings.NewReader(`{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}` + "\n")
	if code := h.run("mcp"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	var frame struct {
		ID    int `json:"id"`
		Error struct {
			Code    int    `json:"code"`
			Message string `json:"message"`
		} `json:"error"`
	}
	if err := json.Unmarshal(h.stdout.Bytes(), &frame); err != nil {
		t.Fatalf("stdout = %q: %v", h.stdout.String(), err)
	}
	if frame.ID != 1 || frame.Error.Code == 0 || frame.Error.Message == "" {
		t.Fatalf("frame = %+v", frame)
	}
	if h.stderr.Len() == 0 {
		t.Fatal("the failure should also be logged to stderr")
	}
}

func TestMCPForwardsWithTheStoredKey(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"jsonrpc":"2.0","id":1,"result":{}}`))
	h := newHarness(t)
	h.login(api.URL)
	h.stdin = strings.NewReader(`{"jsonrpc":"2.0","id":1,"method":"ping"}` + "\n")
	if code := h.run("mcp"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if h.stdout.String() != `{"jsonrpc":"2.0","id":1,"result":{}}`+"\n" {
		t.Fatalf("stdout = %q", h.stdout.String())
	}
	req := api.requests[0]
	if req.URL.Path != "/mcp" || req.Header.Get("Authorization") != "Bearer "+testKey {
		t.Fatalf("request = %s %v", req.URL.Path, req.Header)
	}
}

func TestMCPHonorsPathAndTimeoutFlags(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"jsonrpc":"2.0","id":1,"result":{}}`))
	h := newHarness(t)
	h.login(api.URL)
	h.stdin = strings.NewReader(`{"jsonrpc":"2.0","id":1,"method":"ping"}` + "\n")
	if code := h.run("mcp", "--path", "custom/mcp", "--timeout", "10s"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if api.requests[0].URL.Path != "/custom/mcp" {
		t.Fatalf("path = %q", api.requests[0].URL.Path)
	}
}

func TestMCPUpstreamFailureEchoingTheKeyStaysOffStdout(t *testing.T) {
	api := newFakeAPI(t, echoProblem(401))
	h := newHarness(t)
	h.login(api.URL)
	h.stdin = strings.NewReader(`{"jsonrpc":"2.0","id":1,"method":"ping"}` + "\n")
	if code := h.run("mcp", "--verbose"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if !strings.Contains(h.stdout.String(), `"error"`) {
		t.Fatalf("stdout = %q", h.stdout.String())
	}
	if strings.Contains(h.stdout.String(), "Bearer mem_") {
		t.Fatalf("stdout = %q", h.stdout.String())
	}
}

// fakeUpdater scripts the release lookup and records installs.
type fakeUpdater struct {
	candidate *selfupdate.Candidate
	findErr   error
	install   error
	tags      []string
	installed int
}

func (f *fakeUpdater) Find(_ context.Context, tag string) (*selfupdate.Candidate, error) {
	f.tags = append(f.tags, tag)
	return f.candidate, f.findErr
}

func (f *fakeUpdater) Install(context.Context, *selfupdate.Candidate, string) error {
	f.installed++
	return f.install
}

func withVersion(t *testing.T, version string) {
	t.Helper()
	previous := Version
	Version = version
	t.Cleanup(func() { Version = previous })
}

func candidate(tag string) *selfupdate.Candidate {
	return &selfupdate.Candidate{Tag: tag, Version: strings.TrimPrefix(tag, "v"), Asset: "memoria_x.tar.gz"}
}

func TestSelfUpdateDecisions(t *testing.T) {
	cases := []struct {
		name      string
		version   string
		candidate string
		args      []string
		code      int
		installs  int
		stderr    string
	}{
		{"updates", "1.0.0", "v1.1.0", nil, 0, 1, "Updated to v1.1.0"},
		{"up to date", "1.1.0", "v1.1.0", nil, 0, 0, "Already up to date"},
		{"dev refused", "dev", "v1.1.0", nil, apperr.ExitGeneric, 0, "--force"},
		{"dev forced", "dev", "v1.1.0", []string{"--force"}, 0, 1, "Updated"},
		{"pinned downgrade refused", "2.0.0", "v1.1.0", []string{"--version", "v1.1.0"}, apperr.ExitGeneric, 0, "--allow-downgrade"},
		{"pinned downgrade allowed", "2.0.0", "v1.1.0", []string{"--version", "v1.1.0", "--allow-downgrade"}, 0, 1, "Updated"},
		{"prerelease refused", "1.0.0", "v2.0.0-rc.1", []string{"--version", "v2.0.0-rc.1"}, apperr.ExitGeneric, 0, "--prerelease"},
		{"prerelease allowed", "1.0.0", "v2.0.0-rc.1", []string{"--version", "v2.0.0-rc.1", "--prerelease"}, 0, 1, "Updated"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			withVersion(t, c.version)
			fake := &fakeUpdater{candidate: candidate(c.candidate)}
			h := newHarness(t)
			h.updater = fake
			code := h.run(append([]string{"self-update"}, c.args...)...)
			if code != c.code {
				t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
			}
			if fake.installed != c.installs {
				t.Fatalf("installs = %d", fake.installed)
			}
			if !strings.Contains(h.stderr.String(), c.stderr) {
				t.Fatalf("stderr = %q", h.stderr.String())
			}
		})
	}
}

func TestSelfUpdateRefusesAReleaseFlaggedPrerelease(t *testing.T) {
	withVersion(t, "1.0.0")
	flagged := candidate("v2.0.0")
	flagged.Prerelease = true
	fake := &fakeUpdater{candidate: flagged}
	h := newHarness(t)
	h.updater = fake
	if code := h.run("self-update", "--version", "v2.0.0"); code != apperr.ExitGeneric || fake.installed != 0 {
		t.Fatalf("code = %d, installs = %d", code, fake.installed)
	}
}

func TestSelfUpdateCheckChangesNothing(t *testing.T) {
	cases := []struct {
		name, version, candidate string
		args                     []string
		want                     string
	}{
		{"update available", "1.0.0", "v1.1.0", nil, "update available: 1.0.0 -> v1.1.0"},
		{"up to date", "1.1.0", "v1.1.0", nil, "up to date"},
		{"dev build", "dev", "v1.1.0", nil, "would not install it"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			withVersion(t, c.version)
			fake := &fakeUpdater{candidate: candidate(c.candidate)}
			h := newHarness(t)
			h.updater = fake
			if code := h.run(append([]string{"self-update", "--check"}, c.args...)...); code != 0 {
				t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
			}
			if !strings.Contains(h.stdout.String(), c.want) {
				t.Fatalf("stdout = %q", h.stdout.String())
			}
			if fake.installed != 0 {
				t.Fatal("--check must not install")
			}
		})
	}
}

func TestSelfUpdatePassesThePinnedTag(t *testing.T) {
	withVersion(t, "1.0.0")
	fake := &fakeUpdater{candidate: candidate("v1.1.0")}
	h := newHarness(t)
	h.updater = fake
	h.run("self-update", "--check", "--version", "v1.1.0")
	if len(fake.tags) != 1 || fake.tags[0] != "v1.1.0" {
		t.Fatalf("tags = %v", fake.tags)
	}
}

func TestSelfUpdateFailures(t *testing.T) {
	withVersion(t, "1.0.0")
	t.Run("lookup fails", func(t *testing.T) {
		h := newHarness(t)
		h.updater = &fakeUpdater{findErr: errors.New("network down")}
		if code := h.run("self-update"); code != apperr.ExitUnavailable || !strings.Contains(h.stderr.String(), "network down") {
			t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
		}
	})
	t.Run("no build for this platform", func(t *testing.T) {
		h := newHarness(t)
		h.updater = &fakeUpdater{findErr: fmt.Errorf("%w: latest release for plan9/mips", selfupdate.ErrNoRelease)}
		if code := h.run("self-update"); code != apperr.ExitNotFound {
			t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
		}
	})
	t.Run("install fails", func(t *testing.T) {
		h := newHarness(t)
		h.updater = &fakeUpdater{candidate: candidate("v1.1.0"), install: errors.New("checksum mismatch")}
		if code := h.run("self-update"); code != apperr.ExitGeneric || !strings.Contains(h.stderr.String(), "checksum mismatch") {
			t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
		}
	})
	t.Run("cannot locate the binary", func(t *testing.T) {
		h := newHarness(t)
		fake := &fakeUpdater{candidate: candidate("v1.1.0")}
		a := h.app()
		a.selfUpdate.newUpdater = func(selfupdate.Options) releaseUpdater { return fake }
		a.selfUpdate.executable = func() (string, error) { return "", errors.New("nowhere") }
		if code := a.run(context.Background(), []string{"self-update"}); code != apperr.ExitGeneric {
			t.Fatalf("code = %d", code)
		}
	})
}

func TestStoredKeyWithNoRecordedServerIsRefused(t *testing.T) {
	h := newHarness(t)
	h.keyring.values[auth.KeyName(auth.DefaultProfile)] = testKey
	h.env[auth.EnvURL] = "https://memoria.example.com"
	if code := h.run("status"); code != apperr.ExitInsecure {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
}

func TestWriteFailuresSurface(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.login(api.URL)
	a := h.app()
	a.stdout = failingWriter{}
	if code := a.run(context.Background(), []string{"collections", "--format", "json"}); code != apperr.ExitGeneric {
		t.Fatalf("code = %d", code)
	}
	if code := a.run(context.Background(), []string{"auth", "status"}); code != apperr.ExitGeneric {
		t.Fatalf("auth status code = %d", code)
	}
}

type failingWriter struct{}

func (failingWriter) Write([]byte) (int, error) { return 0, fmt.Errorf("closed: %w", io.ErrClosedPipe) }
