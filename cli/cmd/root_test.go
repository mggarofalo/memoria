package cmd

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
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/auth"
	"github.com/mggarofalo/memoria/cli/internal/selfupdate"
	"github.com/mggarofalo/memoria/cli/internal/transport"
)

const testKey = "mem_0011223344556677_8899aabbccddeeff"

// memoryKeyring stands in for the OS keyring.
type memoryKeyring struct {
	values map[string]string
	err    error
}

func (m *memoryKeyring) Get(name string) (string, error) {
	if m.err != nil {
		return "", m.err
	}
	v, ok := m.values[name]
	if !ok {
		return "", auth.ErrNotFound
	}
	return v, nil
}

func (m *memoryKeyring) Set(name, value string) error {
	if m.err != nil {
		return m.err
	}
	m.values[name] = value
	return nil
}

func (m *memoryKeyring) Delete(name string) error {
	if m.err != nil {
		return m.err
	}
	if _, ok := m.values[name]; !ok {
		return auth.ErrNotFound
	}
	delete(m.values, name)
	return nil
}

// harness runs the real command tree with captured output.
type harness struct {
	t             *testing.T
	stdin         *strings.Reader
	stdout        bytes.Buffer
	stderr        bytes.Buffer
	env           map[string]string
	keyring       *memoryKeyring
	noRing        bool
	ringErr       error
	terminal      bool
	password      string
	updater       releaseUpdater
	updateOptions selfupdate.Options
	config        string
	slept         []time.Duration
	sleepErr      error
	checkOutput   string
	checkErr      error
	checkLists    *[][]string
	checkFiles    []string
	checkArgs     []string
}

func newHarness(t *testing.T) *harness {
	t.Helper()
	dir := t.TempDir()
	t.Setenv("XDG_CONFIG_HOME", dir)
	return &harness{
		t:       t,
		stdin:   strings.NewReader(""),
		env:     map[string]string{},
		keyring: &memoryKeyring{values: map[string]string{}},
		config:  filepath.Join(dir, "memoria"),
	}
}

func (h *harness) app() *app {
	a := newApp(h.stdin, &h.stdout, &h.stderr)
	a.getenv = func(name string) string { return h.env[name] }
	a.stdinIsTerminal = func() bool { return h.terminal }
	a.readPassword = func() ([]byte, error) { return []byte(h.password), nil }
	a.openKeyring = func() (auth.SecretStore, error) {
		if h.ringErr != nil {
			return nil, h.ringErr
		}
		if h.noRing {
			return nil, auth.ErrKeyringUnavailable
		}
		return h.keyring, nil
	}
	a.selfUpdate.newUpdater = func(o selfupdate.Options) releaseUpdater {
		h.updateOptions = o
		return h.updater
	}
	a.selfUpdate.executable = func() (string, error) { return "memoria-test-exe", nil }
	a.redactionCheck = h.fakeRedactionCheck
	a.sleep = func(_ context.Context, wait time.Duration) error {
		h.slept = append(h.slept, wait)
		return h.sleepErr
	}
	return a
}

// fakeRedactionCheck reads the lists of files it was given, while they still exist.
func (h *harness) fakeRedactionCheck(_ context.Context, args []string) ([]byte, error) {
	h.checkArgs = args
	for i, arg := range args {
		if arg != "--manifest" {
			continue
		}
		h.checkFiles = append(h.checkFiles, args[i+1])
		data, err := os.ReadFile(args[i+1])
		if err != nil {
			return nil, err
		}
		if h.checkLists != nil {
			*h.checkLists = append(*h.checkLists, strings.Split(string(data), "\n"))
		}
	}
	return []byte(h.checkOutput), h.checkErr
}

// run executes a command. Every test using it also asserts the key is absent
// from stdout, stderr and the config file.
func (h *harness) run(args ...string) int {
	h.t.Helper()
	h.stdout.Reset()
	h.stderr.Reset()
	code := h.app().run(context.Background(), args)
	h.assertNoKeyLeak()
	return code
}

