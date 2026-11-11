package transport

import (
	"bytes"
	"context"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
)

const testKey = "mem_0011223344556677_8899aabbccddeeff"

func mustParse(t *testing.T, raw string) *url.URL {
	t.Helper()
	u, err := url.Parse(raw)
	if err != nil {
		t.Fatal(err)
	}
	return u
}

func TestParseBaseURL(t *testing.T) {
	good := []string{"https://memoria.example.com", "https://memoria.example.com/", "http://127.0.0.1:8080", "https://host/prefix"}
	for _, raw := range good {
		if _, err := ParseBaseURL(raw); err != nil {
			t.Errorf("ParseBaseURL(%q) = %v", raw, err)
		}
	}
	bad := []string{
		"",
		"memoria.example.com",
		"ftp://host",
		"https://",
		"https://user:" + testKey + "@host",
		"https://" + testKey + "@host",
		"https://host?key=" + testKey,
		"https://host#" + testKey,
		"https://host/%zz" + testKey,
		"https://exam\u0130ple.test",
		"https://exam%C4%B0ple.test",
		"https://ex\u212Aample.test",
		"http://127.0.0.1\u3002evil.test",
	}
	for _, raw := range bad {
		_, err := ParseBaseURL(raw)
		if err == nil {
			t.Errorf("ParseBaseURL(%q) should fail", raw)
			continue
		}
		if strings.Contains(err.Error(), testKey) {
			t.Errorf("error repeats the input: %v", err)
		}
		if apperr.ExitCode(err) != apperr.ExitUsage {
			t.Errorf("ParseBaseURL(%q) exit = %d", raw, apperr.ExitCode(err))
		}
	}
}

func TestClassify(t *testing.T) {
	cases := map[string]Class{
		"https://8.8.8.8":                ClassHTTPS,
		"http://localhost":               ClassLoopback,
		"http://LOCALHOST:8080":          ClassLoopback,
		"http://localhost.":              ClassLoopback,
		"http://127.0.0.1":               ClassLoopback,
		"http://127.8.8.8":               ClassLoopback,
		"http://[::1]:80":                ClassLoopback,
		"http://[::ffff:127.0.0.1]":      ClassLoopback,
		"http://10.1.2.3":                ClassPrivate,
		"http://172.16.0.1":              ClassPrivate,
		"http://172.31.255.255":          ClassPrivate,
		"http://192.168.1.20:8080":       ClassPrivate,
		"http://[fd00::1]":               ClassPrivate,
		"http://[fd00:ec2::254]":         ClassPublic, // AWS metadata, inside the ULA range.
		"http://[::ffff:10.0.0.1]":       ClassPrivate,
		"http://8.8.8.8":                 ClassPublic,
		"http://172.32.0.1":              ClassPublic,
		"http://169.254.169.254":         ClassPublic, // Cloud metadata is not a LAN.
		"http://100.64.0.1":              ClassPublic,
		"http://0.0.0.0":                 ClassPublic,
		"http://[2001:4860:4860::8888]":  ClassPublic,
		"http://[::ffff:8.8.8.8]":        ClassPublic,
		"http://memoria.lan":             ClassHostname,
		"http://localhost.evil.test":     ClassHostname,
		"http://127.0.0.1.evil.test":     ClassHostname,
		"http://10.0.0.1.nip.io":         ClassHostname,
		"http://2130706433":              ClassHostname,
		"http://0x7f.0.0.1":              ClassHostname,
		"http://evillocalhost":           ClassHostname,
		"http://localhost@8.8.8.8/":      ClassPublic,
		"http://8.8.8.8#@localhost/path": ClassPublic,
	}
	for raw, want := range cases {
		if got := Classify(mustParse(t, raw)); got != want {
			t.Errorf("Classify(%q) = %v, want %v", raw, got, want)
		}
	}
}

