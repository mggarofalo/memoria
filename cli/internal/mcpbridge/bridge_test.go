package mcpbridge

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/redact"
	"github.com/mggarofalo/memoria/cli/internal/transport"
)

const testKey = "mem_0011223344556677_8899aabbccddeeff"

const initialize = `{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}`

type frame struct {
	JSONRPC string          `json:"jsonrpc"`
	ID      json.RawMessage `json:"id"`
	Result  json.RawMessage `json:"result"`
	Error   *struct {
		Code    int      `json:"code"`
		Message string   `json:"message"`
		Data    failData `json:"data"`
	} `json:"error"`
	Method string `json:"method"`
}

type run struct {
	stdout, log string
	frames      []frame
	err         error
}

// runBridge feeds lines to a bridge, then checks the invariant every test
// shares: stdout holds nothing but one JSON-RPC object per line, and the key is
// nowhere.
func runBridge(t *testing.T, opts Options, lines ...string) run {
	t.Helper()
	var stdout, log bytes.Buffer
	if opts.Redactor == nil {
		opts.Redactor = redact.New()
		opts.Redactor.Add(testKey)
	}
	opts.Log = &log
	err := New(&stdout, opts).Run(context.Background(), strings.NewReader(strings.Join(lines, "\n")+"\n"))
	r := run{stdout: stdout.String(), log: log.String(), err: err}
	for _, line := range strings.Split(strings.TrimRight(r.stdout, "\n"), "\n") {
		if line == "" {
			continue
		}
		var f frame
		if jsonErr := json.Unmarshal([]byte(line), &f); jsonErr != nil {
			t.Fatalf("stdout has a line that is not JSON: %q", line)
		}
		if f.JSONRPC != "2.0" {
			t.Fatalf("stdout has a line that is not JSON-RPC: %q", line)
		}
		r.frames = append(r.frames, f)
	}
	if strings.Contains(r.stdout, testKey) || strings.Contains(r.log, testKey) {
		t.Fatalf("key leaked\nstdout: %s\nlog: %s", r.stdout, r.log)
	}
	return r
}

func upstream(t *testing.T, handler http.HandlerFunc) (Options, *httptest.Server) {
	t.Helper()
	server := httptest.NewServer(handler)
	t.Cleanup(server.Close)
	base, err := url.Parse(server.URL)
	if err != nil {
		t.Fatal(err)
	}
	client, err := transport.NewClient(transport.Options{BaseURL: base, Key: testKey})
	if err != nil {
		t.Fatal(err)
	}
	return Options{Endpoint: server.URL + "/mcp", Client: client}, server
}

func requireOne(t *testing.T, r run) frame {
	t.Helper()
	if len(r.frames) != 1 {
		t.Fatalf("frames = %d, stdout = %q, log = %q", len(r.frames), r.stdout, r.log)
	}
	return r.frames[0]
}

func TestForwardsJSONWithTheKey(t *testing.T) {
	var got http.Header
	var body []byte
	opts, _ := upstream(t, func(w http.ResponseWriter, r *http.Request) {
		got = r.Header.Clone()
		body, _ = io.ReadAll(r.Body)
		w.Header().Set("Content-Type", "application/json")
		_, _ = io.WriteString(w, `{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-06-18"}}`)
	})
	f := requireOne(t, runBridge(t, opts, initialize))
	if string(f.ID) != "1" || f.Error != nil {
		t.Fatalf("frame = %+v", f)
	}
	if got.Get("Authorization") != "Bearer "+testKey {
		t.Error("the key should go upstream")
	}
	if !strings.Contains(got.Get("Accept"), "text/event-stream") || !strings.Contains(got.Get("Accept"), "application/json") {
		t.Errorf("Accept = %q", got.Get("Accept"))
	}
	if string(body) != initialize {
		t.Errorf("body = %s", body)
	}
}

func TestRelaysServerSentEvents(t *testing.T) {
	opts, _ := upstream(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/event-stream")
		_, _ = io.WriteString(w, ": comment\n\n")
		_, _ = io.WriteString(w, "event: ping\ndata: ignored\n\n")
		_, _ = io.WriteString(w, "event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\"}\n\n")
		// Multi-line data must collapse onto one stdout line.
		_, _ = io.WriteString(w, "data: {\"jsonrpc\":\"2.0\",\ndata: \"id\":1,\"result\":{}}\n\n")
	})
	r := runBridge(t, opts, initialize)
	if len(r.frames) != 2 || r.frames[0].Method != "notifications/progress" || string(r.frames[1].ID) != "1" {
		t.Fatalf("stdout = %q", r.stdout)
	}
	if r.frames[1].Error != nil {
		t.Fatalf("unexpected error: %+v", r.frames[1].Error)
	}
}

