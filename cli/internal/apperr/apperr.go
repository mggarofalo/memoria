// Package apperr maps failures to the CLI's exit codes and to messages that are
// safe to show. Response bodies are never quoted wholesale: only the named
// problem-details fields reach a message, and only after the caller has passed
// it through redaction and terminal sanitizing on the way to stderr.
package apperr

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"mime"
	"net"
	"net/http"
	"net/url"
	"sort"
	"strconv"
	"strings"
	"time"
)

// Exit codes. Scripts depend on these; do not renumber.
const (
	ExitOK          = 0
	ExitGeneric     = 1
	ExitUsage       = 2
	ExitAuth        = 3
	ExitNotFound    = 4
	ExitUnavailable = 5
	ExitRateLimited = 6
	ExitInsecure    = 7
)

// Kind classifies a failure.
type Kind string

// The failure kinds. They also appear in the data of MCP bridge errors.
const (
	KindGeneric     Kind = "error"
	KindUsage       Kind = "usage"
	KindAuth        Kind = "auth"
	KindForbidden   Kind = "forbidden"
	KindNotFound    Kind = "not_found"
	KindUnavailable Kind = "unavailable"
	KindRateLimited Kind = "rate_limited"
	KindInsecure    Kind = "insecure"
)

// Error is a classified failure.
type Error struct {
	Kind       Kind
	Status     int           // HTTP status, when there was a response.
	Code       string        // The server's own code for a refusal, when it gave one.
	Message    string        // Safe to display after redaction and sanitizing.
	RetryAfter time.Duration // Zero when the server gave no usable value.
	TraceID    string
	cause      error
}

func (e *Error) Error() string { return e.Message }

// Unwrap exposes the underlying cause, if any.
func (e *Error) Unwrap() error { return e.cause }

// New creates a classified error with a formatted message.
func New(kind Kind, format string, args ...any) *Error {
	return &Error{Kind: kind, Message: fmt.Sprintf(format, args...)}
}

// Wrap classifies cause, keeping it reachable through errors.Is.
func Wrap(kind Kind, cause error, message string) *Error {
	return &Error{Kind: kind, Message: message, cause: cause}
}

// ExitCode returns the process exit code for err.
func ExitCode(err error) int {
	if err == nil {
		return ExitOK
	}
	var e *Error
	if errors.As(err, &e) {
		switch e.Kind {
		case KindUsage:
			return ExitUsage
		case KindAuth, KindForbidden:
			return ExitAuth
		case KindNotFound:
			return ExitNotFound
		case KindUnavailable:
			return ExitUnavailable
		case KindRateLimited:
			return ExitRateLimited
		case KindInsecure:
			return ExitInsecure
		}
	}
	return ExitGeneric
}

// problem holds the problem-details fields the CLI will repeat.
type problem struct {
	Title   string              `json:"title"`
	Detail  string              `json:"detail"`
	Code    string              `json:"code"`
	TraceID string              `json:"traceId"`
	Errors  map[string][]string `json:"errors"`
}

// FromResponse classifies a non-success response.
func FromResponse(status int, header http.Header, body []byte) *Error {
	if header == nil {
		header = http.Header{}
	}
	p := parseProblem(header, body)
	class, ok := statusClasses[status]
	if !ok {
		class = statusClass{kind: KindGeneric, message: "request failed"}
	}
	e := &Error{Kind: class.kind, Status: status, Code: p.Code, TraceID: firstNonEmpty(p.TraceID, header.Get("X-Trace-Id"))}

	hint := class.hint
	if status == http.StatusForbidden {
		hint = forbiddenHint(p, hint)
	}
	if status == http.StatusTooManyRequests {
		e.RetryAfter = ParseRetryAfter(header.Get("Retry-After"), time.Now())
		if e.RetryAfter > 0 {
			hint = "retry after " + formatSeconds(e.RetryAfter)
		}
	}
	e.Message = composeMessage(fmt.Sprintf("%s (HTTP %d)", class.message, status), p, hint, e.TraceID)
	return e
}

type statusClass struct {
	kind          Kind
	message, hint string
}

var statusClasses = map[int]statusClass{
	http.StatusBadRequest:          {kind: KindUsage, message: "invalid request"},
	http.StatusUnprocessableEntity: {kind: KindUsage, message: "invalid request"},
	http.StatusUnauthorized:        {KindAuth, "authentication failed", "check the key, or run 'memoria auth login'"},
	http.StatusForbidden:           {KindForbidden, "forbidden", "this key lacks the scope or collection access the request needs"},
	http.StatusNotFound:            {kind: KindNotFound, message: "not found"},
	http.StatusTooManyRequests:     {kind: KindRateLimited, message: "rate limited"},
	http.StatusBadGateway:          {kind: KindUnavailable, message: "server unavailable"},
	http.StatusServiceUnavailable:  {kind: KindUnavailable, message: "server unavailable"},
	http.StatusGatewayTimeout:      {kind: KindUnavailable, message: "server unavailable"},
}

