package apperr

import (
	"context"
	"errors"
	"fmt"
	"net"
	"net/http"
	"net/url"
	"strings"
	"testing"
	"time"
)

func problemHeader() http.Header {
	h := http.Header{}
	h.Set("Content-Type", "application/problem+json")
	return h
}

func TestFromResponseClassifies(t *testing.T) {
	cases := []struct {
		status int
		kind   Kind
		exit   int
	}{
		{400, KindUsage, ExitUsage},
		{401, KindAuth, ExitAuth},
		{403, KindForbidden, ExitAuth},
		{404, KindNotFound, ExitNotFound},
		{422, KindUsage, ExitUsage},
		{429, KindRateLimited, ExitRateLimited},
		{500, KindGeneric, ExitGeneric},
		{502, KindUnavailable, ExitUnavailable},
		{503, KindUnavailable, ExitUnavailable},
		{504, KindUnavailable, ExitUnavailable},
	}
	for _, c := range cases {
		t.Run(fmt.Sprint(c.status), func(t *testing.T) {
			e := FromResponse(c.status, problemHeader(), []byte(`{"title":"T","detail":"D","traceId":"abc"}`))
			if e.Kind != c.kind {
				t.Errorf("kind = %q, want %q", e.Kind, c.kind)
			}
			if got := ExitCode(e); got != c.exit {
				t.Errorf("exit = %d, want %d", got, c.exit)
			}
			if !strings.Contains(e.Message, "D") || !strings.Contains(e.Message, "trace abc") {
				t.Errorf("message = %q", e.Message)
			}
		})
	}
}

func TestAuthAndForbiddenHaveDistinctMessagesAndTheSameExit(t *testing.T) {
	unauthorized := FromResponse(401, nil, nil)
	forbidden := FromResponse(403, nil, nil)
	if unauthorized.Message == forbidden.Message {
		t.Fatal("401 and 403 should read differently")
	}
	if !strings.Contains(unauthorized.Message, "auth login") {
		t.Errorf("401 message = %q", unauthorized.Message)
	}
	if !strings.Contains(forbidden.Message, "scope") {
		t.Errorf("403 message = %q", forbidden.Message)
	}
}

func TestForbiddenHintFollowsTheMissingScope(t *testing.T) {
	cases := map[string]string{
		`{"detail":"This API key lacks the 'read' scope.","code":"insufficient_scope"}`:   "by design",
		`{"detail":"This API key lacks the 'admin' scope.","code":"insufficient_scope"}`:  "--bootstrap-admin-key",
		`{"detail":"This API key lacks the 'ingest' scope.","code":"insufficient_scope"}`: "lacks the scope or collection",
		`{"detail":"Operations act on every collection.","code":"collection_scoped_key"}`: "--scope admin",
		`{"detail":"Not in this key's collections.","code":"collection_forbidden"}`:       "lacks the scope or collection",
		`{"detail":"lacks the 'read' scope","code":"something_else"}`:                     "lacks the scope or collection",
	}
	for body, want := range cases {
		if e := FromResponse(403, problemHeader(), []byte(body)); !strings.Contains(e.Message, want) {
			t.Errorf("%s: message = %q", body, e.Message)
		}
	}
}

func TestRawBodiesAreNeverQuoted(t *testing.T) {
	const marker = "RAW-BODY-CONTENT"
	for name, build := range map[string]func() (http.Header, []byte){
		"html": func() (http.Header, []byte) {
			h := http.Header{}
			h.Set("Content-Type", "text/html")
			return h, []byte("<html>" + marker + "</html>")
		},
		"broken json": func() (http.Header, []byte) {
			return problemHeader(), []byte(`{"detail":"` + marker)
		},
		"extension field": func() (http.Header, []byte) {
			return problemHeader(), []byte(`{"title":"T","debug":"` + marker + `"}`)
		},
	} {
		t.Run(name, func(t *testing.T) {
			h, body := build()
			if e := FromResponse(500, h, body); strings.Contains(e.Message, marker) {
				t.Fatalf("message quoted the body: %q", e.Message)
			}
		})
	}
}