func TestStreamEndingWithoutDelimiterStillCounts(t *testing.T) {
	opts, _ := upstream(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/event-stream")
		_, _ = io.WriteString(w, "data: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\n")
	})
	if f := requireOne(t, runBridge(t, opts, initialize)); f.Error != nil {
		t.Fatalf("frame = %+v", f)
	}
}

func TestSessionAndProtocolHeadersFollowInitialize(t *testing.T) {
	var mu sync.Mutex
	var seen []http.Header
	opts, _ := upstream(t, func(w http.ResponseWriter, r *http.Request) {
		mu.Lock()
		seen = append(seen, r.Header.Clone())
		mu.Unlock()
		w.Header().Set("Mcp-Session-Id", "session-1")
		w.Header().Set("Content-Type", "application/json")
		_, _ = io.WriteString(w, `{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-06-18"}}`)
	})
	var stdout bytes.Buffer
	b := New(&stdout, opts)
	if err := b.Run(context.Background(), strings.NewReader(initialize+"\n")); err != nil {
		t.Fatal(err)
	}
	if err := b.Run(context.Background(), strings.NewReader(`{"jsonrpc":"2.0","id":2,"method":"ping"}`+"\n")); err != nil {
		t.Fatal(err)
	}
	if len(seen) != 2 {
		t.Fatalf("requests = %d", len(seen))
	}
	if seen[1].Get("Mcp-Session-Id") != "session-1" || seen[1].Get("Mcp-Protocol-Version") != "2025-06-18" {
		t.Fatalf("headers = %v", seen[1])
	}
}

func TestNotificationGetsNoReply(t *testing.T) {
	opts, _ := upstream(t, func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusAccepted)
	})
	r := runBridge(t, opts, `{"jsonrpc":"2.0","method":"notifications/initialized"}`)
	if r.stdout != "" {
		t.Fatalf("stdout = %q", r.stdout)
	}
}

func TestClientResponseIsForwarded(t *testing.T) {
	var body string
	opts, _ := upstream(t, func(w http.ResponseWriter, r *http.Request) {
		data, _ := io.ReadAll(r.Body)
		body = string(data)
		w.WriteHeader(http.StatusAccepted)
	})
	r := runBridge(t, opts, `{"jsonrpc":"2.0","id":"srv-1","result":{}}`)
	if r.stdout != "" || !strings.Contains(body, "srv-1") {
		t.Fatalf("stdout = %q, body = %q", r.stdout, body)
	}
}

func TestUpstreamFailuresBecomeErrors(t *testing.T) {
	cases := []struct {
		name   string
		status int
		header map[string]string
		code   int
		kind   apperr.Kind
	}{
		{"unauthorized", 401, nil, codeAuth, apperr.KindAuth},
		{"forbidden", 403, nil, codeForbidden, apperr.KindForbidden},
		{"not found", 404, nil, codeNotFound, apperr.KindNotFound},
		{"rate limited", 429, map[string]string{"Retry-After": "7"}, codeRateLimited, apperr.KindRateLimited},
		{"unavailable", 503, nil, codeUnavailable, apperr.KindUnavailable},
		{"server error", 500, nil, codeUpstream, apperr.KindGeneric},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			opts, _ := upstream(t, func(w http.ResponseWriter, r *http.Request) {
				for k, v := range c.header {
					w.Header().Set(k, v)
				}
				w.Header().Set("Content-Type", "application/problem+json")
				w.WriteHeader(c.status)
				// A hostile or careless server echoes the header back.
				_, _ = fmt.Fprintf(w, `{"title":"Nope","detail":"got %s","traceId":"t-1"}`, r.Header.Get("Authorization"))
			})
			f := requireOne(t, runBridge(t, opts, initialize))
			if f.Error == nil || f.Error.Code != c.code || f.Error.Data.Kind != c.kind {
				t.Fatalf("frame = %+v", f.Error)
			}
			assertFailureData(t, f, c.status)
		})
	}
}

