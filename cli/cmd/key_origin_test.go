package cmd

import (
	"encoding/json"
	"os"
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
	for name, c := range map[string]struct {
		setup func(h *harness) []string
		warns string
	}{
		"flag":               {func(*harness) []string { return []string{"--allow-private-http"} }, "warning: plain http to a private address is explicitly allowed"},
		"environment":        {func(h *harness) []string { h.env[auth.EnvAllowPrivateHTTP] = "1"; return nil }, "warning: plain http to a private address is explicitly allowed"},
		"the broader opt-in": {func(*harness) []string { return []string{"--allow-insecure-http"} }, "warning: plain http to a private address is explicitly allowed"},
	} {
		t.Run(name, func(t *testing.T) {
			h := newHarness(t)
			h.env[auth.EnvAPIKey] = testKey
			args := append([]string{"status", "--api-url", "http://10.255.255.1:9", "--timeout", "50ms"}, c.setup(h)...)
			if code := h.run(args...); code == apperr.ExitInsecure {
				t.Fatalf("still refused as insecure: stderr = %q", h.stderr.String())
			}
			if !strings.Contains(h.stderr.String(), c.warns) {
				t.Fatalf("stderr = %q, want %q", h.stderr.String(), c.warns)
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

// The refusal comes before the config's URL is looked at, so a planted plain-http server cannot
// turn it into a request to opt in to plain http for a server nobody chose.
func TestAnExplicitKeyIsRefusedBeforeTheConfigURLIsJudged(t *testing.T) {
	for _, planted := range []string{"http://10.0.0.9:8080", "http://memoria.lan", "http://8.8.8.8"} {
		h := newHarness(t)
		h.writeConfigFile(`{"active_profile":"default","profiles":{"default":{"api_url":"` + planted + `"}}}`)
		h.env[auth.EnvAPIKey] = testKey
		if code := h.run("collections"); code != apperr.ExitInsecure {
			t.Fatalf("%s: code = %d", planted, code)
		}
		text := h.stderr.String()
		if strings.Contains(text, "allow-private-http") || strings.Contains(text, "allow-insecure-http") || !strings.Contains(text, auth.EnvURL) {
			t.Fatalf("%s: stderr = %q", planted, text)
		}
	}
}

func TestLoginWithAKeyFlagNamesItsServer(t *testing.T) {
	api := newFakeAPI(t, jsonReply(`{"collections":[]}`))
	h := newHarness(t)
	h.login(api.URL)
	before := len(api.requests)

	if code := h.run("auth", "login", "--api-key", testKey); code != apperr.ExitInsecure || !strings.Contains(h.stderr.String(), "--api-url") {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if len(api.requests) != before {
		t.Fatal("the key was sent to the config's server")
	}
}

// childEnvHelperArg marks a run of this test binary as the child, which prints its environment.
const childEnvHelperArg = "memoria-childenv-helper"

// TestChildEnvHelper is not a test: the tests below start this binary as a child process, and
// this prints what the child inherited. Arguments, not the environment, mark it, since the
// environment is what is being checked.
func TestChildEnvHelper(t *testing.T) {
	if !slices.Contains(os.Args, childEnvHelperArg) {
		return
	}
	seen := map[string]string{}
	for _, entry := range os.Environ() {
		name, value, _ := strings.Cut(entry, "=")
		seen[name] = value
	}
	data, _ := json.Marshal(seen)
	_, _ = os.Stdout.Write(append(data, '\n'))
	os.Exit(0)
}

func setChildEnvTestVariables(t *testing.T) {
	t.Setenv(auth.EnvAPIKey, testKey)
	t.Setenv("GITHUB_TOKEN", "ghp_must_not_reach_the_child")
	t.Setenv("OPENAI_API_KEY", "sk-must-not-reach-the-child")
	t.Setenv("QMD_TEST_ORDINARY", "kept")
}

func assertChildEnvironment(t *testing.T, out []byte) {
	t.Helper()
	var seen map[string]string
	if err := json.Unmarshal(out, &seen); err != nil {
		t.Fatalf("output %q: %v", out, err)
	}
	for name := range seen {
		for _, secret := range []string{auth.EnvAPIKey, "GITHUB_TOKEN", "OPENAI_API_KEY"} {
			if strings.EqualFold(name, secret) {
				t.Fatalf("%s reached the child", name)
			}
		}
	}
	if seen["QMD_TEST_ORDINARY"] != "kept" {
		t.Fatalf("an allowed variable was dropped: %v", seen)
	}
}

func helperArgs() []string {
	return []string{"-test.run=^TestChildEnvHelper$", "--", childEnvHelperArg}
}

// The redaction tool's process, run for real with this binary standing in for dotnet.
func TestTheRedactionToolInheritsNoCredential(t *testing.T) {
	setChildEnvTestVariables(t)
	command := dotnetCommand(t.Context(), helperArgs(), nil)
	if command.Env == nil {
		t.Fatal("the tool would inherit the whole environment")
	}
	command.Path = os.Args[0]
	command.Args = append([]string{os.Args[0]}, helperArgs()...)
	out, err := command.Output()
	if err != nil {
		t.Fatal(err)
	}
	assertChildEnvironment(t, out)
}

// A real child process, run the way verify runs a local qmd that is a program.
func TestLocalQMDInheritsNoCredential(t *testing.T) {
	setChildEnvTestVariables(t)
	exe, err := filepath.Abs(os.Args[0])
	if err != nil {
		t.Fatal(err)
	}
	out, err := runLocalQMD(t.Context(), exe, helperArgs())
	if err != nil {
		t.Fatal(err)
	}
	assertChildEnvironment(t, out)
}