func (h *harness) assertNoKeyLeak() {
	h.t.Helper()
	for name, text := range map[string]string{"stdout": h.stdout.String(), "stderr": h.stderr.String()} {
		if strings.Contains(text, testKey) || strings.Contains(text, "8899aabbccddeeff") {
			h.t.Fatalf("the key appeared on %s: %s", name, text)
		}
	}
	data, err := os.ReadFile(filepath.Join(h.config, "config.json"))
	if err == nil && strings.Contains(string(data), testKey) {
		h.t.Fatalf("the key appeared in config.json: %s", data)
	}
}

// login stores the key against a server, bypassing the login command.
func (h *harness) login(url string) {
	h.t.Helper()
	h.keyring.values[auth.KeyName(auth.DefaultProfile)] = h.entryFor(url)
	cfg, err := auth.LoadConfig()
	if err != nil {
		h.t.Fatal(err)
	}
	cfg.Profiles[auth.DefaultProfile] = auth.Profile{APIURL: url, Backend: auth.BackendKeyring}
	if err := cfg.Save(); err != nil {
		h.t.Fatal(err)
	}
}

func (h *harness) configContent() string {
	h.t.Helper()
	data, err := os.ReadFile(filepath.Join(h.config, "config.json"))
	if err != nil {
		h.t.Fatal(err)
	}
	return string(data)
}

// entryFor is what the store holds for the test key, bound to a server.
func (h *harness) entryFor(raw string) string {
	h.t.Helper()
	parsed, err := url.Parse(raw)
	if err != nil {
		h.t.Fatal(err)
	}
	entry, err := auth.EncodeStoredKey(transport.Origin(parsed), testKey)
	if err != nil {
		h.t.Fatal(err)
	}
	return entry
}

// storedKeyring reports whether the keyring holds the test key for a profile.
func (h *harness) storedKeyring(profile string) bool {
	return strings.Contains(h.keyring.values[auth.KeyName(profile)], testKey)
}

// writeConfigFile replaces the config's content, keeping its private permissions.
func (h *harness) writeConfigFile(content string) {
	h.t.Helper()
	cfg, err := auth.LoadConfig()
	if err != nil {
		h.t.Fatal(err)
	}
	if err := cfg.Save(); err != nil {
		h.t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(h.config, "config.json"), []byte(content), 0o600); err != nil {
		h.t.Fatal(err)
	}
}

// fakeAPI is a scripted Memoria server. It records requests.
type fakeAPI struct {
	*httptest.Server
	requests []*http.Request
	bodies   []string
	respond  func(w http.ResponseWriter, r *http.Request)
}

func newFakeAPI(t *testing.T, respond http.HandlerFunc) *fakeAPI {
	t.Helper()
	f := &fakeAPI{respond: respond}
	f.Server = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		body, _ := io.ReadAll(r.Body)
		f.requests = append(f.requests, r.Clone(context.Background()))
		f.bodies = append(f.bodies, string(body))
		f.respond(w, r)
	}))
	t.Cleanup(f.Close)
	return f
}

func jsonReply(body string) http.HandlerFunc {
	return func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = io.WriteString(w, body)
	}
}

// echoProblem replies with a problem document quoting the Authorization header,
// in plain and encoded forms, as a careless server's error page would.
func echoProblem(status int) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/problem+json")
		w.WriteHeader(status)
		_ = json.NewEncoder(w).Encode(map[string]any{
			"title":   "Failed for " + r.Header.Get("Authorization"),
			"detail":  "request headers: Authorization: " + r.Header.Get("Authorization"),
			"traceId": "trace-" + strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer "),
			"errors":  map[string][]string{"auth": {r.Header.Get("Authorization")}},
		})
	}
}

func TestRootCommandHasName(t *testing.T) {
	h := newHarness(t)
	if root := h.app().rootCmd(); root.Name() != "memoria" {
		t.Fatalf("root command name = %q", root.Name())
	}
}

func TestRootCommandDefinesGlobalFlags(t *testing.T) {
	h := newHarness(t)
	root := h.app().rootCmd()
	for _, name := range []string{"api-url", "api-key", "profile", "format", "verbose", "quiet", "allow-insecure-http", "timeout"} {
		if root.PersistentFlags().Lookup(name) == nil {
			t.Errorf("missing persistent flag %q", name)
		}
	}
}