func TestEvaluate(t *testing.T) {
	cases := []struct {
		url      string
		allow    bool
		wantWarn bool
		wantErr  bool
	}{
		{"https://8.8.8.8", false, false, false},
		{"http://127.0.0.1", false, false, false},
		{"http://localhost", false, false, false},
		{"http://192.168.1.1", false, true, false},
		{"http://memoria.lan", false, false, true},
		{"http://memoria.internal:8080", false, false, true},
		{"http://[fd00:ec2::254]", false, false, true},
		{"http://169.254.169.254", false, false, true},
		{"http://memoria.lan", true, true, false},
		{"http://8.8.8.8", false, false, true},
		{"http://8.8.8.8", true, true, false},
		{"http://memoria.example.com", true, true, false},
	}
	for _, c := range cases {
		warning, err := Evaluate(mustParse(t, c.url), c.allow)
		if (warning != "") != c.wantWarn {
			t.Errorf("Evaluate(%q, %v) warning = %q", c.url, c.allow, warning)
		}
		if (err != nil) != c.wantErr {
			t.Errorf("Evaluate(%q, %v) err = %v", c.url, c.allow, err)
		}
		if err != nil && apperr.ExitCode(err) != apperr.ExitInsecure {
			t.Errorf("exit = %d", apperr.ExitCode(err))
		}
	}
}

func TestCheckLoopbackDial(t *testing.T) {
	for _, addr := range []string{"127.0.0.1:80", "[::1]:80", "127.8.8.8:80"} {
		if err := checkLoopbackDial(addr); err != nil {
			t.Errorf("checkLoopbackDial(%q) = %v", addr, err)
		}
	}
	// localhost answering with anything else is a poisoned resolver.
	for _, addr := range []string{"8.8.8.8:80", "10.0.0.1:80", "192.168.0.5:8080", "[fd00::1]:80", "[2001:4860:4860::8888]:80"} {
		if apperr.ExitCode(checkLoopbackDial(addr)) != apperr.ExitInsecure {
			t.Errorf("checkLoopbackDial(%q) should refuse", addr)
		}
	}
}

func TestLoopbackDialIsWiredForTheNameLocalhostOnly(t *testing.T) {
	refused := newBaseTransport(mustParse(t, "http://localhost:8080"), false)
	_, err := refused.DialContext(context.Background(), "tcp", "8.8.8.8:80")
	if apperr.ExitCode(classifyDialRefusal(err)) != apperr.ExitInsecure {
		t.Fatalf("dial = %v", err)
	}
	for raw, allow := range map[string]bool{
		"http://127.0.0.1:8080": false,
		"http://10.0.0.1":       false,
		"https://localhost":     false,
		"http://localhost:8080": true,
		"http://memoria.lan":    true,
	} {
		if needsLoopbackDial(mustParse(t, raw), allow) {
			t.Errorf("%s (override %v) should not need the check", raw, allow)
		}
	}
}

func TestPlainHTTPNeverUsesAProxy(t *testing.T) {
	t.Setenv("HTTP_PROXY", "http://proxy.example.test:3128")
	tr := newBaseTransport(mustParse(t, "http://192.168.1.5"), false)
	req := httptest.NewRequest(http.MethodGet, "http://192.168.1.5/v1/status", nil)
	proxy, err := tr.Proxy(req)
	if err != nil || proxy != nil {
		t.Fatalf("proxy = %v, err = %v; a plain-http proxy would see the key", proxy, err)
	}
}

func TestSameOrigin(t *testing.T) {
	cases := []struct {
		a, b string
		want bool
	}{
		{"https://a.example", "https://a.example/path", true},
		{"https://a.example", "https://A.EXAMPLE:443/", true},
		{"http://a.example:80", "http://a.example", true},
		{"https://a.example", "http://a.example", false},
		{"https://a.example", "https://b.a.example", false},
		{"https://a.example", "https://a.example:8443", false},
		{"http://127.0.0.1:1", "http://localhost:1", false},
	}
	for _, c := range cases {
		if got := SameOrigin(mustParse(t, c.a), mustParse(t, c.b)); got != c.want {
			t.Errorf("SameOrigin(%q, %q) = %v", c.a, c.b, got)
		}
	}
}

// fakeRoundTripper records what would have gone on the wire.
type fakeRoundTripper struct {
	got []*http.Request
}

func (f *fakeRoundTripper) RoundTrip(req *http.Request) (*http.Response, error) {
	f.got = append(f.got, req)
	return &http.Response{StatusCode: 200, Body: io.NopCloser(strings.NewReader("{}")), Header: http.Header{}, Request: req}, nil
}