func assertFailureData(t *testing.T, f frame, status int) {
	t.Helper()
	if f.Error.Data.Status != status || string(f.ID) != "1" || f.Error.Data.TraceID != "t-1" {
		t.Errorf("frame = %+v, id = %s", f.Error, f.ID)
	}
	if status == 429 && f.Error.Data.RetryAfterSeconds != 7 {
		t.Errorf("retry after = %d", f.Error.Data.RetryAfterSeconds)
	}
}

func TestUpstreamDownBecomesAnError(t *testing.T) {
	opts, server := upstream(t, func(http.ResponseWriter, *http.Request) {})
	server.Close()
	f := requireOne(t, runBridge(t, opts, initialize))
	if f.Error == nil || f.Error.Code != codeUnavailable {
		t.Fatalf("frame = %+v", f.Error)
	}
}

func TestBadUpstreamResponses(t *testing.T) {
	cases := map[string]http.HandlerFunc{
		"html": func(w http.ResponseWriter, r *http.Request) {
			w.Header().Set("Content-Type", "text/html")
			_, _ = io.WriteString(w, "<html>")
		},
		"no content type": func(w http.ResponseWriter, r *http.Request) {
			w.Header()["Content-Type"] = nil
			_, _ = io.WriteString(w, "x")
		},
		"invalid json": func(w http.ResponseWriter, r *http.Request) {
			w.Header().Set("Content-Type", "application/json")
			_, _ = io.WriteString(w, "{not json")
		},
		"invalid sse frame": func(w http.ResponseWriter, r *http.Request) {
			w.Header().Set("Content-Type", "text/event-stream")
			_, _ = io.WriteString(w, "data: {not json\n\n")
		},
		"202 for a request": func(w http.ResponseWriter, r *http.Request) {
			w.WriteHeader(http.StatusAccepted)
		},
		"stream without the answer": func(w http.ResponseWriter, r *http.Request) {
			w.Header().Set("Content-Type", "text/event-stream")
			_, _ = io.WriteString(w, "data: {\"jsonrpc\":\"2.0\",\"method\":\"note\"}\n\n")
		},
	}
	for name, handler := range cases {
		t.Run(name, func(t *testing.T) {
			opts, _ := upstream(t, handler)
			r := runBridge(t, opts, initialize)
			last := r.frames[len(r.frames)-1]
			if last.Error == nil || string(last.ID) != "1" {
				t.Fatalf("stdout = %q", r.stdout)
			}
		})
	}
}

func TestStreamFailingMidwayReportsAnError(t *testing.T) {
	opts, _ := upstream(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/event-stream")
		w.Header().Set("Content-Length", "1000")
		_, _ = io.WriteString(w, "data: {\"jsonrpc\":\"2.0\",\"me")
	})
	f := requireOne(t, runBridge(t, opts, initialize))
	if f.Error == nil {
		t.Fatalf("frame = %+v", f)
	}
}

func TestUpstreamFrameCarryingTheKeyIsScrubbed(t *testing.T) {
	opts, _ := upstream(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = fmt.Fprintf(w, `{"jsonrpc":"2.0","id":1,"result":{"text":"%s"}}`, testKey)
	})
	r := runBridge(t, opts, initialize)
	if !strings.Contains(r.stdout, redact.Placeholder) {
		t.Fatalf("stdout = %q", r.stdout)
	}
}

func TestRedirectToAnotherOriginBecomesAnError(t *testing.T) {
	other := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		t.Error("the redirect target should not be contacted")
	}))
	defer other.Close()
	opts, _ := upstream(t, func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, other.URL, http.StatusTemporaryRedirect)
	})
	f := requireOne(t, runBridge(t, opts, initialize))
	if f.Error == nil || f.Error.Code != codeInsecure {
		t.Fatalf("frame = %+v", f.Error)
	}
}

func TestMissingCredentialsAnswerEveryRequest(t *testing.T) {
	opts := Options{StartupErr: apperr.New(apperr.KindAuth, "no API key found: run 'memoria auth login'")}
	r := runBridge(t, opts,
		initialize,
		`{"jsonrpc":"2.0","method":"notifications/initialized"}`,
		`{"jsonrpc":"2.0","id":"two","method":"tools/list"}`,
	)
	if r.err != nil {
		t.Fatal(r.err)
	}
	if len(r.frames) != 2 {
		t.Fatalf("stdout = %q", r.stdout)
	}
	for _, f := range r.frames {
		if f.Error == nil || f.Error.Code != codeAuth || !strings.Contains(f.Error.Message, "auth login") {
			t.Fatalf("frame = %+v", f.Error)
		}
	}
	if string(r.frames[1].ID) != `"two"` {
		t.Fatalf("string ids must keep their type: %s", r.frames[1].ID)
	}
	if !strings.Contains(r.log, "auth login") {
		t.Fatalf("log = %q", r.log)
	}
}

