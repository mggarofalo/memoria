package cmd

import (
	"encoding/json"
	"os"
	"os/exec"
	"path/filepath"
	"slices"
	"strings"
	"testing"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/auth"
)

// A key from --api-key or MEMORIA_API_KEY is tied to no server. The config file
// can be planted by anyone who can set XDG_CONFIG_HOME or HOME, so the key goes
// only to a server named in the same invocation.
func TestAnExplicitKeyIsNeverSentToTheServerInTheConfig(t *testing.T) {
	for name, setup := range map[string]func(h *harness) []string{
		"from the environment": func(h *harness) []string {
			h.env[auth.EnvAPIKey] = testKey
			return []string{"collections"}
		},
		"from the environment, quiet and piped": func(h *harness) []string {
			h.env[auth.EnvAPIKey] = testKey
			return []string{"collections", "--quiet"}
		},
		"from the flag": func(_ *harness) []string {
			return []string{"collections", "--api-key", testKey}
		},
		"mcp, from the environment": func(h *harness) []string {
			h.env[auth.EnvAPIKey] = testKey
			h.stdin = strings.NewReader(`{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_collections","arguments":{}}}` + "\n")
			return []string{"mcp"}
		},
	} {
		t.Run(name, func(t *testing.T) {
			planted := newFakeAPI(t, jsonReply(`{"collections":[]}`))
			h := newHarness(t)
			h.writeConfigFile(`{"active_profile":"default","profiles":{"default":{"api_url":"` + planted.URL + `"}}}`)
			args := setup(h)

			code := h.run(args...)

			if len(planted.requests) != 0 {
				t.Fatalf("the config's server received %d request(s)", len(planted.requests))
			}
			text := h.stderr.String() + h.stdout.String()
			if args[0] != "mcp" && code != apperr.ExitInsecure {
				t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
			}
			if !strings.Contains(text, "--api-url") || !strings.Contains(text, auth.EnvURL) {
				t.Fatalf("the refusal does not say how to name the server: %q", text)
			}
			if strings.Contains(text, planted.URL) || strings.Contains(text, strings.TrimPrefix(planted.URL, "http://")) {
				t.Fatalf("the refusal repeats the config's server: %q", text)
			}
		})
	}
}

func TestAnExplicitKeyGoesToAServerNamedTheSameWay(t *testing.T) {
	for name, setup := range map[string]func(h *harness, url string) []string{
		"env key, env url": func(h *harness, url string) []string {
			h.env[auth.EnvAPIKey], h.env[auth.EnvURL] = testKey, url
			return []string{"collections"}
		},
		"env key, flag url": func(h *harness, url string) []string {
			h.env[auth.EnvAPIKey] = testKey
			return []string{"collections", "--api-url", url}
		},
		"flag key, env url": func(h *harness, url string) []string {
			h.env[auth.EnvURL] = url
			return []string{"collections", "--api-key", testKey}
		},
	} {
		t.Run(name, func(t *testing.T) {
			chosen := newFakeAPI(t, jsonReply(`{"collections":[]}`))
			planted := newFakeAPI(t, jsonReply(`{"collections":[]}`))
			h := newHarness(t)
			h.writeConfigFile(`{"active_profile":"default","profiles":{"default":{"api_url":"` + planted.URL + `"}}}`)

			if code := h.run(setup(h, chosen.URL)...); code != 0 {
				t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
			}
			if len(chosen.requests) != 1 || len(planted.requests) != 0 {
				t.Fatalf("chosen got %d, planted got %d", len(chosen.requests), len(planted.requests))
			}
			if got := chosen.requests[0].Header.Get("Authorization"); got != "Bearer "+testKey {
				t.Fatalf("Authorization = %q", got)
			}
		})
	}
}

// A stored key keeps its own guard, the origin saved with it, so the config's URL still serves it.
func TestAStoredKeyStillUsesTheConfigURL(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.login(api.URL)

	if code := h.run("collections"); code != 0 || len(api.requests) != 1 {
		t.Fatalf("code = %d, requests = %d, stderr = %q", code, len(api.requests), h.stderr.String())
	}
}

func TestHelpNeverEchoesATopic(t *testing.T) {
	for name, args := range map[string][]string{
		"a key":                   {"help", testKey},
		"an opaque value":         {"help", "opaque-secret-value"},
		"a key after a command":   {"help", "auth", testKey},
		"a key beside a real one": {"help", testKey, "query"},
	} {
		t.Run(name, func(t *testing.T) {
			h := newHarness(t)
			h.run(args...)
			if text := h.stdout.String() + h.stderr.String(); strings.Contains(text, "opaque-secret-value") || strings.Contains(text, testKey) {
				t.Fatalf("output = %q", text)
			}
		})
	}
}