func TestGuardAttachesTheKeyToTheConfiguredOriginOnly(t *testing.T) {
	fake := &fakeRoundTripper{}
	client := newClientWithBase(Options{BaseURL: mustParse(t, "https://memoria.example.com"), Key: testKey}, fake)

	for raw, wantAuth := range map[string]bool{
		"https://memoria.example.com/v1/status":      true,
		"https://memoria.example.com:443/v1/status":  true,
		"https://evil.memoria.example.com/v1/status": false, // net/http would keep the header here.
		"http://memoria.example.com/v1/status":       false, // And here, on a downgrade.
		"https://memoria.example.com:8443/v1/status": false,
		"https://memoria.example.com.evil.test/x":    false,
	} {
		req, err := http.NewRequest(http.MethodGet, raw, nil)
		if err != nil {
			t.Fatal(err)
		}
		// A preset header must not survive either: the guard owns this header.
		req.Header.Set("Authorization", "Bearer preset")
		resp, err := client.Do(req)
		if err != nil {
			t.Fatal(err)
		}
		_ = resp.Body.Close()
		sent := fake.got[len(fake.got)-1].Header.Get("Authorization")
		if wantAuth && sent != "Bearer "+testKey {
			t.Errorf("%s: Authorization = %q", raw, sent)
		}
		if !wantAuth && sent != "" {
			t.Errorf("%s: Authorization should be absent, got %q", raw, sent)
		}
		if req.Header.Get("Authorization") != "Bearer preset" {
			t.Errorf("%s: the caller's request was mutated", raw)
		}
	}
}

// U+0130 lowercases to ASCII i in Go, while the network stack maps it through IDNA
// to a different xn-- name. A check using Unicode case folding sends the key there.
func TestNonASCIIHostsNeverMatchAnOrigin(t *testing.T) {
	base := mustParse(t, "https://memoria.example.test")
	for _, raw := range []string{
		"https://memor\u0130a.example.test/",
		"https://memor%C4%B0a.example.test/",
		"https://memor\u0131a.example.test/",
		"https://ex\u212Aample.test/",
	} {
		other, err := url.Parse(raw)
		if err != nil {
			t.Fatal(err)
		}
		if SameOrigin(base, other) || SameOrigin(other, base) {
			t.Errorf("%s matched the origin", raw)
		}
	}
	if Origin(mustParse(t, "https://memor\u0130a.example.test")) == Origin(base) {
		t.Error("Origin should not fold Unicode case")
	}
}

func TestNonASCIIRedirectIsRefusedAndGetsNoKey(t *testing.T) {
	fake := &fakeRoundTripper{}
	client := newClientWithBase(Options{BaseURL: mustParse(t, "https://memoria.example.test"), Key: testKey}, fake)
	req, err := http.NewRequest(http.MethodGet, "https://memor\u0130a.example.test/x", nil)
	if err != nil {
		t.Fatal(err)
	}
	resp, err := client.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	_ = resp.Body.Close()
	if got := fake.got[0].Header.Get("Authorization"); got != "" {
		t.Fatalf("Authorization = %q", got)
	}
}

func TestRedirectLocationWithNonASCIIHostIsRefused(t *testing.T) {
	var server *httptest.Server //nolint:staticcheck // the handler refers to server, so it is declared first
	server = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Location", "https://exam%C4%B0ple.test/x")
		w.WriteHeader(http.StatusFound)
	}))
	defer server.Close()
	client, err := NewClient(Options{BaseURL: mustParse(t, server.URL), Key: testKey})
	if err != nil {
		t.Fatal(err)
	}
	resp, err := client.Get(server.URL)
	if err == nil {
		_ = resp.Body.Close()
		t.Fatal("expected a refusal")
	}
	if got := apperr.ExitCode(apperr.FromTransport(err)); got != apperr.ExitInsecure {
		t.Fatalf("exit = %d: %v", got, err)
	}
}

func TestRedirectToAnotherOriginIsRefusedAndTheKeyNeverArrives(t *testing.T) {
	var otherHits atomic.Int32
	other := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		otherHits.Add(1)
		if r.Header.Get("Authorization") != "" {
			t.Error("the key reached another origin")
		}
	}))
	defer other.Close()

	origin := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, other.URL+"/stolen", http.StatusTemporaryRedirect)
	}))
	defer origin.Close()

	client, err := NewClient(Options{BaseURL: mustParse(t, origin.URL), Key: testKey})
	if err != nil {
		t.Fatal(err)
	}
	resp, err := client.Get(origin.URL + "/v1/status")
	if err == nil {
		_ = resp.Body.Close()
		t.Fatal("expected the redirect to be refused")
	}
	if got := apperr.ExitCode(apperr.FromTransport(err)); got != apperr.ExitInsecure {
		t.Errorf("exit = %d: %v", got, err)
	}
	if otherHits.Load() != 0 {
		t.Error("the redirect target should not have been contacted")
	}
}