func TestMalformedMessages(t *testing.T) {
	cases := []struct {
		name, line string
		code       int
		id         string
	}{
		{"not json", `{oops`, codeParseError, "null"},
		{"batch", `[{"jsonrpc":"2.0","id":1,"method":"ping"}]`, codeInvalidRequest, "null"},
		{"scalar", `42`, codeInvalidRequest, "null"},
		{"wrong version", `{"jsonrpc":"1.0","id":5,"method":"ping"}`, codeInvalidRequest, "5"},
		{"object id", `{"jsonrpc":"2.0","id":{},"method":"ping"}`, codeInvalidRequest, "null"},
		{"no method", `{"jsonrpc":"2.0","id":6}`, codeInvalidRequest, "6"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			opts, _ := upstream(t, func(http.ResponseWriter, *http.Request) {
				t.Error("a malformed message should not go upstream")
			})
			f := requireOne(t, runBridge(t, opts, c.line))
			if f.Error == nil || f.Error.Code != c.code || string(f.ID) != c.id {
				t.Fatalf("frame = %+v id = %s", f.Error, f.ID)
			}
		})
	}
}

func TestOversizedMessageIsRejectedAndTheStreamStaysInSync(t *testing.T) {
	opts, _ := upstream(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = io.WriteString(w, `{"jsonrpc":"2.0","id":2,"result":{}}`)
	})
	opts.MaxInboundBytes = 1024
	big := `{"jsonrpc":"2.0","id":1,"method":"x","params":"` + strings.Repeat("a", 256*1024) + `"}`
	r := runBridge(t, opts, big, `{"jsonrpc":"2.0","id":2,"method":"ping"}`)
	if len(r.frames) != 2 {
		t.Fatalf("stdout = %q", r.stdout)
	}
	if r.frames[0].Error == nil || r.frames[0].Error.Code != codeInvalidRequest || string(r.frames[0].ID) != "null" {
		t.Fatalf("first = %+v", r.frames[0])
	}
	if r.frames[1].Error != nil || string(r.frames[1].ID) != "2" {
		t.Fatalf("second = %+v", r.frames[1])
	}
}

func TestOversizedUpstreamFrameIsAnError(t *testing.T) {
	for name, contentType := range map[string]string{"json": "application/json", "sse": "text/event-stream"} {
		t.Run(name, func(t *testing.T) {
			opts, _ := upstream(t, func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Content-Type", contentType)
				prefix := ""
				if contentType == "text/event-stream" {
					prefix = "data: "
				}
				_, _ = io.WriteString(w, prefix+`{"jsonrpc":"2.0","id":1,"result":"`+strings.Repeat("a", 256*1024)+`"}`+"\n\n")
			})
			opts.MaxOutboundBytes = 1024
			f := requireOne(t, runBridge(t, opts, initialize))
			if f.Error == nil {
				t.Fatalf("frame = %+v", f)
			}
			if strings.Contains(f.Error.Message, "aaaa") {
				t.Fatal("the error should not quote the frame")
			}
		})
	}
}

func TestBlankLinesAndBOMAreTolerated(t *testing.T) {
	opts, _ := upstream(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = io.WriteString(w, `{"jsonrpc":"2.0","id":1,"result":{}}`)
	})
	f := requireOne(t, runBridge(t, opts, "", "   ", "\xef\xbb\xbf"+initialize+"\r"))
	if f.Error != nil {
		t.Fatalf("frame = %+v", f.Error)
	}
}