func TestCommandTree(t *testing.T) {
	h := newHarness(t)
	root := h.app().rootCmd()
	for _, path := range [][]string{
		{"auth", "login"}, {"auth", "logout"}, {"auth", "status"},
		{"keys", "create"}, {"keys", "list"}, {"keys", "rotate"}, {"keys", "revoke"},
		{"query"}, {"get"}, {"multi-get"}, {"collections"}, {"status"}, {"mcp"}, {"self-update"}, {"version"},
	} {
		if found, _, err := root.Find(path); err != nil || found.Name() != path[len(path)-1] {
			t.Errorf("command %v: %v", path, err)
		}
	}
}

func TestVersionCommandAndFlag(t *testing.T) {
	h := newHarness(t)
	if code := h.run("version"); code != 0 || strings.TrimSpace(h.stdout.String()) != "memoria "+Version {
		t.Fatalf("code = %d, stdout = %q", code, h.stdout.String())
	}
	if code := h.run("--version"); code != 0 || !strings.Contains(h.stdout.String(), Version) {
		t.Fatalf("code = %d, stdout = %q", code, h.stdout.String())
	}
}

func TestUsageErrorsExitTwo(t *testing.T) {
	cases := [][]string{
		{"no-such-command"},
		{"query"},
		{"get"},
		{"get", "a", "b"},
		{"multi-get"},
		{"collections", "extra"},
		{"status", "extra"},
		{"query", "x", "--limit", "abc"},
		{"query", "x", "--no-such-flag"},
		{"query", "x", "--format", "yaml"},
		{"auth", "extra"},
		{"self-update", "--version", "1.2.3"},
		{"--profile", "../evil", "status"},
	}
	for _, args := range cases {
		t.Run(strings.Join(args, " "), func(t *testing.T) {
			h := newHarness(t)
			h.env[auth.EnvURL] = "https://memoria.example.com"
			h.env[auth.EnvAPIKey] = testKey
			if code := h.run(args...); code != apperr.ExitUsage {
				t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
			}
			if !strings.HasPrefix(h.stderr.String(), "error: ") {
				t.Fatalf("stderr = %q", h.stderr.String())
			}
		})
	}
}

