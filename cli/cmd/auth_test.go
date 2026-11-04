package cmd

import (
	"errors"
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
	if h.keyring.values[auth.KeyName("default")] != testKey {
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
	if h.keyring.values[auth.KeyName("default")] != testKey {
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
	if !strings.Contains(h.stderr.String(), "no read access") || h.keyring.values[auth.KeyName("default")] != testKey {
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
	if h.keyring.values[auth.KeyName("default")] != testKey {
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
	if len(h.keyring.values) != 0 {
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
	if h.keyring.values[auth.KeyName("work")] != testKey || h.savedConfig().ActiveProfile != "work" {
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
	h.keyring.values[auth.KeyName("default")] = testKey

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
		if code := h.run("auth", "logout"); code != 0 || !strings.Contains(h.stderr.String(), "No stored key") {
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
