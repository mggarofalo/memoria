package cmd

import (
	"encoding/json"
	"io"
	"net/http"
	"strings"
	"testing"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
)

const (
	opsJobID   = "5b0c2a8e-1c1d-4c7e-9a43-0d4a2f6f9b11"
	workerJSON = `{"name":"desktop","host":"10.0.0.2:8383","state":"up","drained":DRAINED,"circuit":"closed",` +
		`"circuits":{"embed":"closed","rerank":"closed","generate":"closed","documents":"closed"},"capabilities":["embed","rerank"],` +
		`"priority":1,"maxConcurrent":8,"inFlight":2,"queueDepth":0,"calls":40,"errorRate":0.25,"lastHeartbeat":"2026-11-01T10:00:00Z",` +
		`"embedModelCompatible":true,"embedDisagrees":false}`
	jobJSON = `{"id":"` + opsJobID + `","state":"failed","collections":null,"embed":true,"force":false,"reason":"POST /reindex",` +
		`"createdAt":"2026-11-01T10:00:00Z","startedAt":null,"finishedAt":"2026-11-01T10:00:09Z","error":"no worker","attempts":2,"worker":"desktop"}`
	countsJSON = `{"queued":1,"held":0,"running":1,"succeeded":3,"failed":1}`
)

func worker(drained bool) string {
	if drained {
		return strings.Replace(workerJSON, "DRAINED", "true", 1)
	}
	return strings.Replace(workerJSON, "DRAINED", "false", 1)
}

// opsServer answers every control route the way the API does.
func opsServer(t *testing.T) (*harness, *fakeAPI) {
	t.Helper()
	return adminHarness(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		route := r.Method + " " + r.URL.Path
		switch {
		case route == "GET /v1/admin/workers":
			_, _ = io.WriteString(w, `{"mode":"remote","workers":[`+worker(false)+`]}`)
		case strings.HasSuffix(route, ":drain"):
			_, _ = io.WriteString(w, worker(true))
		case strings.HasSuffix(route, ":enable"):
			_, _ = io.WriteString(w, worker(false))
		case route == "GET /v1/admin/queue":
			_, _ = io.WriteString(w, `{"paused":true,"counts":`+countsJSON+`,"persistent":true,"deadLetters":2,`+
				`"interactive":{"inFlight":1,"waiting":0},"background":{"inFlight":3,"waiting":5},"jobs":[`+jobJSON+`,`+jobJSON+`]}`)
		case route == "POST /v1/admin/queue:pause":
			_, _ = io.WriteString(w, `{"paused":true,"counts":`+countsJSON+`}`)
		case route == "POST /v1/admin/queue:resume":
			_, _ = io.WriteString(w, `{"paused":false,"counts":`+countsJSON+`}`)
		case strings.HasPrefix(route, "POST /v1/admin/queue/jobs/"), route == "POST /v1/admin/reindex":
			w.WriteHeader(http.StatusAccepted)
			_, _ = io.WriteString(w, jobJSON)
		default:
			w.WriteHeader(http.StatusNotFound)
			_, _ = io.WriteString(w, `{"title":"no route"}`)
		}
	})
}

func runOK(t *testing.T, h *harness, args ...string) string {
	t.Helper()
	if code := h.run(args...); code != 0 {
		t.Fatalf("%v: code = %d, stderr = %q", args, code, h.stderr.String())
	}
	return h.stdout.String()
}

func TestWorkersListShowsStateLoadAndErrors(t *testing.T) {
	h, api := opsServer(t)
	out := runOK(t, h, "workers", "list")
	for _, want := range []string{"NAME", "desktop", "up", "closed", "25%", "embed,rerank", "10.0.0.2:8383"} {
		if !strings.Contains(out, want) {
			t.Errorf("out lacks %q: %q", want, out)
		}
	}
	if api.requests[0].Method != http.MethodGet || api.requests[0].URL.Path != "/v1/admin/workers" {
		t.Fatalf("request = %s %s", api.requests[0].Method, api.requests[0].URL.Path)
	}
	h.assertNoKeyLeak()
}

func TestWorkersListMarkdownAndEmptyPool(t *testing.T) {
	h, _ := opsServer(t)
	if out := runOK(t, h, "workers", "list", "--format", "markdown"); !strings.Contains(out, "desktop `: up, circuit closed, 2 in flight") {
		t.Fatalf("out = %q", out)
	}
	local, _ := adminHarness(t, replyStatus(200, `{"mode":"local","workers":[]}`))
	for _, format := range []string{"table", "markdown"} {
		local.stdout.Reset()
		if out := runOK(t, local, "workers", "list", "--format", format); !strings.Contains(out, "local LLM mode") {
			t.Fatalf("%s: out = %q", format, out)
		}
	}
}