func TestMissingURLIsAUsageError(t *testing.T) {
	h := newHarness(t)
	h.env[auth.EnvAPIKey] = testKey
	if code := h.run("status"); code != apperr.ExitUsage || !strings.Contains(h.stderr.String(), "no server URL") {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
}

func TestKeyOnTheCommandLineWarnsAndNeverEchoes(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	code := h.run("collections", "--api-url", api.URL, "--api-key", testKey)
	if code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if !strings.Contains(h.stderr.String(), "warning: --api-key") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	if got := api.requests[0].Header.Get("Authorization"); got != "Bearer "+testKey {
		t.Fatalf("Authorization = %q", got)
	}
}

func TestParseErrorsNeverQuoteTheKey(t *testing.T) {
	for name, args := range map[string][]string{
		"unknown command, key shape":  {testKey},
		"as the value of a bad flag":  {"query", "x", "--limit", testKey},
		"positional to a no-arg cmd":  {"auth", "login", testKey},
		"positional, not key-shaped":  {"auth", "login", "--api-key", "opaque-secret-value", "opaque-secret-value"},
		"with --api-key=":             {"--api-key=opaque-secret-value", "--limit", "opaque-secret-value"},
		"status positional":           {"status", testKey},
		"mcp positional":              {"mcp", testKey},
		"self-update positional":      {"self-update", testKey},
		"self-update as bad version":  {"self-update", "--version", testKey},
		"collections positional":      {"collections", testKey},
		"auth logout positional":      {"auth", "logout", testKey},
		"auth status positional":      {"auth", "status", testKey},
		"version positional":          {"version", testKey},
		"auth group positional":       {"auth", testKey},
		"key inside a server url":     {"status", "--api-url", "https://user:" + testKey + "@host"},
		"key inside a url query":      {"status", "--api-url", "https://host?k=" + testKey},
		"key inside a malformed url":  {"status", "--api-url", "https://host/%zz" + testKey},
		"key as format":               {"status", "--format", testKey},
		"key as profile":              {"status", "--profile", testKey + "/x"},
		"key as backend":              {"auth", "login", "--backend", "opaque-secret-value"},
		"opaque key as format":        {"status", "--format", "opaque-secret-value"},
		"opaque key as timeout":       {"status", "--timeout", "opaque-secret-value"},
		"opaque key as limit":         {"query", "x", "--limit", "opaque-secret-value"},
		"opaque key as a command":     {"opaque-secret-value"},
		"single dash form":            {"-api-key=opaque-secret-value", "status"},
		"single dash, separate value": {"-api-key", "opaque-secret-value", "--limit", "opaque-secret-value"},
		"unknown flag with a value":   {"status", "--apikey=opaque-secret-value"},
	} {
		t.Run(name, func(t *testing.T) {
			h := newHarness(t)
			if code := h.run(args...); code == 0 {
				t.Fatalf("expected a failure, stdout = %q", h.stdout.String())
			}
			if strings.Contains(h.stderr.String(), "opaque-secret-value") {
				t.Fatalf("stderr = %q", h.stderr.String())
			}
		})
	}
}

func TestHelpShowsNoKey(t *testing.T) {
	h := newHarness(t)
	if code := h.run("--api-key", testKey, "query", "--help"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if !strings.Contains(h.stdout.String(), "--collection") {
		t.Fatalf("stdout = %q", h.stdout.String())
	}
}

func TestExitCodes(t *testing.T) {
	cases := []struct {
		name   string
		status int
		want   int
	}{
		{"unauthorized", 401, apperr.ExitAuth},
		{"forbidden", 403, apperr.ExitAuth},
		{"not found", 404, apperr.ExitNotFound},
		{"rate limited", 429, apperr.ExitRateLimited},
		{"bad request", 400, apperr.ExitUsage},
		{"server error", 500, apperr.ExitGeneric},
		{"bad gateway", 502, apperr.ExitUnavailable},
		{"unavailable", 503, apperr.ExitUnavailable},
		{"gateway timeout", 504, apperr.ExitUnavailable},
	}
	commands := [][]string{
		{"query", "x"},
		{"get", "a.md"},
		{"multi-get", "a.md"},
		{"collections"},
		{"status"},
	}
	for _, c := range cases {
		for _, args := range commands {
			t.Run(c.name+"/"+args[0], func(t *testing.T) {
				api := newFakeAPI(t, echoProblem(c.status))
				h := newHarness(t)
				h.login(api.URL)
				if code := h.run(args...); code != c.want {
					t.Fatalf("code = %d, want %d, stderr = %q", code, c.want, h.stderr.String())
				}
			})
		}
	}
}

func TestEveryCommandScrubsAnEchoedHeader(t *testing.T) {
	commands := [][]string{
		{"query", "x"},
		{"get", "a.md"},
		{"multi-get", "a.md"},
		{"collections"},
		{"status"},
		{"auth", "status"},
	}
	for _, status := range []int{401, 403, 429, 500, 503} {
		for _, args := range commands {
			for _, extra := range [][]string{nil, {"--verbose"}, {"--format", "json"}} {
				name := fmt.Sprintf("%d/%s/%v", status, strings.Join(args, "_"), extra)
				t.Run(name, func(t *testing.T) {
					api := newFakeAPI(t, echoProblem(status))
					h := newHarness(t)
					h.login(api.URL)
					code := h.run(append(args, extra...)...)
					if code == 0 && status != 403 {
						t.Fatalf("expected failure, stdout = %q", h.stdout.String())
					}
					if strings.Contains(h.stderr.String(), "Bearer mem_") {
						t.Fatalf("stderr = %q", h.stderr.String())
					}
				})
			}
		}
	}
}

func TestOtherEncodingsOfTheKeyAreScrubbed(t *testing.T) {
	api := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/problem+json")
		w.WriteHeader(500)
		key := strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer ")
		_, _ = fmt.Fprintf(w, `{"title":"T","detail":"%s %s"}`, key, "bWVtXzAwMTEyMjMzNDQ1NTY2NzdfODg5OWFhYmJjY2RkZWVmZg==")
	})
	h := newHarness(t)
	h.login(api.URL)
	h.run("status")
	if strings.Contains(h.stderr.String(), "bWVtXzAwMTEy") {
		t.Fatalf("base64 form survived: %q", h.stderr.String())
	}
}

