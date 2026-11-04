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

func TestCheckDialAddressRejectsWhatItCannotParse(t *testing.T) {
	for _, address := range []string{"no-port", "not-an-ip:80"} {
		if err := checkDialAddress(address); err == nil {
			t.Errorf("checkDialAddress(%q) should fail", address)
		}
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
