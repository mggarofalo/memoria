package transport

import (
	"bytes"
	"errors"
	"net/http"
	"strings"
	"testing"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
)

type failingRoundTripper struct{ err error }

func (f failingRoundTripper) RoundTrip(*http.Request) (*http.Response, error) { return nil, f.err }

func TestGuardReportsTransportFailures(t *testing.T) {
	var log bytes.Buffer
	client := newClientWithBase(Options{BaseURL: mustParse(t, "https://memoria.example.com"), Verbose: &log, UserAgent: "test"},
		failingRoundTripper{err: errors.New("boom")})
	resp, err := client.Get("https://memoria.example.com/x")
	if err == nil {
		_ = resp.Body.Close()
		t.Fatal("expected an error")
	}
	if !strings.Contains(log.String(), "< failed after") {
		t.Fatalf("log = %q", log.String())
	}
}

func TestClassifiedDialRefusalSurvivesWrapping(t *testing.T) {
	refusal := apperr.New(apperr.KindInsecure, "refused")
	wrapped := errors.Join(errors.New("dial"), refusal)
	if got := classifyDialRefusal(wrapped); !errors.Is(got, refusal) || got != error(refusal) {
		t.Fatalf("got %v", got)
	}
	plain := errors.New("plain")
	if got := classifyDialRefusal(plain); got != plain {
		t.Fatalf("got %v", got)
	}
}

func TestCheckLoopbackDialRejectsWhatItCannotParse(t *testing.T) {
	for _, address := range []string{"no-port", "not-an-ip:80"} {
		if err := checkLoopbackDial(address); err == nil {
			t.Errorf("checkLoopbackDial(%q) should fail", address)
		}
	}
}

// The largest valid response is a multi-get: 16 MiB of bodies and 16 MiB of
// metadata. The cap must clear that, and not by much, since it bounds decompression.
func TestResponseCap(t *testing.T) {
	const largestValid = 32 << 20
	if DefaultMaxResponseBytes < largestValid || DefaultMaxResponseBytes > 64<<20 {
		t.Fatalf("cap = %d", DefaultMaxResponseBytes)
	}
}

func TestNewClientNeedsABaseURL(t *testing.T) {
	if _, err := NewClient(Options{}); err == nil {
		t.Fatal("expected an error")
	}
}

func TestOriginOfAnUnknownSchemeHasNoDefaultPort(t *testing.T) {
	if got := Origin(mustParse(t, "ftp://host")); !strings.HasPrefix(got, "ftp://host") {
		t.Fatalf("got %q", got)
	}
}