func TestRateLimitReportsRetryAfter(t *testing.T) {
	api := newFakeAPI(t, func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Retry-After", "30")
		w.Header().Set("Content-Type", "application/problem+json")
		w.WriteHeader(429)
		_, _ = io.WriteString(w, `{"title":"Too many requests"}`)
	})
	h := newHarness(t)
	h.login(api.URL)
	if code := h.run("status"); code != apperr.ExitRateLimited {
		t.Fatalf("code = %d", code)
	}
	if !strings.Contains(h.stderr.String(), "retry after 30s") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
}

func TestServerDownIsUnavailable(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{}`))
	url := api.URL
	api.Close()
	h := newHarness(t)
	h.login(url)
	if code := h.run("status"); code != apperr.ExitUnavailable {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
}

func TestMalformedSuccessResponses(t *testing.T) {
	for _, args := range [][]string{{"query", "x"}, {"get", "a"}, {"multi-get", "a"}, {"collections"}, {"status"}} {
		t.Run(args[0]+"/invalid json", func(t *testing.T) {
			api := newFakeAPI(t, jsonReply(`{not json`))
			h := newHarness(t)
			h.login(api.URL)
			if code := h.run(args...); code != apperr.ExitGeneric {
				t.Fatalf("code = %d", code)
			}
		})
		t.Run(args[0]+"/wrong content type", func(t *testing.T) {
			api := newFakeAPI(t, func(w http.ResponseWriter, _ *http.Request) {
				w.Header().Set("Content-Type", "text/plain")
				_, _ = io.WriteString(w, "hello")
			})
			h := newHarness(t)
			h.login(api.URL)
			if code := h.run(args...); code != apperr.ExitGeneric || !strings.Contains(h.stderr.String(), "cannot read") {
				t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
			}
		})
	}
}

func TestPublicPlainHTTPIsRefusedWithoutContact(t *testing.T) {
	h := newHarness(t)
	h.env[auth.EnvAPIKey] = testKey
	code := h.run("status", "--api-url", "http://8.8.8.8:9")
	if code != apperr.ExitInsecure || !strings.Contains(h.stderr.String(), "--allow-insecure-http") {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
}

func TestPrivatePlainHTTPWarns(t *testing.T) {
	h := newHarness(t)
	h.env[auth.EnvAPIKey] = testKey
	// 192.0.2.x would be public; 10.255.255.1 stays unrouted and the short
	// timeout ends the attempt. What matters is the warning before it.
	h.run("status", "--api-url", "http://10.255.255.1:9", "--timeout", "50ms")
	if !strings.Contains(h.stderr.String(), "private address") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
}

func TestInsecureOverride(t *testing.T) {
	for name, set := range map[string]func(*harness) []string{
		"flag": func(*harness) []string { return []string{"--allow-insecure-http"} },
		"env": func(h *harness) []string {
			h.env[auth.EnvAllowInsecureHTTP] = "1"
			return nil
		},
	} {
		t.Run(name, func(t *testing.T) {
			h := newHarness(t)
			h.env[auth.EnvAPIKey] = testKey
			args := append([]string{"status", "--api-url", "http://203.0.113.1:9", "--timeout", "50ms"}, set(h)...)
			code := h.run(args...)
			if code == apperr.ExitInsecure {
				t.Fatalf("the override should allow the attempt: %q", h.stderr.String())
			}
			if !strings.Contains(h.stderr.String(), "explicitly allowed") {
				t.Fatalf("stderr = %q", h.stderr.String())
			}
		})
	}
}

func TestLoopbackHTTPIsQuiet(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.login(api.URL)
	if code := h.run("collections"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if strings.Contains(h.stderr.String(), "warning") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
}

func TestCrossOriginRedirectIsRefused(t *testing.T) {
	other := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	api := newFakeAPI(t, func(w http.ResponseWriter, r *http.Request) {
		http.Redirect(w, r, other.URL+r.URL.Path, http.StatusTemporaryRedirect)
	})
	h := newHarness(t)
	h.login(api.URL)
	if code := h.run("collections"); code != apperr.ExitInsecure {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if len(other.requests) != 0 {
		t.Fatal("the redirect target was contacted")
	}
}

func TestStoredKeyIsNotSentToAnotherServer(t *testing.T) {
	saved := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	other := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	for name, set := range map[string]func(*harness) []string{
		"flag": func(*harness) []string { return []string{"--api-url", other.URL} },
		"env": func(h *harness) []string {
			h.env[auth.EnvURL] = other.URL
			return nil
		},
	} {
		t.Run(name, func(t *testing.T) {
			h := newHarness(t)
			h.login(saved.URL)
			code := h.run(append([]string{"collections"}, set(h)...)...)
			if code != apperr.ExitInsecure {
				t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
			}
			if len(other.requests) != 0 {
				t.Fatal("the other server was contacted with the stored key")
			}
		})
	}
	t.Run("an explicit key may go anywhere", func(t *testing.T) {
		h := newHarness(t)
		h.login(saved.URL)
		h.env[auth.EnvAPIKey] = testKey
		if code := h.run("collections", "--api-url", other.URL); code != 0 {
			t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
		}
	})
}

func TestEnvKeyWarnsOnlyAtATerminal(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.env[auth.EnvAPIKey] = testKey
	h.env[auth.EnvURL] = api.URL
	h.run("collections")
	if strings.Contains(h.stderr.String(), "warning") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	h.terminal = true
	h.run("collections")
	if !strings.Contains(h.stderr.String(), auth.EnvAPIKey) {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	h.run("collections", "--quiet")
	if strings.Contains(h.stderr.String(), "warning") {
		t.Fatalf("--quiet stderr = %q", h.stderr.String())
	}
}

func TestStraySecretInConfigWarns(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.login(api.URL)
	path := filepath.Join(h.config, "config.json")
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	withSecret := strings.Replace(string(data), `"active_profile"`, `"api_key": "not-a-real-key-value", "active_profile"`, 1)
	if err := os.WriteFile(path, []byte(withSecret), 0o600); err != nil {
		t.Fatal(err)
	}
	h.run("collections")
	if !strings.Contains(h.stderr.String(), "looks like a key") || strings.Contains(h.stderr.String(), "not-a-real-key-value") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
}

func TestCorruptConfigFailsEveryCommandCleanly(t *testing.T) {
	for _, args := range [][]string{{"status"}, {"auth", "login"}, {"auth", "logout"}, {"auth", "status"}} {
		h := newHarness(t)
		h.env[auth.EnvAPIKey] = testKey
		h.writeConfigFile("{oops")
		if code := h.run(args...); code != apperr.ExitGeneric {
			t.Fatalf("%v: code = %d, stderr = %q", args, code, h.stderr.String())
		}
	}
}

func TestCobraErrorsAreClassifiedWithoutQuoting(t *testing.T) {
	err := classifyCobraError(errors.New(`unknown command "opaque-value" for "memoria"`))
	if apperr.ExitCode(err) != apperr.ExitUsage || strings.Contains(err.Error(), "opaque-value") {
		t.Errorf("err = %v", err)
	}
	if got := apperr.ExitCode(classifyCobraError(errors.New("other"))); got != apperr.ExitGeneric {
		t.Errorf("got %d", got)
	}
}

func TestNewAppDefaults(t *testing.T) {
	var out, errw bytes.Buffer
	a := newApp(strings.NewReader(""), &out, &errw)
	if a.getenv == nil || a.stdinIsTerminal == nil || a.readPassword == nil || a.openKeyring == nil {
		t.Fatal("defaults missing")
	}
	if a.stdinIsTerminal() && os.Getenv("CI") != "" {
		t.Log("stdin is a terminal")
	}
	if a.selfUpdate.newUpdater(selfupdate.Options{}) == nil {
		t.Fatal("expected an updater")
	}
}