func TestCancellationStopsTheRequestWithoutAReply(t *testing.T) {
	started := make(chan struct{})
	opts, _ := upstream(t, func(w http.ResponseWriter, r *http.Request) {
		// The server only notices a disconnect once the body has been read.
		_, _ = io.Copy(io.Discard, r.Body)
		close(started)
		<-r.Context().Done()
	})
	stdinR, stdinW := io.Pipe()
	var stdout bytes.Buffer
	opts.Log = io.Discard
	done := make(chan error, 1)
	go func() { done <- New(&lockedWriter{w: &stdout}, opts).Run(context.Background(), stdinR) }()

	_, _ = io.WriteString(stdinW, `{"jsonrpc":"2.0","id":7,"method":"tools/call"}`+"\n")
	<-started
	_, _ = io.WriteString(stdinW, `{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7}}`+"\n")
	_ = stdinW.Close()

	select {
	case err := <-done:
		if err != nil {
			t.Fatal(err)
		}
	case <-time.After(30 * time.Second):
		t.Fatal("the cancelled request never ended")
	}
	if stdout.Len() != 0 {
		t.Fatalf("a cancelled request gets no response: %q", stdout.String())
	}
}

func TestSlowRequestDoesNotBlockOthers(t *testing.T) {
	release := make(chan struct{})
	opts, _ := upstream(t, func(w http.ResponseWriter, r *http.Request) {
		body, _ := io.ReadAll(r.Body)
		w.Header().Set("Content-Type", "application/json")
		if bytes.Contains(body, []byte(`"slow"`)) {
			<-release
			_, _ = io.WriteString(w, `{"jsonrpc":"2.0","id":1,"result":{}}`)
			return
		}
		_, _ = io.WriteString(w, `{"jsonrpc":"2.0","id":2,"result":{}}`)
	})
	stdinR, stdinW := io.Pipe()
	stdoutR, stdoutW := io.Pipe()
	done := make(chan error, 1)
	go func() { done <- New(stdoutW, opts).Run(context.Background(), stdinR) }()

	_, _ = io.WriteString(stdinW, `{"jsonrpc":"2.0","id":1,"method":"slow"}`+"\n")
	_, _ = io.WriteString(stdinW, `{"jsonrpc":"2.0","id":2,"method":"fast"}`+"\n")

	buf := make([]byte, 256)
	n, err := stdoutR.Read(buf)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(buf[:n]), `"id":2`) {
		t.Fatalf("first frame = %q", buf[:n])
	}
	close(release)
	if _, err := stdoutR.Read(buf); err != nil {
		t.Fatal(err)
	}
	_ = stdinW.Close()
	if err := <-done; err != nil {
		t.Fatal(err)
	}
}

func TestRequestTimeoutBecomesUnavailable(t *testing.T) {
	block := make(chan struct{})
	opts, _ := upstream(t, func(w http.ResponseWriter, r *http.Request) { <-block })
	t.Cleanup(func() { close(block) })
	opts.RequestTimeout = 50 * time.Millisecond
	f := requireOne(t, runBridge(t, opts, initialize))
	if f.Error == nil || f.Error.Code != codeUnavailable {
		t.Fatalf("frame = %+v", f.Error)
	}
}

func TestContextCancelEndsRun(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if err := New(io.Discard, Options{}).Run(ctx, strings.NewReader("")); err != nil {
		t.Fatal(err)
	}
}

func TestStdinErrorIsReturned(t *testing.T) {
	boom := errors.New("boom")
	r, w := io.Pipe()
	_ = w.CloseWithError(boom)
	if err := New(io.Discard, Options{}).Run(context.Background(), r); !errors.Is(err, boom) {
		t.Fatalf("err = %v", err)
	}
}

func TestStdoutFailureIsLoggedNotFatal(t *testing.T) {
	var log bytes.Buffer
	b := New(failingWriter{}, Options{Log: &log, StartupErr: apperr.New(apperr.KindAuth, "no key")})
	if err := b.Run(context.Background(), strings.NewReader(initialize+"\n")); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(log.String(), "cannot write to stdout") {
		t.Fatalf("log = %q", log.String())
	}
}

func TestNotificationFailureIsLoggedOnly(t *testing.T) {
	opts, _ := upstream(t, func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusServiceUnavailable)
	})
	r := runBridge(t, opts, `{"jsonrpc":"2.0","method":"notifications/initialized"}`)
	if r.stdout != "" || !strings.Contains(r.log, "upstream failure") {
		t.Fatalf("stdout = %q, log = %q", r.stdout, r.log)
	}
}

func TestNullIDIsANotification(t *testing.T) {
	opts, _ := upstream(t, func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusAccepted)
	})
	r := runBridge(t, opts, `{"jsonrpc":"2.0","id":null,"method":"notifications/x"}`)
	if r.stdout != "" {
		t.Fatalf("stdout = %q", r.stdout)
	}
}

