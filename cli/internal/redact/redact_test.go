package redact

import (
	"bytes"
	"encoding/base64"
	"io"
	"net/url"
	"strings"
	"testing"
)

const secret = "mem_0123456789abcdef_feedfacefeedface+/="

func TestRedactsEveryEncoding(t *testing.T) {
	r := New()
	r.Add(secret)
	for name, value := range map[string]string{
		"raw":       secret,
		"query":     url.QueryEscape(secret),
		"path":      url.PathEscape(secret),
		"base64":    base64.StdEncoding.EncodeToString([]byte(secret)),
		"rawbase64": base64.RawURLEncoding.EncodeToString([]byte(secret)),
	} {
		if got := r.String("before " + value + " after"); got != "before "+Placeholder+" after" {
			t.Errorf("%s: got %q", name, got)
		}
		if got := string(r.Bytes([]byte(value))); got != Placeholder {
			t.Errorf("%s bytes: got %q", name, got)
		}
	}
}

func TestStringLeavesOtherTokensAloneButDiagnosticDoesNot(t *testing.T) {
	r := New()
	r.Add(secret)
	other := "mem_aaaabbbb_ccccdddd"
	if got := r.String("mentions " + other); !strings.Contains(got, other) {
		t.Fatalf("stdout scrubbing should keep other tokens: %q", got)
	}
	for _, input := range []string{
		"mentions " + other,
		"Authorization: Bearer opaque-token-value",
		`{"authorization":"Bearer opaque-token-value"}`,
		"authorization=opaque-token-value; next",
		"header was bearer opaque-token-value",
	} {
		got := r.Diagnostic(input)
		if strings.Contains(got, other) || strings.Contains(got, "opaque-token-value") {
			t.Errorf("Diagnostic(%q) = %q", input, got)
		}
		if !strings.Contains(got, Placeholder) {
			t.Errorf("Diagnostic(%q) lost its placeholder: %q", input, got)
		}
	}
}

func TestDiagnosticKeepsOrdinaryText(t *testing.T) {
	r := New()
	for _, input := range []string{
		"A valid API key is required.",
		"memory_usage_high is a collection name",
		"authentication failed (401)",
	} {
		if got := r.Diagnostic(input); got != input {
			t.Errorf("Diagnostic(%q) = %q", input, got)
		}
	}
}

func TestWritersReportOriginalLength(t *testing.T) {
	r := New()
	r.Add(secret)
	for name, wrap := range map[string]func(*bytes.Buffer) int{
		"stdout": func(b *bytes.Buffer) int {
			n, err := r.Writer(b).Write([]byte(secret))
			if err != nil {
				t.Fatal(err)
			}
			return n
		},
		"stderr": func(b *bytes.Buffer) int {
			n, err := r.DiagnosticWriter(b).Write([]byte(secret))
			if err != nil {
				t.Fatal(err)
			}
			return n
		},
	} {
		var out bytes.Buffer
		if n := wrap(&out); n != len(secret) {
			t.Errorf("%s: n = %d, want %d (fmt treats a short count as an error)", name, n, len(secret))
		}
		if strings.Contains(out.String(), "feedface") {
			t.Errorf("%s leaked: %q", name, out.String())
		}
	}
}

type brokenWriter struct{}

func (brokenWriter) Write([]byte) (int, error) { return 0, io.ErrClosedPipe }

func TestWritersReportFailures(t *testing.T) {
	r := New()
	if _, err := r.Writer(brokenWriter{}).Write([]byte("x")); err == nil {
		t.Error("Writer should report the failure")
	}
	if _, err := r.DiagnosticWriter(brokenWriter{}).Write([]byte("x")); err == nil {
		t.Error("DiagnosticWriter should report the failure")
	}
}

func TestEmptySecretIsIgnored(t *testing.T) {
	r := New()
	r.Add("")
	if got := r.String("unchanged"); got != "unchanged" {
		t.Fatalf("got %q", got)
	}
}