func TestWorkersDrainAndEnableSayWhatHappened(t *testing.T) {
	h, api := opsServer(t)
	if out := runOK(t, h, "workers", "drain", "desktop"); !strings.Contains(out, "desktop is drained (up), with 2 calls in flight") {
		t.Fatalf("out = %q", out)
	}
	h.stdout.Reset()
	if out := runOK(t, h, "workers", "enable", "desktop"); !strings.Contains(out, "desktop is up") {
		t.Fatalf("out = %q", out)
	}
	if api.requests[0].Method != http.MethodPost || api.requests[0].URL.Path != "/v1/admin/workers/desktop:drain" || api.requests[1].URL.Path != "/v1/admin/workers/desktop:enable" {
		t.Fatalf("requests = %s, %s", api.requests[0].URL.Path, api.requests[1].URL.Path)
	}
}

func TestWorkersDrainJSONIsTheResponse(t *testing.T) {
	h, _ := opsServer(t)
	if out := runOK(t, h, "workers", "drain", "desktop", "--format", "json"); out != worker(true) {
		t.Fatalf("out = %q", out)
	}
}

func TestOpsRefuseABadArgumentWithoutCallingTheServer(t *testing.T) {
	for name, args := range map[string][]string{
		"no name":        {"workers", "drain"},
		"two names":      {"workers", "drain", "a", "b"},
		"slash":          {"workers", "drain", "a/b"},
		"dot segment":    {"workers", "enable", ".."},
		"space":          {"workers", "enable", "a b"},
		"no job":         {"queue", "retry"},
		"pasted key":     {"queue", "cancel", "mem_0011223344556677_secret"},
		"negative jobs":  {"queue", "status", "--jobs", "-1"},
		"force no embed": {"reindex", "--force", "--no-embed"},
		"stray argument": {"reindex", "notes"},
	} {
		t.Run(name, func(t *testing.T) {
			h, api := opsServer(t)
			if code := h.run(args...); code != apperr.ExitUsage {
				t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
			}
			if len(api.requests) != 0 {
				t.Fatal("the server was contacted")
			}
		})
	}
}

func TestQueueStatusShowsStateAndRecentJobs(t *testing.T) {
	h, _ := opsServer(t)
	out := runOK(t, h, "queue", "status")
	for _, want := range []string{"Paused: yes", "Persistent: yes", "set aside: 2", "1 queued (0 held), 1 running", "background 3 running, 5 waiting", opsJobID, "failed", "all", "no worker"} {
		if !strings.Contains(out, want) {
			t.Errorf("out lacks %q: %q", want, out)
		}
	}
	if n := strings.Count(out, opsJobID); n != 2 {
		t.Fatalf("listed %d jobs, want 2: %q", n, out)
	}
	h.stdout.Reset()
	if out := runOK(t, h, "queue", "status", "--jobs", "1"); strings.Count(out, opsJobID) != 1 {
		t.Fatalf("--jobs 1 listed more: %q", out)
	}
	h.stdout.Reset()
	if out := runOK(t, h, "queue", "status", "--format", "markdown"); !strings.Contains(out, "failed: all (no worker)") {
		t.Fatalf("markdown = %q", out)
	}
}

func TestQueueStatusWithNoJobsSaysSo(t *testing.T) {
	h, _ := adminHarness(t, replyStatus(200, `{"paused":false,"counts":`+countsJSON+`,"persistent":false,"deadLetters":0,`+
		`"interactive":{"inFlight":0,"waiting":0},"background":{"inFlight":0,"waiting":0},"jobs":[]}`))
	if out := runOK(t, h, "queue", "status"); !strings.Contains(out, "No jobs.") || !strings.Contains(out, "Persistent: no") {
		t.Fatalf("out = %q", out)
	}
}

func TestQueueRetryCancelPauseAndResume(t *testing.T) {
	for _, tc := range []struct {
		args       []string
		path, want string
	}{
		{[]string{"queue", "retry", opsJobID}, "/v1/admin/queue/jobs/" + opsJobID + ":retry", "Retried job " + opsJobID},
		{[]string{"queue", "cancel", opsJobID}, "/v1/admin/queue/jobs/" + opsJobID + ":cancel", "Cancelled job " + opsJobID},
		{[]string{"queue", "pause"}, "/v1/admin/queue:pause", "The queue is paused: 1 queued"},
		{[]string{"queue", "resume"}, "/v1/admin/queue:resume", "The queue is running"},
	} {
		t.Run(tc.args[1], func(t *testing.T) {
			h, api := opsServer(t)
			if out := runOK(t, h, tc.args...); !strings.Contains(out, tc.want) {
				t.Fatalf("out = %q", out)
			}
			if api.requests[0].Method != http.MethodPost || api.requests[0].URL.Path != tc.path {
				t.Fatalf("request = %s %s", api.requests[0].Method, api.requests[0].URL.Path)
			}
		})
	}
}