func TestJSONBodyFailingMidwayIsAnError(t *testing.T) {
	opts, _ := upstream(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.Header().Set("Content-Length", "1000")
		_, _ = io.WriteString(w, `{"jsonrpc"`)
	})
	if f := requireOne(t, runBridge(t, opts, initialize)); f.Error == nil {
		t.Fatalf("frame = %+v", f)
	}
}

func jsonUpstream(t *testing.T, body string) Options {
	t.Helper()
	opts, _ := upstream(t, func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = io.WriteString(w, body)
	})
	return opts
}

// None of these answers the request, so the client must get an error for it and
// must not get the frame: left alone, it would wait forever on its own id.
func TestFramesThatDoNotAnswerGetAnError(t *testing.T) {
	for name, body := range map[string]string{
		"capitalized id":   `{"jsonrpc":"2.0","Id":1,"result":{}}`,
		"id and ID":        `{"jsonrpc":"2.0","id":2,"ID":1,"result":{}}`,
		"another id":       `{"jsonrpc":"2.0","id":2,"result":{}}`,
		"id 1.0 is not 1":  `{"jsonrpc":"2.0","id":1.0,"result":{}}`,
		"string id":        `{"jsonrpc":"2.0","id":"1","result":{}}`,
		"null id":          `{"jsonrpc":"2.0","id":null,"result":{}}`,
		"no jsonrpc":       `{"id":1,"result":{}}`,
		"wrong jsonrpc":    `{"jsonrpc":"1.0","id":1,"result":{}}`,
		"capital JSONRPC":  `{"JSONRPC":"2.0","id":1,"result":{}}`,
		"result and error": `{"jsonrpc":"2.0","id":1,"result":{},"error":{}}`,
		"neither":          `{"jsonrpc":"2.0","id":1}`,
		"repeated id":      `{"jsonrpc":"2.0","id":2,"id":1,"result":{}}`,
		"array":            `[{"jsonrpc":"2.0","id":1,"result":{}}]`,
		"scalar":           `1`,
	} {
		t.Run(name, func(t *testing.T) {
			f := requireOne(t, runBridge(t, jsonUpstream(t, body), initialize))
			if f.Error == nil || string(f.ID) != "1" {
				t.Fatalf("frame = %+v, id = %s", f.Error, f.ID)
			}
		})
	}
}

func TestErrorAnswerIsRelayed(t *testing.T) {
	f := requireOne(t, runBridge(t, jsonUpstream(t, `{"jsonrpc":"2.0","id":1,"error":{"code":-1,"message":"from upstream"}}`), initialize))
	if f.Error == nil || f.Error.Message != "from upstream" {
		t.Fatalf("frame = %+v", f.Error)
	}
}

func TestStreamIsClosedOnceAnswered(t *testing.T) {
	closed := make(chan struct{})
	opts, _ := upstream(t, func(w http.ResponseWriter, r *http.Request) {
		_, _ = io.Copy(io.Discard, r.Body)
		w.Header().Set("Content-Type", "text/event-stream")
		_, _ = io.WriteString(w, "data: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}\n\n")
		w.(http.Flusher).Flush()
		// A second answer the client must never see, and a stream held open.
		_, _ = io.WriteString(w, "data: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"second\":true}}\n\n")
		w.(http.Flusher).Flush()
		<-r.Context().Done()
		close(closed)
	})
	opts.RequestTimeout = time.Minute
	r := runBridge(t, opts, initialize)
	if len(r.frames) != 1 || strings.Contains(r.stdout, "second") {
		t.Fatalf("stdout = %q", r.stdout)
	}
	select {
	case <-closed:
	case <-time.After(10 * time.Second):
		t.Fatal("the stream was left open after the answer")
	}
}