func TestValidationErrorsAreSummarized(t *testing.T) {
	e := FromResponse(400, problemHeader(), []byte(`{"title":"Bad","errors":{"limit":["too big"],"ids":["empty"]}}`))
	if !strings.Contains(e.Message, "ids: empty; limit: too big") {
		t.Fatalf("message = %q", e.Message)
	}
}

func TestRetryAfter(t *testing.T) {
	now := time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)
	cases := []struct {
		value string
		want  time.Duration
	}{
		{"", 0},
		{"30", 30 * time.Second},
		{" 5 ", 5 * time.Second},
		{"0", 0},
		{"-5", 0},
		{"soon", 0},
		{"99999999999999", 7 * 24 * time.Hour},
		{now.Add(90 * time.Second).Format(http.TimeFormat), 90 * time.Second},
		{now.Add(-time.Hour).Format(http.TimeFormat), 0},
	}
	for _, c := range cases {
		if got := ParseRetryAfter(c.value, now); got != c.want {
			t.Errorf("ParseRetryAfter(%q) = %v, want %v", c.value, got, c.want)
		}
	}
}

func TestRateLimitMessageCarriesRetryAfter(t *testing.T) {
	h := problemHeader()
	h.Set("Retry-After", "42")
	e := FromResponse(429, h, nil)
	if e.RetryAfter != 42*time.Second || !strings.Contains(e.Message, "retry after 42s") {
		t.Fatalf("%+v", e)
	}
}

func TestTraceIDFallsBackToHeader(t *testing.T) {
	h := http.Header{}
	h.Set("X-Trace-Id", "from-header")
	if e := FromResponse(500, h, nil); e.TraceID != "from-header" {
		t.Fatalf("trace = %q", e.TraceID)
	}
}

func TestFromTransport(t *testing.T) {
	dial := &url.Error{Op: "Get", URL: "https://host/v1/search?query=private", Err: &net.OpError{Op: "dial", Err: errors.New("refused")}}
	got := FromTransport(dial)
	if ExitCode(got) != ExitUnavailable {
		t.Errorf("dial failure exit = %d", ExitCode(got))
	}
	if strings.Contains(got.Error(), "query=private") {
		t.Errorf("message should not quote the URL: %q", got)
	}
	if ExitCode(FromTransport(context.DeadlineExceeded)) != ExitUnavailable {
		t.Error("timeout should be unavailable")
	}
	if ExitCode(FromTransport(context.Canceled)) != ExitGeneric {
		t.Error("cancel should be generic")
	}
	insecure := New(KindInsecure, "refused")
	if ExitCode(FromTransport(&url.Error{Err: insecure})) != ExitInsecure {
		t.Error("classified errors should survive url.Error")
	}
	if FromTransport(nil) != nil {
		t.Error("nil should stay nil")
	}
}

type timeoutError struct{}

func (timeoutError) Error() string   { return "slow" }
func (timeoutError) Timeout() bool   { return true }
func (timeoutError) Temporary() bool { return true }

func TestFromTransportMoreCases(t *testing.T) {
	if ExitCode(FromTransport(&net.DNSError{Err: "no such host"})) != ExitUnavailable {
		t.Error("a DNS failure should be unavailable")
	}
	if ExitCode(FromTransport(timeoutError{})) != ExitUnavailable {
		t.Error("a network timeout should be unavailable")
	}
	plain := errors.New("odd")
	got := FromTransport(plain)
	if ExitCode(got) != ExitGeneric || !errors.Is(got, plain) {
		t.Errorf("got %v", got)
	}
	existing := New(KindAuth, "x")
	if FromTransport(fmt.Errorf("wrapped: %w", existing)) != error(existing) {
		t.Error("a classified error should pass through")
	}
}

func TestExitCodeOfPlainErrorIsGeneric(t *testing.T) {
	if ExitCode(errors.New("x")) != ExitGeneric || ExitCode(nil) != ExitOK {
		t.Fatal("unexpected exit codes")
	}
	if ExitCode(fmt.Errorf("wrapped: %w", New(KindNotFound, "x"))) != ExitNotFound {
		t.Fatal("wrapped errors should keep their kind")
	}
}