func TestReindexSendsWhatWasAskedAndNoMore(t *testing.T) {
	for name, tc := range map[string]struct {
		args []string
		want map[string]any
	}{
		"everything": {nil, map[string]any{"collections": nil, "embed": true, "force": nil}},
		"some":       {[]string{"--collection", "notes", "--collection", "docs"}, map[string]any{"collections": []any{"notes", "docs"}, "embed": true, "force": nil}},
		"scan only":  {[]string{"--no-embed"}, map[string]any{"collections": nil, "embed": false, "force": nil}},
		"force":      {[]string{"--force"}, map[string]any{"collections": nil, "embed": true, "force": true}},
	} {
		t.Run(name, func(t *testing.T) {
			h, api := opsServer(t)
			if out := runOK(t, h, append([]string{"reindex"}, tc.args...)...); !strings.Contains(out, "Queued job "+opsJobID) {
				t.Fatalf("out = %q", out)
			}
			var body map[string]any
			if err := json.Unmarshal([]byte(api.bodies[0]), &body); err != nil {
				t.Fatal(err)
			}
			for key, want := range tc.want {
				if got, _ := json.Marshal(body[key]); string(got) != mustJSON(t, want) {
					t.Errorf("%s = %s, want %s (body %s)", key, got, mustJSON(t, want), api.bodies[0])
				}
			}
		})
	}
}

func mustJSON(t *testing.T, v any) string {
	t.Helper()
	data, err := json.Marshal(v)
	if err != nil {
		t.Fatal(err)
	}
	return string(data)
}

func TestOpsErrorsAreClassified(t *testing.T) {
	problem := func(status int, body string) http.HandlerFunc {
		return func(w http.ResponseWriter, _ *http.Request) {
			w.Header().Set("Content-Type", "application/problem+json")
			w.WriteHeader(status)
			_, _ = io.WriteString(w, body)
		}
	}
	for name, tc := range map[string]struct {
		reply http.HandlerFunc
		args  []string
		code  int
		want  string
	}{
		"scoped key": {problem(403, `{"title":"Key is bound to collections","code":"collection_scoped_key"}`), []string{"workers", "drain", "desktop"}, apperr.ExitAuth, "--scope admin"},
		"no admin":   {problem(403, `{"detail":"This API key lacks the 'admin' scope.","code":"insufficient_scope"}`), []string{"queue", "status"}, apperr.ExitAuth, "--bootstrap-admin-key"},
		"unknown":    {problem(404, `{"title":"Not found","detail":"No worker is named ghost","code":"unknown_worker"}`), []string{"workers", "enable", "ghost"}, apperr.ExitNotFound, "No worker is named ghost"},
		"finished":   {problem(409, `{"title":"Conflict","detail":"This job has already finished","code":"not_cancellable"}`), []string{"queue", "cancel", opsJobID}, apperr.ExitGeneric, "already finished"},
		"bad list":   {problem(400, `{"title":"invalid","errors":{"collections":["An empty list is refused."]}}`), []string{"reindex", "--collection", "x"}, apperr.ExitUsage, "empty list"},
		"down":       {problem(503, `{"title":"Search backend unavailable"}`), []string{"queue", "pause"}, apperr.ExitUnavailable, "unavailable"},
		"odd 200":    {replyStatus(200, `null`), []string{"workers", "list"}, apperr.ExitGeneric, "cannot read"},
		"odd 202":    {replyStatus(202, `{}`), []string{"queue", "retry", opsJobID}, apperr.ExitGeneric, "cannot read"},
		"wrong 2xx":  {replyStatus(200, jobJSON), []string{"reindex"}, apperr.ExitGeneric, "cannot read"},
		"odd queue":  {replyStatus(200, `{}`), []string{"queue", "status"}, apperr.ExitGeneric, "cannot read"},
		"odd pause":  {replyStatus(200, `null`), []string{"queue", "resume"}, apperr.ExitGeneric, "cannot read"},
		"odd worker": {replyStatus(200, `{}`), []string{"workers", "drain", "desktop"}, apperr.ExitGeneric, "cannot read"},
	} {
		t.Run(name, func(t *testing.T) {
			h, _ := adminHarness(t, tc.reply)
			if code := h.run(tc.args...); code != tc.code {
				t.Fatalf("code = %d, want %d, stderr = %q", code, tc.code, h.stderr.String())
			}
			if !strings.Contains(h.stderr.String(), tc.want) {
				t.Fatalf("stderr = %q", h.stderr.String())
			}
		})
	}
}