// Held-open streams once filled all 16 slots and stopped stdin being read, so the
// cancellations that would have freed them were never seen.
func TestCancellationsArriveWhileEverySlotIsHeld(t *testing.T) {
	const requests = maxConcurrent + 8
	var started sync.WaitGroup
	started.Add(maxConcurrent)
	opts, _ := upstream(t, func(_ http.ResponseWriter, r *http.Request) {
		_, _ = io.Copy(io.Discard, r.Body)
		started.Done()
		<-r.Context().Done()
	})
	stdinR, stdinW := io.Pipe()
	var stdout bytes.Buffer
	done := make(chan error, 1)
	go func() { done <- New(&lockedWriter{w: &stdout}, opts).Run(context.Background(), stdinR) }()

	for i := range requests {
		_, _ = fmt.Fprintf(stdinW, `{"jsonrpc":"2.0","id":%d,"method":"tools/call"}`+"\n", i)
	}
	started.Wait()
	for i := range requests {
		_, _ = fmt.Fprintf(stdinW, `{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":%d}}`+"\n", i)
	}
	_ = stdinW.Close()
	select {
	case err := <-done:
		if err != nil {
			t.Fatal(err)
		}
	case <-time.After(30 * time.Second):
		t.Fatal("the cancellations never got through")
	}
}

func TestContextCancelEndsRunWhileStdinIsOpen(t *testing.T) {
	stdinR, stdinW := io.Pipe()
	defer func() { _ = stdinW.Close() }()
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() { done <- New(io.Discard, Options{}).Run(ctx, stdinR) }()
	cancel()
	select {
	case err := <-done:
		if err != nil {
			t.Fatal(err)
		}
	case <-time.After(10 * time.Second):
		t.Fatal("a signal should end the bridge even with stdin open")
	}
}

func TestContextCancelStopsInFlightRequests(t *testing.T) {
	started := make(chan struct{})
	opts, _ := upstream(t, func(_ http.ResponseWriter, r *http.Request) {
		_, _ = io.Copy(io.Discard, r.Body)
		close(started)
		<-r.Context().Done()
	})
	stdinR, stdinW := io.Pipe()
	defer func() { _ = stdinW.Close() }()
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() { done <- New(io.Discard, opts).Run(ctx, stdinR) }()
	_, _ = io.WriteString(stdinW, initialize+"\n")
	<-started
	cancel()
	select {
	case <-done:
	case <-time.After(10 * time.Second):
		t.Fatal("the bridge did not stop")
	}
}

func TestTooManyWaitingRequestsAreRefused(t *testing.T) {
	var stdout bytes.Buffer
	opts := Options{Endpoint: "https://unused.invalid", Client: http.DefaultClient}
	b := New(&stdout, opts)
	b.pending.Store(maxPending)
	var wg sync.WaitGroup
	b.dispatch(context.Background(), []byte(`{"jsonrpc":"2.0","id":1,"method":"x"}`), &wg)
	wg.Wait()
	if !strings.Contains(stdout.String(), "too many") {
		t.Fatalf("stdout = %q", stdout.String())
	}
}

func TestRequestTimingOutInTheQueueIsAnswered(t *testing.T) {
	var stdout bytes.Buffer
	b := New(&stdout, Options{RequestTimeout: time.Millisecond})
	for range maxConcurrent {
		b.slots <- struct{}{}
	}
	var wg sync.WaitGroup
	b.dispatch(context.Background(), []byte(`{"jsonrpc":"2.0","id":1,"method":"x"}`), &wg)
	wg.Wait()
	if !strings.Contains(stdout.String(), `"id":1`) || !strings.Contains(stdout.String(), `"error"`) {
		t.Fatalf("stdout = %q", stdout.String())
	}
}

func TestEscapedKeyInAFrameIsScrubbed(t *testing.T) {
	// The escape sits inside the secret half, so no byte-level match can see it.
	escaped := strings.Replace(testKey, "cc", string(rune(92))+"u0063c", 1)
	r := runBridge(t, jsonUpstream(t, `{"jsonrpc":"2.0","id":1,"result":{"text":"`+escaped+`"}}`), initialize)
	if strings.Contains(r.stdout, "ddeeff") || !strings.Contains(r.stdout, redact.Placeholder) {
		t.Fatalf("stdout = %q", r.stdout)
	}
}

func TestSecretHalfInAFrameIsScrubbed(t *testing.T) {
	r := runBridge(t, jsonUpstream(t, `{"jsonrpc":"2.0","id":1,"result":{"text":"8899aabbccddeeff"}}`), initialize)
	if strings.Contains(r.stdout, "8899aabbccddeeff") {
		t.Fatalf("stdout = %q", r.stdout)
	}
}