func TestHelpStillHelps(t *testing.T) {
	h := newHarness(t)
	if code := h.run("help", "query"); code != 0 || !strings.Contains(h.stdout.String(), "--collection") {
		t.Fatalf("code = %d, stdout = %q", code, h.stdout.String())
	}
	if code := h.run("help"); code != 0 || !strings.Contains(h.stdout.String(), "Available Commands") {
		t.Fatalf("code = %d, stdout = %q", code, h.stdout.String())
	}
	if code := h.run("help", "no-such-topic"); code != apperr.ExitUsage || !strings.Contains(h.stderr.String(), "unknown help topic") {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
}

func TestPlainHTTPToAPrivateAddressNeedsItsOptIn(t *testing.T) {
	h := newHarness(t)
	h.env[auth.EnvAPIKey] = testKey
	if code := h.run("status", "--api-url", "http://10.255.255.1"); code != apperr.ExitInsecure {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if !strings.Contains(h.stderr.String(), "--allow-private-http") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}

	// The MCP bridge reports it in its answer, where a client sees it, not only on stderr.
	h.stdin = strings.NewReader(`{"jsonrpc":"2.0","id":1,"method":"ping"}` + "\n")
	if code := h.run("mcp", "--api-url", "http://192.168.1.20:8080"); code != 0 || !strings.Contains(h.stdout.String(), `"kind":"insecure"`) {
		t.Fatalf("mcp: code = %d, stdout = %q", code, h.stdout.String())
	}
}

func TestThePrivateOptInIsAFlagOrAnEnvironmentVariable(t *testing.T) {
	for name, setup := range map[string]func(h *harness) []string{
		"flag":               func(*harness) []string { return []string{"--allow-private-http"} },
		"environment":        func(h *harness) []string { h.env[auth.EnvAllowPrivateHTTP] = "1"; return nil },
		"the broader opt-in": func(*harness) []string { return []string{"--allow-insecure-http"} },
	} {
		t.Run(name, func(t *testing.T) {
			h := newHarness(t)
			h.env[auth.EnvAPIKey] = testKey
			args := append([]string{"status", "--api-url", "http://10.255.255.1", "--timeout", "1ms"}, setup(h)...)
			if code := h.run(args...); code == apperr.ExitInsecure {
				t.Fatalf("still refused as insecure: stderr = %q", h.stderr.String())
			}
			if !strings.Contains(h.stderr.String(), "warning: ") {
				t.Fatalf("no warning: stderr = %q", h.stderr.String())
			}
		})
	}
}

func TestTheEnvironmentOptInIsExact(t *testing.T) {
	for _, value := range []string{"true", "yes", "0", " 1"} {
		h := newHarness(t)
		h.env[auth.EnvAPIKey] = testKey
		h.env[auth.EnvAllowPrivateHTTP] = value
		if code := h.run("status", "--api-url", "http://10.255.255.1"); code != apperr.ExitInsecure {
			t.Fatalf("%q: code = %d", value, code)
		}
	}
}

func TestThePrivateOptInDoesNotReachAPublicAddress(t *testing.T) {
	h := newHarness(t)
	h.env[auth.EnvAPIKey] = testKey
	if code := h.run("status", "--api-url", "http://8.8.8.8", "--allow-private-http"); code != apperr.ExitInsecure {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
}

func TestTheRedactionToolInheritsNoCredential(t *testing.T) {
	t.Setenv(auth.EnvAPIKey, testKey)
	t.Setenv("GITHUB_TOKEN", "ghp_must_not_reach_the_tool")
	t.Setenv("MEMORIA_TEST_ORDINARY", "kept")

	env := dotnetCommand(t.Context(), []string{"--version"}, nil).Env

	if env == nil {
		t.Fatal("the tool would inherit the whole environment")
	}
	for _, entry := range env {
		name, _, _ := strings.Cut(entry, "=")
		if strings.EqualFold(name, auth.EnvAPIKey) || strings.EqualFold(name, "GITHUB_TOKEN") {
			t.Fatalf("%s reached the tool", name)
		}
	}
	if !slices.Contains(env, "MEMORIA_TEST_ORDINARY=kept") {
		t.Fatal("an ordinary variable was dropped")
	}
}

// A real child process, run the way verify runs local qmd: what it sees of the environment is what it inherited.
func TestLocalQMDInheritsNoCredential(t *testing.T) {
	node, err := exec.LookPath("node")
	if err != nil {
		t.Skip("no node here to stand in for qmd")
	}
	script := filepath.Join(t.TempDir(), "env.js")
	if err := os.WriteFile(script, []byte("console.log(JSON.stringify(process.env))"), 0o600); err != nil {
		t.Fatal(err)
	}
	t.Setenv(auth.EnvAPIKey, testKey)
	t.Setenv("OPENAI_API_KEY", "sk-must-not-reach-qmd")
	t.Setenv("MEMORIA_TEST_ORDINARY", "kept")

	out, err := runLocalQMD(t.Context(), node, []string{script})
	if err != nil {
		t.Fatal(err)
	}
	assertChildEnvironment(t, out)
}

func assertChildEnvironment(t *testing.T, out []byte) {
	t.Helper()
	var seen map[string]string
	if err := json.Unmarshal(out, &seen); err != nil {
		t.Fatalf("output %q: %v", out, err)
	}
	for name := range seen {
		if strings.EqualFold(name, auth.EnvAPIKey) || strings.EqualFold(name, "OPENAI_API_KEY") {
			t.Fatalf("%s reached the child", name)
		}
	}
	if seen["MEMORIA_TEST_ORDINARY"] != "kept" {
		t.Fatal("an ordinary variable was dropped")
	}
}