func TestSameOriginRedirectKeepsTheKey(t *testing.T) {
	var final atomic.Value
	var server *httptest.Server //nolint:staticcheck // the handler refers to server, so it is declared first
	server = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/start" {
			http.Redirect(w, r, server.URL+"/end", http.StatusFound)
			return
		}
		final.Store(r.Header.Get("Authorization"))
	}))
	defer server.Close()

	client, err := NewClient(Options{BaseURL: mustParse(t, server.URL), Key: testKey})
	if err != nil {
		t.Fatal(err)
	}
	resp, err := client.Get(server.URL + "/start")
	if err != nil {
		t.Fatal(err)
	}
	_ = resp.Body.Close()
	if final.Load() != "Bearer "+testKey {
		t.Fatalf("Authorization = %v", final.Load())
	}
}

func TestRedirectLoopStops(t *testing.T) {
	var server *httptest.Server //nolint:staticcheck // the handler refers to server, so it is declared first
	server = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, server.URL+"/again", http.StatusFound)
	}))
	defer server.Close()
	client, err := NewClient(Options{BaseURL: mustParse(t, server.URL)})
	if err != nil {
		t.Fatal(err)
	}
	if resp, err := client.Get(server.URL); err == nil {
		_ = resp.Body.Close()
		t.Fatal("expected a redirect limit error")
	}
}

func TestPublicHTTPRefusedBeforeAnyConnection(t *testing.T) {
	_, err := NewClient(Options{BaseURL: mustParse(t, "http://8.8.8.8"), Key: testKey})
	if apperr.ExitCode(err) != apperr.ExitInsecure {
		t.Fatalf("err = %v", err)
	}
	if _, err := NewClient(Options{BaseURL: mustParse(t, "http://8.8.8.8"), Key: testKey, AllowInsecureHTTP: true}); err != nil {
		t.Fatalf("override: %v", err)
	}
}

func TestResponseBodyIsCapped(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write(bytes.Repeat([]byte("x"), 2048))
	}))
	defer server.Close()

	client, err := NewClient(Options{BaseURL: mustParse(t, server.URL), MaxResponseBytes: 1024})
	if err != nil {
		t.Fatal(err)
	}
	resp, err := client.Get(server.URL)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = resp.Body.Close() }()
	if _, err := io.ReadAll(resp.Body); !errors.Is(err, ErrResponseTooLarge) {
		t.Fatalf("err = %v", err)
	}
}

func TestResponseAtTheCapIsAllowed(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write(bytes.Repeat([]byte("x"), 1024))
	}))
	defer server.Close()

	client, err := NewClient(Options{BaseURL: mustParse(t, server.URL), MaxResponseBytes: 1024})
	if err != nil {
		t.Fatal(err)
	}
	resp, err := client.Get(server.URL)
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = resp.Body.Close() }()
	body, err := io.ReadAll(resp.Body)
	if err != nil || len(body) != 1024 {
		t.Fatalf("len = %d, err = %v", len(body), err)
	}
}

func TestVerboseNeverLogsHeaders(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("X-Trace-Id", "trace-123")
		w.Header().Set("X-Echo", r.Header.Get("Authorization"))
		w.WriteHeader(http.StatusTeapot)
	}))
	defer server.Close()

	var log bytes.Buffer
	client, err := NewClient(Options{BaseURL: mustParse(t, server.URL), Key: testKey, Verbose: &log, Timeout: 10 * time.Second})
	if err != nil {
		t.Fatal(err)
	}
	resp, err := client.Get(server.URL + "/v1/status")
	if err != nil {
		t.Fatal(err)
	}
	_ = resp.Body.Close()
	out := log.String()
	if strings.Contains(out, testKey) || strings.Contains(out, "Bearer") {
		t.Fatalf("verbose leaked: %s", out)
	}
	if !strings.Contains(out, "> GET /v1/status") || !strings.Contains(out, "< 418") || !strings.Contains(out, "trace-123") {
		t.Fatalf("verbose missing detail: %s", out)
	}
}