func TestKeyInAMemberNameDoesNotCollapseMembers(t *testing.T) {
	escaped := strings.Replace(testKey, "_", string(rune(92))+"u005f", 1)
	r := runBridge(t, jsonUpstream(t, `{"jsonrpc":"2.0","id":1,"result":{"`+testKey+`":1,"`+escaped+`":2}}`), initialize)
	var frame struct {
		Result map[string]int `json:"result"`
	}
	if err := json.Unmarshal([]byte(r.stdout), &frame); err != nil {
		t.Fatal(err)
	}
	if len(frame.Result) != 2 {
		t.Fatalf("stdout = %q", r.stdout)
	}
}

// The id is the client's own. Scrubbing it would leave the client waiting forever.
func TestKeyUsedAsAnIDStillCorrelates(t *testing.T) {
	var stdout, log bytes.Buffer
	opts := jsonUpstream(t, `{"jsonrpc":"2.0","id":"`+testKey+`","result":{"text":"`+testKey+`"}}`)
	opts.Redactor = redact.New()
	opts.Redactor.Add(testKey)
	opts.Log = &log
	request := `{"jsonrpc":"2.0","id":"` + testKey + `","method":"ping"}` + "\n"
	if err := New(&stdout, opts).Run(context.Background(), strings.NewReader(request)); err != nil {
		t.Fatal(err)
	}
	var frame struct {
		ID     string `json:"id"`
		Result struct {
			Text string `json:"text"`
		} `json:"result"`
		Error json.RawMessage `json:"error"`
	}
	if err := json.Unmarshal(stdout.Bytes(), &frame); err != nil {
		t.Fatalf("stdout = %q: %v", stdout.String(), err)
	}
	if frame.ID != testKey || frame.Error != nil {
		t.Fatalf("stdout = %q", stdout.String())
	}
	if frame.Result.Text != redact.Placeholder {
		t.Fatalf("stdout = %q", stdout.String())
	}
}

func TestStartupLogIsSanitized(t *testing.T) {
	r := runBridge(t, Options{StartupErr: errors.New("bad\x1b]52;c;x\x07\nthing")})
	if strings.Contains(r.log, "\x1b") || strings.Contains(r.log, "\x07") || strings.Count(r.log, "\n") != 1 {
		t.Fatalf("log = %q", r.log)
	}
}

func TestInboundMemberNamesAreExact(t *testing.T) {
	for name, line := range map[string]string{
		"capital JSONRPC": `{"JSONRPC":"2.0","id":1,"method":"ping"}`,
		"repeated method": `{"jsonrpc":"2.0","id":1,"method":"a","method":"b"}`,
		"trailing data":   `{"jsonrpc":"2.0","id":1,"method":"a"} {"x":1}`,
	} {
		t.Run(name, func(t *testing.T) {
			opts, _ := upstream(t, func(http.ResponseWriter, *http.Request) {
				t.Error("this should not go upstream")
			})
			if f := requireOne(t, runBridge(t, opts, line)); f.Error == nil {
				t.Fatalf("frame = %+v", f)
			}
		})
	}
}

func TestObjectFieldsRejectsTruncatedInput(t *testing.T) {
	for _, raw := range []string{``, `{`, `{"a"`, `{"a":`, `{"a":1`, `{1:2}`} {
		if _, err := objectFields([]byte(raw)); err == nil {
			t.Errorf("objectFields(%q) should fail", raw)
		}
	}
}

func TestUnclassifiedErrorBody(t *testing.T) {
	if got := errorBody(errors.New("x")); got.Code != codeInternal {
		t.Fatalf("code = %d", got.Code)
	}
}

func TestCancelledIDOfBadParams(t *testing.T) {
	m := &message{method: "notifications/cancelled", params: json.RawMessage(`"nope"`)}
	if m.cancelledID() != "" {
		t.Fatal("bad params should name no request")
	}
}

func TestBadEndpointBecomesAnError(t *testing.T) {
	opts := Options{Endpoint: "http://bad host/mcp", Client: http.DefaultClient}
	f := requireOne(t, runBridge(t, opts, initialize))
	if f.Error == nil {
		t.Fatalf("frame = %+v", f)
	}
}

type lockedWriter struct {
	mu sync.Mutex
	w  io.Writer
}

func (l *lockedWriter) Write(p []byte) (int, error) {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.w.Write(p)
}

type failingWriter struct{}

func (failingWriter) Write([]byte) (int, error) { return 0, errors.New("closed") }