func TestOpsCommandsHaveHelp(t *testing.T) {
	for _, args := range [][]string{{"workers"}, {"queue"}} {
		h, api := opsServer(t)
		if out := runOK(t, h, args...); !strings.Contains(out, "admin") {
			t.Fatalf("%v help = %q", args, out)
		}
		if len(api.requests) != 0 {
			t.Fatal("help contacted the server")
		}
	}
}

const fullReadiness = `{"status":"degraded","ready":true,"reason":null,"sidecar":{"status":"degraded","version":"1.2.3","uptimeSeconds":42,` +
	`"index":{"dbPath":"/x/index.sqlite","totalDocuments":10,"needsEmbedding":3,"hasVectorIndex":true},` +
	`"models":{"embed":"e","rerank":"r","generate":"g","loaded":{"embed":false,"rerank":false,"generate":false},"resident":false},` +
	`"warmup":{"requested":false,"done":false,"error":null,"durationMs":null},"metadata":{"documents":10,"refreshedAt":null},` +
	`"jobs":{"pending":2,"persistent":false,"stuck":{"reason":"no worker can embed","attempts":4}},` +
	`"llm":{"mode":"remote","degraded":true,"unavailable":["embed","rerank"],"workersUp":0,"workersTotal":1,"documentsSetAside":1}}}`

func traced(status int, body string) http.HandlerFunc {
	return func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.Header().Set("X-Trace-Id", "4bf92f3577b34da6a3ce929d0e0e4736")
		w.WriteHeader(status)
		_, _ = io.WriteString(w, body)
	}
}

func TestReadyShowsTheBackendsReportAndTheTraceID(t *testing.T) {
	h, api := adminHarness(t, traced(200, fullReadiness))
	out := runOK(t, h, "ready")
	for _, want := range []string{"Status: degraded.", "10 documents, 3 waiting to embed", "2 pending, persistent: no", "after 4 attempts: no worker can embed", "0 of 1 workers up, unavailable: embed,rerank", "set aside: 1", "Trace: 4bf92f3577b34da6a3ce929d0e0e4736"} {
		if !strings.Contains(out, want) {
			t.Errorf("out lacks %q: %q", want, out)
		}
	}
	if api.requests[0].URL.Path != "/ready" || api.requests[0].URL.Query().Has("strict") {
		t.Fatalf("request = %s", api.requests[0].URL)
	}
}

func TestReadyWithoutAdminSaysWhatItCannotSee(t *testing.T) {
	h, _ := adminHarness(t, traced(200, `{"status":"ready","ready":true,"reason":null,"sidecar":null}`))
	if out := runOK(t, h, "ready", "--format", "markdown"); !strings.Contains(out, "Status: ready.") || !strings.Contains(out, "admin scope") {
		t.Fatalf("out = %q", out)
	}
}

func TestReadyNotReadyPrintsTheReportAndExits5(t *testing.T) {
	h, api := adminHarness(t, traced(503, `{"status":"not_ready","ready":false,"reason":"unreachable: the sidecar could not be reached","sidecar":null}`))
	if code := h.run("ready", "--strict"); code != apperr.ExitUnavailable {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if out := h.stdout.String(); !strings.Contains(out, "Status: not_ready.") || !strings.Contains(out, "Reason: unreachable") {
		t.Fatalf("out = %q", out)
	}
	if api.requests[0].URL.Query().Get("strict") != "true" {
		t.Fatalf("request = %s", api.requests[0].URL)
	}
}

func TestReadyJSONAndOddAnswers(t *testing.T) {
	h, _ := adminHarness(t, traced(200, fullReadiness))
	if out := runOK(t, h, "ready", "--format", "json"); out != fullReadiness {
		t.Fatalf("out = %q", out)
	}
	for name, reply := range map[string]http.HandlerFunc{
		"empty":     traced(200, `{}`),
		"forbidden": traced(401, `{"title":"no"}`),
	} {
		t.Run(name, func(t *testing.T) {
			h, _ := adminHarness(t, reply)
			if code := h.run("ready"); code == 0 {
				t.Fatalf("stdout = %q", h.stdout.String())
			}
		})
	}
}
