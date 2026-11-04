package cmd

import (
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/auth"
)

const credentialFile = "credentials.json"

func (h *harness) credentialFile() string { return filepath.Join(h.config, credentialFile) }

func (h *harness) savedConfig() *auth.Config {
	h.t.Helper()
	cfg, err := auth.LoadConfig()
	if err != nil {
		h.t.Fatal(err)
	}
	return cfg
}

func TestLoginFromStdinStoresInTheKeyring(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.stdin = strings.NewReader(testKey + "\n")
	if code := h.run("auth", "login", "--api-url", api.URL+"/"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if !h.storedKeyring("default") {
		t.Fatal("the key was not stored")
	}
	if got := api.requests[0].Header.Get("Authorization"); got != "Bearer "+testKey {
		t.Fatalf("Authorization = %q", got)
	}
	profile := h.savedConfig().Profile("default")
	if profile.APIURL != api.URL || profile.Backend != auth.BackendKeyring {
		t.Fatalf("profile = %+v", profile)
	}
	if h.stdout.Len() != 0 {
		t.Fatalf("stdout = %q", h.stdout.String())
	}
	if _, err := os.Stat(h.credentialFile()); !errors.Is(err, os.ErrNotExist) {
		t.Fatal("the keyring login should not write the credentials file")
	}
}

func TestLoginPromptsWithEchoOffAtATerminal(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.terminal = true
	h.password = testKey
	// At a terminal the URL prompt reads stdin; the key does not.
	h.stdin = strings.NewReader(api.URL + "\n")
	if code := h.run("auth", "login"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if !strings.Contains(h.stderr.String(), "input hidden") || !strings.Contains(h.stderr.String(), "server URL") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	if !h.storedKeyring("default") {
		t.Fatal("the key was not stored")
	}
}

func TestLoginWithFlagWarns(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	if code := h.run("auth", "login", "--api-url", api.URL, "--api-key", testKey); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if !strings.Contains(h.stderr.String(), "shell history") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
}

func TestLoginRejectedKeyIsNotStored(t *testing.T) {
	for _, status := range []int{401, 503, 429} {
		api := newFakeAPI(t, echoProblem(status))
		h := newHarness(t)
		h.stdin = strings.NewReader(testKey)
		if code := h.run("auth", "login", "--api-url", api.URL); code == 0 {
			t.Fatalf("%d: expected failure", status)
		}
		if len(h.keyring.values) != 0 {
			t.Fatalf("%d: a key the server did not accept was stored", status)
		}
		if _, err := os.Stat(filepath.Join(h.config, "config.json")); !errors.Is(err, os.ErrNotExist) {
			t.Fatalf("%d: config should not be written", status)
		}
	}
}

func TestLoginKeepsAKeyWithoutReadScope(t *testing.T) {
	api := newFakeAPI(t, echoProblem(http.StatusForbidden))
	h := newHarness(t)
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--api-url", api.URL); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if !strings.Contains(h.stderr.String(), "no read access") || !h.storedKeyring("default") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
}

func TestLoginNoVerifyDoesNotContactTheServer(t *testing.T) {
	api := newFakeAPI(t, echoProblem(401))
	h := newHarness(t)
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--api-url", api.URL, "--no-verify"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if len(api.requests) != 0 {
		t.Fatal("the server was contacted")
	}
}

func TestLoginInputProblems(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	cases := map[string]string{
		"empty stdin":     "",
		"only whitespace": "  \n",
		"too long":        strings.Repeat("x", maxStdinKeyBytes+1),
		"spaces in value": "two words here",
		"too short":       "abc12",
	}
	for name, input := range cases {
		t.Run(name, func(t *testing.T) {
			h := newHarness(t)
			h.stdin = strings.NewReader(input)
			if code := h.run("auth", "login", "--api-url", api.URL); code != apperr.ExitUsage {
				t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
			}
			if input != "" && strings.TrimSpace(input) != "" && strings.Contains(h.stderr.String(), input) {
				t.Fatalf("the input was echoed: %q", h.stderr.String())
			}
		})
	}
}

func TestLoginUsesOnlyTheFirstLineOfStdin(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.stdin = strings.NewReader(testKey + "\nsomething else\n")
	if code := h.run("auth", "login", "--api-url", api.URL); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if !h.storedKeyring("default") {
		t.Fatal("wrong value stored")
	}
}

func TestLoginWithoutAURL(t *testing.T) {
	h := newHarness(t)
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login"); code != apperr.ExitUsage {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
}

func TestLoginRefusesAPublicPlainHTTPServerBeforeReadingTheKey(t *testing.T) {
	h := newHarness(t)
	input := strings.NewReader(testKey)
	h.stdin = input
	if code := h.run("auth", "login", "--api-url", "http://8.8.8.8"); code != apperr.ExitInsecure {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if input.Len() != len(testKey) {
		t.Fatal("the key was read before the URL was checked")
	}
}

func TestLoginUnknownBackend(t *testing.T) {
	h := newHarness(t)
	if code := h.run("auth", "login", "--backend", "nope", "--api-url", "https://x.example"); code != apperr.ExitUsage {
		t.Fatalf("code = %d", code)
	}
}

func TestLoginWithoutAKeyringDoesNotFallBackSilently(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.noRing = true
	input := strings.NewReader(testKey)
	h.stdin = input
	code := h.run("auth", "login", "--api-url", api.URL)
	if code != apperr.ExitGeneric || !strings.Contains(h.stderr.String(), "--backend file") {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if _, err := os.Stat(h.credentialFile()); !errors.Is(err, os.ErrNotExist) {
		t.Fatal("nothing should have been written")
	}
	if input.Len() != len(testKey) {
		t.Fatal("the key should not be read when it cannot be stored")
	}
}

func TestFileBackendLoginWarnsAndWorks(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.noRing = true
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--api-url", api.URL, "--backend", "file"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if !strings.Contains(h.stderr.String(), "unencrypted") || !strings.Contains(h.stderr.String(), "weaker") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	data, err := os.ReadFile(h.credentialFile())
	if err != nil || !strings.Contains(string(data), testKey) {
		t.Fatalf("credentials file: %v", err)
	}
	if h.savedConfig().Profile("default").Backend != auth.BackendFile {
		t.Fatal("the profile should remember its backend")
	}

	if code := h.run("collections"); code != 0 {
		t.Fatalf("using the stored key: code = %d, stderr = %q", code, h.stderr.String())
	}
	if got := api.requests[len(api.requests)-1].Header.Get("Authorization"); got != "Bearer "+testKey {
		t.Fatalf("Authorization = %q", got)
	}
}

func TestLoginRemovesTheCopyInTheOtherBackend(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--api-url", api.URL, "--backend", "file"); code != 0 {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--api-url", api.URL); code != 0 {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	if _, err := os.Stat(h.credentialFile()); !errors.Is(err, os.ErrNotExist) {
		t.Fatal("the plaintext copy should be gone")
	}
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--api-url", api.URL, "--backend", "file"); code != 0 {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	if h.storedKeyring(auth.DefaultProfile) {
		t.Fatal("the keyring copy should be gone")
	}
}

func TestLoginStoreFailureIsReported(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.keyring.err = errors.New("keyring is locked")
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--api-url", api.URL); code != apperr.ExitGeneric || !strings.Contains(h.stderr.String(), "locked") {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
}

func TestLoginWarnsWhenTheEnvironmentWillOverride(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.env[auth.EnvAPIKey] = "mem_other00000000000_0000000000000000"
	h.stdin = strings.NewReader(testKey)
	h.run("auth", "login", "--api-url", api.URL)
	if !strings.Contains(h.stderr.String(), "takes precedence") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
}

func TestLoginNamedProfile(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.env[auth.EnvProfile] = "work"
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--api-url", api.URL); code != 0 {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	if !h.storedKeyring("work") || h.savedConfig().ActiveProfile != "work" {
		t.Fatal("the named profile was not used")
	}
}

func TestLogoutRemovesFromBothBackends(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--api-url", api.URL, "--backend", "file"); code != 0 {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	h.keyring.values[auth.KeyName("default")] = h.entryFor(api.URL)

	if code := h.run("auth", "logout"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if len(h.keyring.values) != 0 {
		t.Fatal("the keyring still holds the key")
	}
	if _, err := os.Stat(h.credentialFile()); !errors.Is(err, os.ErrNotExist) {
		t.Fatal("the credentials file still exists")
	}
	if !strings.Contains(h.stderr.String(), "stays valid on the server") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
}

func TestLogoutEdgeCases(t *testing.T) {
	t.Run("nothing stored", func(t *testing.T) {
		h := newHarness(t)
		if code := h.run("auth", "logout"); code != 0 || !strings.Contains(h.stderr.String(), "No key removed") {
			t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
		}
	})
	t.Run("no keyring on this machine", func(t *testing.T) {
		h := newHarness(t)
		h.noRing = true
		if code := h.run("auth", "logout"); code != 0 {
			t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
		}
	})
	t.Run("keyring failure is reported", func(t *testing.T) {
		h := newHarness(t)
		h.keyring.err = errors.New("keyring is locked")
		if code := h.run("auth", "logout"); code != apperr.ExitGeneric || !strings.Contains(h.stderr.String(), "locked") {
			t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
		}
	})
	t.Run("environment key remains", func(t *testing.T) {
		h := newHarness(t)
		h.env[auth.EnvAPIKey] = testKey
		h.run("auth", "logout")
		if !strings.Contains(h.stderr.String(), "still set") {
			t.Fatalf("stderr = %q", h.stderr.String())
		}
	})
	t.Run("bad profile", func(t *testing.T) {
		h := newHarness(t)
		if code := h.run("auth", "logout", "--profile", "../x"); code != apperr.ExitUsage {
			t.Fatalf("code = %d", code)
		}
	})
}

func TestStatusReportsSourceAndNoPartOfTheKey(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.login(api.URL)
	if code := h.run("auth", "status"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	out := h.stdout.String()
	for _, want := range []string{"profile: default", "from keyring", "accepted by server: true", api.URL} {
		if !strings.Contains(out, want) {
			t.Errorf("missing %q in %q", want, out)
		}
	}
	for _, part := range []string{"mem_", "0011", "eeff"} {
		if strings.Contains(out, part) {
			t.Errorf("status shows part of the key (%q): %q", part, out)
		}
	}
}

func TestStatusJSON(t *testing.T) {
	api := newFakeAPI(t, echoProblem(401))
	h := newHarness(t)
	h.login(api.URL)
	code := h.run("auth", "status", "--format", "json")
	if code != apperr.ExitAuth {
		t.Fatalf("code = %d", code)
	}
	for _, want := range []string{`"authenticated":false`, `"source":"keyring"`, `"verified":false`} {
		if !strings.Contains(h.stdout.String(), want) {
			t.Errorf("missing %q in %q", want, h.stdout.String())
		}
	}
}

func TestStatusOfflineDoesNotContactTheServer(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{}`))
	h := newHarness(t)
	h.login(api.URL)
	if code := h.run("auth", "status", "--offline"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if len(api.requests) != 0 {
		t.Fatal("the server was contacted")
	}
	if strings.Contains(h.stdout.String(), "accepted by server") {
		t.Fatalf("stdout = %q", h.stdout.String())
	}
}

func TestStatusWithoutCredentialsExitsAuth(t *testing.T) {
	h := newHarness(t)
	h.env[auth.EnvURL] = "https://memoria.example.com"
	if code := h.run("auth", "status"); code != apperr.ExitAuth || !strings.Contains(h.stderr.String(), "auth login") {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
}

func TestStatusWarnsAboutTheFileBackend(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--api-url", api.URL, "--backend", "file"); code != 0 {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	h.run("auth", "status")
	if !strings.Contains(h.stdout.String(), "from file") || !strings.Contains(h.stderr.String(), "unencrypted") {
		t.Fatalf("stdout = %q, stderr = %q", h.stdout.String(), h.stderr.String())
	}
}

func TestVerboseNeverShowsTheKey(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.login(api.URL)
	if code := h.run("collections", "--verbose"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if !strings.Contains(h.stderr.String(), "> GET /v1/collections") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	if strings.Contains(h.stderr.String(), "Authorization") || strings.Contains(h.stderr.String(), "Bearer") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
}

// The review's proof of concept: a config directory the attacker chose names their
// server. The keyring is looked up by profile name, so it still finds the real key.
func TestPoisonedConfigCannotRedirectTheKeyringKey(t *testing.T) {
	real := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	evil := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.login(real.URL)

	poisoned := t.TempDir()
	t.Setenv("XDG_CONFIG_HOME", poisoned)
	cfg, err := auth.LoadConfig()
	if err != nil {
		t.Fatal(err)
	}
	cfg.Profiles[auth.DefaultProfile] = auth.Profile{APIURL: evil.URL, Backend: auth.BackendKeyring}
	if err := cfg.Save(); err != nil {
		t.Fatal(err)
	}

	for _, args := range [][]string{{"collections"}, {"status"}, {"query", "x"}, {"get", "a"}, {"multi-get", "a"}, {"auth", "status"}} {
		if code := h.run(args...); code != apperr.ExitInsecure {
			t.Fatalf("%v: code = %d, stderr = %q", args, code, h.stderr.String())
		}
	}
	h.stdin = strings.NewReader(`{"jsonrpc":"2.0","id":1,"method":"ping"}` + "\n")
	if code := h.run("mcp"); code != 0 || !strings.Contains(h.stdout.String(), `"kind":"insecure"`) {
		t.Fatalf("mcp: code = %d, stdout = %q", code, h.stdout.String())
	}
	if len(evil.requests) != 0 {
		t.Fatalf("the attacker's server received %d requests", len(evil.requests))
	}
}

// Editing the stored entry's origin is out of reach of a poisoned config, but the
// check must hold whichever store the key came from.
func TestFileBackendKeyIsBoundToo(t *testing.T) {
	real := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	evil := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--api-url", real.URL, "--backend", "file"); code != 0 {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	cfg := h.savedConfig()
	cfg.Profiles[auth.DefaultProfile] = auth.Profile{APIURL: evil.URL, Backend: auth.BackendFile}
	if err := cfg.Save(); err != nil {
		t.Fatal(err)
	}
	if code := h.run("collections"); code != apperr.ExitInsecure {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if len(evil.requests) != 0 {
		t.Fatal("the key reached another server")
	}
}

func TestLoginIgnoresTheEnvironmentURL(t *testing.T) {
	evil := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.env[auth.EnvURL] = evil.URL
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login"); code != apperr.ExitUsage {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if len(evil.requests) != 0 || len(h.keyring.values) != 0 {
		t.Fatal("a key went to the URL in the environment")
	}
	if !strings.Contains(h.stderr.String(), "ignored") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
}

func TestPoisonedEnvironmentCannotRebindAProfile(t *testing.T) {
	real := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	evil := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.login(real.URL)
	h.env[auth.EnvURL] = evil.URL
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if len(evil.requests) != 0 {
		t.Fatal("the key went to the URL in the environment")
	}
	if h.savedConfig().Profile("default").APIURL != real.URL {
		t.Fatal("the profile was rebound")
	}
}

func TestPoisonedConfigCannotCaptureALogin(t *testing.T) {
	real := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	evil := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.login(real.URL)
	cfg := h.savedConfig()
	cfg.Profiles[auth.DefaultProfile] = auth.Profile{APIURL: evil.URL, Backend: auth.BackendKeyring}
	if err := cfg.Save(); err != nil {
		t.Fatal(err)
	}
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login"); code != apperr.ExitInsecure {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if len(evil.requests) != 0 {
		t.Fatal("a pasted key went to the server in the config")
	}
}

func TestExplicitURLCanRebindAProfile(t *testing.T) {
	first := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	second := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.login(first.URL)
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--api-url", second.URL); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if code := h.run("collections"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if len(second.requests) != 2 {
		t.Fatalf("requests = %d", len(second.requests))
	}
}

func TestLoginShowsTheServerBeforeAskingForTheKey(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.terminal = true
	h.password = testKey
	if code := h.run("auth", "login", "--api-url", api.URL); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	text := h.stderr.String()
	server, prompt := strings.Index(text, "Logging in to http://127.0.0.1"), strings.Index(text, "input hidden")
	if server < 0 || prompt < 0 || server > prompt {
		t.Fatalf("stderr = %q", text)
	}
}

// poisonConfig points the config at a fresh directory naming the attacker's server.
func (h *harness) poisonConfig(url, backend string) {
	h.t.Helper()
	h.t.Setenv("XDG_CONFIG_HOME", h.t.TempDir())
	cfg, err := auth.LoadConfig()
	if err != nil {
		h.t.Fatal(err)
	}
	cfg.Profiles[auth.DefaultProfile] = auth.Profile{APIURL: url, Backend: backend}
	if err := cfg.Save(); err != nil {
		h.t.Fatal(err)
	}
}

// The review's proof of concept. The piped key went to the attacker and the real
// keyring entry was deleted, because only the chosen store was compared.
func TestPoisonedFileLoginCannotTakeTheKeyOrDeleteTheKeyring(t *testing.T) {
	real := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	evil := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.login(real.URL)
	want := h.keyring.values[auth.KeyName(auth.DefaultProfile)]

	h.poisonConfig(evil.URL, auth.BackendFile)
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--backend", "file"); code != apperr.ExitInsecure {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if len(evil.requests) != 0 {
		t.Fatal("the attacker received the key")
	}
	if h.keyring.values[auth.KeyName(auth.DefaultProfile)] != want {
		t.Fatal("the real keyring entry was changed")
	}
}

func TestPoisonedLoginWithNoEntryAnywhereIsRefused(t *testing.T) {
	evil := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	for _, backend := range []string{auth.BackendKeyring, auth.BackendFile} {
		t.Run(backend, func(t *testing.T) {
			h := newHarness(t)
			h.poisonConfig(evil.URL, backend)
			h.stdin = strings.NewReader(testKey)
			if code := h.run("auth", "login", "--backend", backend); code != apperr.ExitInsecure {
				t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
			}
			if len(evil.requests) != 0 {
				t.Fatal("the attacker received the key")
			}
			if !strings.Contains(h.stderr.String(), "--api-url") {
				t.Fatalf("stderr = %q", h.stderr.String())
			}
		})
	}
}

// The credentials file sits in the attacker's directory, so it cannot vouch.
func TestPoisonedCredentialsFileCannotVouchForTheConfig(t *testing.T) {
	evil := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.poisonConfig(evil.URL, auth.BackendFile)
	file, err := auth.NewFileStore()
	if err != nil {
		t.Fatal(err)
	}
	if err := file.Set(auth.KeyName(auth.DefaultProfile), h.entryFor(evil.URL)); err != nil {
		t.Fatal(err)
	}
	evil.requests = nil
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--backend", "file"); code != apperr.ExitInsecure {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if len(evil.requests) != 0 {
		t.Fatal("the attacker received the key")
	}
}

func TestConfigURLIsUsedWhenTheKeyringConfirmsIt(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.login(api.URL)
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if len(api.requests) != 1 {
		t.Fatalf("requests = %d", len(api.requests))
	}
}

func TestSavedURLWithNoKeyringIsRefused(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.noRing = true
	h.poisonConfig(api.URL, auth.BackendFile)
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--backend", "file"); code != apperr.ExitInsecure {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
}

func TestLoginLeavesAnEntryForAnotherServerAlone(t *testing.T) {
	first := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	second := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--api-url", first.URL, "--backend", "file"); code != 0 {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--api-url", second.URL); code != 0 {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	if _, err := os.Stat(h.credentialFile()); err != nil {
		t.Fatalf("the file entry for the other server should stay: %v", err)
	}
}

func TestFileLoginBesideAKeyringKeyForAnotherServerIsRefused(t *testing.T) {
	real := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	other := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.login(real.URL)
	h.stdin = strings.NewReader(testKey)
	code := h.run("auth", "login", "--api-url", other.URL, "--backend", "file")
	if code != apperr.ExitInsecure || !strings.Contains(h.stderr.String(), "--include-keyring") {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if len(other.requests) != 0 || !h.storedKeyring(auth.DefaultProfile) {
		t.Fatal("the login should have changed nothing")
	}
}

// A poisoned directory with its own credentials: the attacker's key, for the
// attacker's server. It would receive search text, so the keyring entry gives it away.
func TestPoisonedFileBackendIsRefusedWhenTheKeyringDisagrees(t *testing.T) {
	real := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	evil := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.login(real.URL)
	h.poisonConfig(evil.URL, auth.BackendFile)
	file, err := auth.NewFileStore()
	if err != nil {
		t.Fatal(err)
	}
	if err := file.Set(auth.KeyName(auth.DefaultProfile), h.entryFor(evil.URL)); err != nil {
		t.Fatal(err)
	}
	if code := h.run("query", "private search text"); code != apperr.ExitInsecure {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if len(evil.requests) != 0 {
		t.Fatal("search text reached the attacker")
	}
}

func TestFileBackendStatesTheServerInUse(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--api-url", api.URL, "--backend", "file"); code != 0 {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	if code := h.run("collections"); code != 0 {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	if !strings.Contains(h.stderr.String(), "for http://127.0.0.1") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
}

// plantCredentials gives the poisoned directory its own credentials file.
func (h *harness) plantCredentials(url string) {
	h.t.Helper()
	file, err := auth.NewFileStore()
	if err != nil {
		h.t.Fatal(err)
	}
	if err := file.Set(auth.KeyName(auth.DefaultProfile), h.entryFor(url)); err != nil {
		h.t.Fatal(err)
	}
}

// The hint on the conflict error once told the victim to do exactly this.
func TestLogoutUnderAPoisonedConfigKeepsTheKeyringKey(t *testing.T) {
	real := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	evil := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.login(real.URL)
	want := h.keyring.values[auth.KeyName(auth.DefaultProfile)]
	h.poisonConfig(evil.URL, auth.BackendFile)
	h.plantCredentials(evil.URL)

	if code := h.run("auth", "logout"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if h.keyring.values[auth.KeyName(auth.DefaultProfile)] != want {
		t.Fatal("the real keyring entry was deleted")
	}
	if !strings.Contains(h.stderr.String(), "--include-keyring") || !strings.Contains(h.stderr.String(), "127.0.0.1") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	if _, err := os.Stat(filepath.Join(os.Getenv("XDG_CONFIG_HOME"), "memoria", credentialFile)); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("the planted credentials should be gone: %v", err)
	}
}

func TestLogoutWithNoConfigKeepsTheKeyringKey(t *testing.T) {
	real := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.login(real.URL)
	t.Setenv("XDG_CONFIG_HOME", t.TempDir())
	if code := h.run("auth", "logout"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if !h.storedKeyring(auth.DefaultProfile) {
		t.Fatal("the real keyring entry was deleted")
	}
}

func TestLogoutIncludeKeyringIsExplicit(t *testing.T) {
	real := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	evil := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.login(real.URL)
	h.poisonConfig(evil.URL, auth.BackendKeyring)
	if code := h.run("auth", "logout", "--include-keyring"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if h.storedKeyring(auth.DefaultProfile) {
		t.Fatal("--include-keyring should remove it")
	}
}

func TestLogoutPrintsTheOriginRemoved(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.login(api.URL)
	if code := h.run("auth", "logout"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if !strings.Contains(h.stderr.String(), "Removed the key for http://127.0.0.1") || !strings.Contains(h.stderr.String(), "OS keyring") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
}

func TestLogoutWithABusRefusedRemovesOnlyTheFile(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--api-url", api.URL, "--backend", "file"); code != 0 {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	h.ringErr = fmt.Errorf("%w: %w", auth.ErrKeyringUnavailable, auth.ErrBusRefused)
	if code := h.run("auth", "logout"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if !strings.Contains(h.stderr.String(), "not touched") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
}

func TestPoisonedDirectoryErrorDoesNotSuggestLogout(t *testing.T) {
	real := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	evil := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.login(real.URL)
	h.poisonConfig(evil.URL, auth.BackendFile)
	h.plantCredentials(evil.URL)
	h.run("collections")
	if strings.Contains(h.stderr.String(), "logout") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
}

// A poisoned directory plus a bad bus address once made the keyring look absent, so
// the conflict check passed and the attacker's key was used.
func TestRefusedBusFailsTheConflictCheckClosed(t *testing.T) {
	evil := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.poisonConfig(evil.URL, auth.BackendFile)
	h.plantCredentials(evil.URL)
	h.ringErr = fmt.Errorf("%w: %w", auth.ErrKeyringUnavailable, auth.ErrBusRefused)
	if code := h.run("query", "private search text"); code != apperr.ExitInsecure {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if len(evil.requests) != 0 {
		t.Fatal("search text reached the attacker")
	}
	if !strings.Contains(h.stderr.String(), "DBUS_SESSION_BUS_ADDRESS") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
}

func TestRefusedBusStopsAFileLogin(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.ringErr = fmt.Errorf("%w: %w", auth.ErrKeyringUnavailable, auth.ErrBusRefused)
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--api-url", api.URL, "--backend", "file"); code != apperr.ExitInsecure {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if len(api.requests) != 0 {
		t.Fatal("the key was sent")
	}
}

// A headless host really has no keyring, and the file backend is for it.
func TestAbsentKeyringStillAllowsTheFileBackend(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.noRing = true
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--api-url", api.URL, "--backend", "file"); code != 0 {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	if code := h.run("collections"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
}

func TestFileBackendServerLineIgnoresQuiet(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--api-url", api.URL, "--backend", "file"); code != 0 {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	if code := h.run("collections", "--quiet"); code != 0 {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	if !strings.Contains(h.stderr.String(), "for http://127.0.0.1") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
}

// A file login leaves a marker, so a later poisoned directory conflicts with it even
// though the keyring holds no key.
func TestFileLoginLeavesAMarkerThatExposesAPoisonedDirectory(t *testing.T) {
	real := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	evil := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--api-url", real.URL, "--backend", "file"); code != 0 {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	if marker := h.keyring.values[auth.OriginMarkerName(auth.DefaultProfile)]; !strings.HasPrefix(marker, "http://127.0.0.1") || strings.Contains(marker, testKey) {
		t.Fatalf("marker = %q", marker)
	}
	h.poisonConfig(evil.URL, auth.BackendFile)
	h.plantCredentials(evil.URL)
	if code := h.run("query", "private search text"); code != apperr.ExitInsecure {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if len(evil.requests) != 0 {
		t.Fatal("search text reached the attacker")
	}
}

func TestFileLoginCanMoveToANewServerExplicitly(t *testing.T) {
	first := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	second := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	for _, api := range []*fakeAPI{first, second} {
		h.stdin = strings.NewReader(testKey)
		if code := h.run("auth", "login", "--api-url", api.URL, "--backend", "file"); code != 0 {
			t.Fatalf("stderr = %q", h.stderr.String())
		}
	}
	if code := h.run("collections"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
}

func TestKeyringLoginClearsTheMarker(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--api-url", api.URL, "--backend", "file"); code != 0 {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--api-url", api.URL); code != 0 {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	if _, ok := h.keyring.values[auth.OriginMarkerName(auth.DefaultProfile)]; ok {
		t.Fatal("the marker should be gone")
	}
}

func TestLogoutClearsTheMarkerForTheConfigsServer(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--api-url", api.URL, "--backend", "file"); code != 0 {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	if code := h.run("auth", "logout"); code != 0 {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	if len(h.keyring.values) != 0 {
		t.Fatalf("keyring = %v", h.keyring.values)
	}
}

// A proxy's login page is a 200 as well.
func TestLoginDoesNotStoreAKeyOnAResponseItCannotRead(t *testing.T) {
	for name, reply := range map[string]http.HandlerFunc{
		"html": func(w http.ResponseWriter, _ *http.Request) {
			w.Header().Set("Content-Type", "text/html")
			_, _ = io.WriteString(w, "<html>Sign in</html>")
		},
		"null":         jsonReply(`null`),
		"empty object": jsonReply(`{}`),
	} {
		t.Run(name, func(t *testing.T) {
			api := newFakeAPI(t, reply)
			h := newHarness(t)
			h.stdin = strings.NewReader(testKey)
			if code := h.run("auth", "login", "--api-url", api.URL); code != apperr.ExitGeneric {
				t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
			}
			if len(h.keyring.values) != 0 {
				t.Fatal("the key was stored")
			}
		})
	}
}

func TestStatusDoesNotCallAnHTMLPageAcceptance(t *testing.T) {
	api := newFakeAPI(t, func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "text/html")
		_, _ = io.WriteString(w, "<html>Sign in</html>")
	})
	h := newHarness(t)
	h.login(api.URL)
	if code := h.run("auth", "status"); code == 0 {
		t.Fatalf("stdout = %q", h.stdout.String())
	}
	if !strings.Contains(h.stdout.String(), "accepted by server: false") {
		t.Fatalf("stdout = %q", h.stdout.String())
	}
}

// The message field once carried a server's words to stdout unscrubbed, including
// an Authorization header that belonged to some other token.
func TestStatusJSONScrubsTheServersWords(t *testing.T) {
	api := newFakeAPI(t, func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/problem+json")
		w.WriteHeader(401)
		_, _ = io.WriteString(w, `{"title":"T","detail":"saw Authorization: Bearer other-token-value and mem_aaaabbbb_ccccdddd"}`)
	})
	h := newHarness(t)
	h.login(api.URL)
	h.run("auth", "status", "--format", "json")
	for _, leak := range []string{"other-token-value", "ccccdddd"} {
		if strings.Contains(h.stdout.String(), leak) {
			t.Fatalf("stdout = %q", h.stdout.String())
		}
	}
}