// forbiddenHint says what to do about a missing scope. The server names the scope in
// its detail. Scopes do not imply one another, so an admin key that cannot read is
// working as designed, which nobody guesses.
func forbiddenHint(p problem, fallback string) string {
	switch {
	case p.Code != "insufficient_scope":
		return fallback
	case strings.Contains(p.Detail, "'read'"):
		return "scopes do not imply one another, so an admin key cannot read, by design. " +
			"Issue a key to search with: memoria keys create --label <name> --scope read --collection <name>, " +
			"then use it with memoria auth login --profile <name>"
	case strings.Contains(p.Detail, "'admin'"):
		return "managing keys needs a key with the admin scope. The first one comes from running the server with --bootstrap-admin-key"
	}
	return fallback
}

func composeMessage(summary string, p problem, hint, traceID string) string {
	if detail := firstNonEmpty(p.Detail, p.Title); detail != "" {
		summary += ": " + detail
	}
	parts := []string{summary}
	if v := validationSummary(p.Errors); v != "" {
		parts = append(parts, v)
	}
	if hint != "" {
		parts = append(parts, hint)
	}
	if traceID != "" {
		parts = append(parts, "trace "+traceID)
	}
	return strings.Join(parts, "; ")
}

// FromTransport classifies an error from the HTTP client. Timeouts and failed
// connections mean the server is unavailable. url.Error is unwrapped because it
// quotes the full request URL, which adds noise and nothing a user can act on.
func FromTransport(err error) error {
	if err == nil {
		return nil
	}
	var existing *Error
	if errors.As(err, &existing) {
		return existing
	}
	return classifyNetwork(err, withoutURL(err))
}

func withoutURL(err error) error {
	var ue *url.Error
	if errors.As(err, &ue) && ue.Err != nil {
		return ue.Err
	}
	return err
}

func classifyNetwork(err, cause error) error {
	var netErr net.Error
	var dnsErr *net.DNSError
	var opErr *net.OpError
	switch {
	case errors.Is(err, context.Canceled):
		return Wrap(KindGeneric, err, "request canceled")
	case errors.Is(err, context.DeadlineExceeded):
		return Wrap(KindUnavailable, err, "request timed out")
	case errors.As(err, &dnsErr), errors.As(err, &opErr):
		return Wrap(KindUnavailable, err, "cannot reach server: "+cause.Error())
	case errors.As(err, &netErr) && netErr.Timeout():
		return Wrap(KindUnavailable, err, "request timed out")
	}
	return Wrap(KindGeneric, err, "request failed: "+cause.Error())
}

// ParseRetryAfter reads a Retry-After value, delta seconds or an HTTP date. It
// returns zero for anything unusable, including times in the past.
func ParseRetryAfter(value string, now time.Time) time.Duration {
	value = strings.TrimSpace(value)
	if value == "" {
		return 0
	}
	if seconds, err := strconv.ParseInt(value, 10, 64); err == nil {
		// Cap the value so a hostile header cannot overflow time.Duration.
		const maxSeconds = 7 * 24 * 60 * 60
		if seconds <= 0 {
			return 0
		}
		if seconds > maxSeconds {
			seconds = maxSeconds
		}
		return time.Duration(seconds) * time.Second
	}
	if when, err := http.ParseTime(value); err == nil {
		if d := when.Sub(now); d > 0 {
			return d
		}
	}
	return 0
}

func parseProblem(header http.Header, body []byte) problem {
	var p problem
	if len(body) == 0 {
		return p
	}
	media, _, err := mime.ParseMediaType(header.Get("Content-Type"))
	if err != nil || !strings.HasSuffix(media, "json") {
		return p
	}
	// A body that does not parse is ignored, never quoted.
	_ = json.Unmarshal(body, &p)
	return p
}

func validationSummary(fields map[string][]string) string {
	if len(fields) == 0 {
		return ""
	}
	names := make([]string, 0, len(fields))
	for name := range fields {
		names = append(names, name)
	}
	sort.Strings(names)
	parts := make([]string, 0, len(names))
	for _, name := range names {
		parts = append(parts, name+": "+strings.Join(fields[name], ", "))
	}
	return strings.Join(parts, "; ")
}

func firstNonEmpty(values ...string) string {
	for _, v := range values {
		if v != "" {
			return v
		}
	}
	return ""
}

func formatSeconds(d time.Duration) string {
	seconds := int64((d + time.Second - 1) / time.Second)
	return strconv.FormatInt(seconds, 10) + "s"
}
