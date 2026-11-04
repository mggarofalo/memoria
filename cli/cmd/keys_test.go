package cmd

import (
	"encoding/json"
	"io"
	"net/http"
	"strings"
	"testing"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/auth"
)

const (
	newKeySecret = "1111222233334444555566667777888899990000aaaabbbbccccddddeeeeffff"
	newKeyID     = "abcdef0123456789"
	newKey       = "mem_" + newKeyID + "_" + newKeySecret
	testKeyID    = "0011223344556677"
)

func issuedBody(id string) string {
	return `{"key":"` + newKey + `","info":{"id":"` + id + `","label":"laptop","scopes":["read"],"collections":["notes"],"allCollections":false,"createdAt":"2026-01-01T00:00:00Z","expiresAt":null,"revokedAt":null,"lastUsedAt":null}}`
}

func replyStatus(status int, body string) http.HandlerFunc {
	return func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(status)
		_, _ = io.WriteString(w, body)
	}
}

func adminHarness(t *testing.T, reply http.HandlerFunc) (*harness, *fakeAPI) {
	t.Helper()
	api := newFakeAPI(t, reply)
	h := newHarness(t)
	h.login(api.URL)
	return h, api
}

func TestKeysCreateWritesThePlaintextToStdoutOnly(t *testing.T) {
	h, api := adminHarness(t, replyStatus(201, issuedBody(newKeyID)))
	code := h.run("keys", "create", "--label", "laptop", "--scope", "read", "--collection", "notes")
	if code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if h.stdout.String() != newKey+"\n" {
		t.Fatalf("stdout = %q", h.stdout.String())
	}
	if strings.Contains(h.stderr.String(), newKeySecret) {
		t.Fatalf("the plaintext reached stderr: %q", h.stderr.String())
	}
	if !strings.Contains(h.stderr.String(), newKeyID) || !strings.Contains(h.stderr.String(), "only time") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
	assertCreateRequest(t, api)
}

func assertCreateRequest(t *testing.T, api *fakeAPI) {
	t.Helper()
	req := api.requests[0]
	if req.Method != http.MethodPost || !strings.HasPrefix(req.URL.Path, "/v1/admin/keys") {
		t.Fatalf("request = %s %s", req.Method, req.URL.Path)
	}
	var body map[string]any
	if err := json.Unmarshal([]byte(api.bodies[0]), &body); err != nil {
		t.Fatal(err)
	}
	if body["label"] != "laptop" || body["allCollections"] != nil {
		t.Fatalf("body = %s", api.bodies[0])
	}
}

func TestKeysCreateDoesNotPutThePlaintextInConfig(t *testing.T) {
	h, _ := adminHarness(t, replyStatus(201, issuedBody(newKeyID)))
	h.run("keys", "create", "--label", "laptop", "--scope", "read", "--collection", "notes")
	if strings.Contains(h.configContent(), newKeySecret) {
		t.Fatal("the plaintext reached the config")
	}
}

func TestKeysCreateAllCollectionsIsExplicit(t *testing.T) {
	h, api := adminHarness(t, replyStatus(201, issuedBody(newKeyID)))
	if code := h.run("keys", "create", "--label", "all", "--scope", "read", "--all-collections"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if !strings.Contains(api.bodies[0], `"allCollections":true`) {
		t.Fatalf("body = %s", api.bodies[0])
	}
}

// With no flag the request says nothing about collections, so the server's refusal
// is what the user sees. The CLI never turns an omission into every collection.
func TestKeysCreateNeverDefaultsToAllCollections(t *testing.T) {
	h, api := adminHarness(t, func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/problem+json")
		w.WriteHeader(400)
		_, _ = io.WriteString(w, `{"title":"One or more validation errors occurred.","errors":{"collections":["List the collections this key may read, or set allCollections to true to request every collection."]}}`)
	})
	if code := h.run("keys", "create", "--label", "x", "--scope", "read"); code != apperr.ExitUsage {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if !strings.Contains(api.bodies[0], `"allCollections":null`) || !strings.Contains(api.bodies[0], `"collections":null`) {
		t.Fatalf("body = %s", api.bodies[0])
	}
	if !strings.Contains(h.stderr.String(), "List the collections") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
}

func TestKeysCreateUsageErrors(t *testing.T) {
	for name, args := range map[string][]string{
		"no label":   {"--scope", "read", "--collection", "c"},
		"no scope":   {"--label", "x", "--collection", "c"},
		"both":       {"--label", "x", "--scope", "read", "--collection", "c", "--all-collections"},
		"bad expiry": {"--label", "x", "--scope", "admin", "--expires-at", "tomorrow"},
		"blank":      {"--label", "  ", "--scope", "admin"},
	} {
		t.Run(name, func(t *testing.T) {
			h, api := adminHarness(t, replyStatus(201, issuedBody(newKeyID)))
			if code := h.run(append([]string{"keys", "create"}, args...)...); code != apperr.ExitUsage {
				t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
			}
			if len(api.requests) != 0 {
				t.Fatal("the server was contacted")
			}
		})
	}
}

func TestKeysCreateSendsExpiry(t *testing.T) {
	h, api := adminHarness(t, replyStatus(201, issuedBody(newKeyID)))
	if code := h.run("keys", "create", "--label", "x", "--scope", "admin", "--expires-at", "2027-01-01T00:00:00Z"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if !strings.Contains(api.bodies[0], `"expiresAt":"2027-01-01T00:00:00Z"`) {
		t.Fatalf("body = %s", api.bodies[0])
	}
}

func TestKeysCreateJSONIsTheResponse(t *testing.T) {
	body := issuedBody(newKeyID)
	h, _ := adminHarness(t, replyStatus(201, body))
	if code := h.run("keys", "create", "--label", "x", "--scope", "read", "--collection", "c", "--format", "json"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if h.stdout.String() != body {
		t.Fatalf("stdout = %q", h.stdout.String())
	}
}

// Once written, the new key must never be repeated, for example by a later error.
func TestIssuedKeyIsRegisteredForRedaction(t *testing.T) {
	h, _ := adminHarness(t, replyStatus(201, issuedBody(newKeyID)))
	a := h.app()
	if code := a.run(t.Context(), []string{"keys", "create", "--label", "x", "--scope", "read", "--collection", "c"}); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if got := a.redactor.String(newKey); strings.Contains(got, newKeySecret) {
		t.Fatalf("got %q", got)
	}
}

func TestIssuedKeyThatCannotBeWrittenSaysToRevokeIt(t *testing.T) {
	h, _ := adminHarness(t, replyStatus(201, issuedBody(newKeyID)))
	a := h.app()
	a.stdout = failingWriter{}
	code := a.run(t.Context(), []string{"keys", "create", "--label", "x", "--scope", "read", "--collection", "c"})
	if code != apperr.ExitGeneric || !strings.Contains(h.stderr.String(), "revoke it") {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
}

func TestNonAdminKeyGetsAHelpfulError(t *testing.T) {
	forbidden := func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/problem+json")
		w.WriteHeader(403)
		_, _ = io.WriteString(w, `{"title":"Insufficient scope","status":403,"detail":"This API key lacks the 'admin' scope.","code":"insufficient_scope"}`)
	}
	for name, args := range map[string][]string{
		"create": {"keys", "create", "--label", "x", "--scope", "read", "--collection", "c"},
		"list":   {"keys", "list"},
		"rotate": {"keys", "rotate", newKeyID},
		"revoke": {"keys", "revoke", newKeyID},
	} {
		t.Run(name, func(t *testing.T) {
			h, _ := adminHarness(t, forbidden)
			if code := h.run(args...); code != apperr.ExitAuth {
				t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
			}
			if !strings.Contains(h.stderr.String(), "--bootstrap-admin-key") {
				t.Fatalf("stderr = %q", h.stderr.String())
			}
		})
	}
}

// By design: a bootstrapped admin key reads nothing, which surprises everyone once.
func TestAdminKeyReadingGetsAHelpfulError(t *testing.T) {
	forbidden := func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/problem+json")
		w.WriteHeader(403)
		_, _ = io.WriteString(w, `{"title":"Insufficient scope","status":403,"detail":"This API key lacks the 'read' scope.","code":"insufficient_scope"}`)
	}
	for _, args := range [][]string{{"query", "x"}, {"get", "a"}, {"multi-get", "a"}, {"collections"}, {"status"}} {
		t.Run(args[0], func(t *testing.T) {
			h, _ := adminHarness(t, forbidden)
			if code := h.run(args...); code != apperr.ExitAuth {
				t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
			}
			if !strings.Contains(h.stderr.String(), "memoria keys create") || !strings.Contains(h.stderr.String(), "by design") {
				t.Fatalf("stderr = %q", h.stderr.String())
			}
		})
	}
}

func TestLoginWithAnAdminKeyExplainsWhatItCannotDo(t *testing.T) {
	api := newFakeAPI(t, func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/problem+json")
		w.WriteHeader(403)
		_, _ = io.WriteString(w, `{"title":"Insufficient scope","detail":"This API key lacks the 'read' scope.","code":"insufficient_scope"}`)
	})
	h := newHarness(t)
	h.stdin = strings.NewReader(testKey)
	if code := h.run("auth", "login", "--api-url", api.URL); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if !strings.Contains(h.stderr.String(), "memoria keys") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
}

func TestKeysListFormats(t *testing.T) {
	body := `{"keys":[{"id":"` + newKeyID + `","label":"laptop","scopes":["read"],"collections":["notes"],"allCollections":false,"createdAt":"2026-01-01T00:00:00Z","expiresAt":null,"revokedAt":null,"lastUsedAt":null}]}`
	for format, want := range map[string]string{"": "LABEL", "table": "LABEL", "markdown": "**laptop**", "json": body} {
		t.Run("format "+format, func(t *testing.T) {
			h, api := adminHarness(t, jsonReply(body))
			args := []string{"keys", "list"}
			if format != "" {
				args = append(args, "--format", format)
			}
			if code := h.run(args...); code != 0 {
				t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
			}
			if !strings.Contains(h.stdout.String(), want) {
				t.Fatalf("stdout = %q", h.stdout.String())
			}
			if api.requests[0].Method != http.MethodGet {
				t.Fatalf("method = %s", api.requests[0].Method)
			}
		})
	}
}

func TestKeysListShapeless(t *testing.T) {
	for _, body := range []string{`null`, `{}`} {
		h, _ := adminHarness(t, jsonReply(body))
		if code := h.run("keys", "list"); code != apperr.ExitGeneric {
			t.Fatalf("%s: code = %d", body, code)
		}
	}
}

func TestKeysRotate(t *testing.T) {
	h, api := adminHarness(t, replyStatus(200, issuedBody(newKeyID)))
	if code := h.run("keys", "rotate", newKeyID); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if h.stdout.String() != newKey+"\n" || strings.Contains(h.stderr.String(), newKeySecret) {
		t.Fatalf("stdout = %q, stderr = %q", h.stdout.String(), h.stderr.String())
	}
	req := api.requests[0]
	if req.Method != http.MethodPost || !strings.Contains(req.URL.Path, newKeyID+":rotate") {
		t.Fatalf("request = %s %s", req.Method, req.URL.Path)
	}
	if strings.Contains(h.stderr.String(), "stopped working") {
		t.Fatalf("this was another key: %q", h.stderr.String())
	}
}

func TestRotatingTheKeyInUseSaysSo(t *testing.T) {
	h, _ := adminHarness(t, replyStatus(200, issuedBody(testKeyID)))
	if code := h.run("keys", "rotate", testKeyID); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if !strings.Contains(h.stderr.String(), "auth login") {
		t.Fatalf("stderr = %q", h.stderr.String())
	}
}

func TestKeysRotateOfARevokedKeyReportsTheConflict(t *testing.T) {
	h, _ := adminHarness(t, func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/problem+json")
		w.WriteHeader(409)
		_, _ = io.WriteString(w, `{"title":"Key is not active","detail":"A revoked or expired key cannot be rotated."}`)
	})
	if code := h.run("keys", "rotate", newKeyID); code != apperr.ExitGeneric || !strings.Contains(h.stderr.String(), "cannot be rotated") {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
}

func TestKeysRevoke(t *testing.T) {
	h, api := adminHarness(t, replyStatus(204, ""))
	if code := h.run("keys", "revoke", newKeyID); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if req := api.requests[0]; req.Method != http.MethodDelete || !strings.HasSuffix(req.URL.Path, "/"+newKeyID) {
		t.Fatalf("request = %s %s", req.Method, req.URL.Path)
	}
	if h.stdout.Len() != 0 || !strings.Contains(h.stderr.String(), "Revoked key "+newKeyID) {
		t.Fatalf("stdout = %q, stderr = %q", h.stdout.String(), h.stderr.String())
	}
}

func TestRevokingTheKeyInUseNeedsForce(t *testing.T) {
	h, api := adminHarness(t, replyStatus(204, ""))
	if code := h.run("keys", "revoke", testKeyID); code != apperr.ExitUsage {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
	if len(api.requests) != 0 {
		t.Fatal("the server was contacted")
	}
	if code := h.run("keys", "revoke", testKeyID, "--force"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
	}
}

// A pasted key must not go into a URL path, where it would land in server logs.
func TestWholeKeyAsAnIDIsRefusedUnsent(t *testing.T) {
	for _, verb := range []string{"rotate", "revoke"} {
		t.Run(verb, func(t *testing.T) {
			h, api := adminHarness(t, replyStatus(204, ""))
			if code := h.run("keys", verb, testKey); code != apperr.ExitUsage {
				t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
			}
			if len(api.requests) != 0 {
				t.Fatal("the server was contacted")
			}
		})
	}
}

func TestKeyIDArgumentCount(t *testing.T) {
	for _, args := range [][]string{{"keys", "rotate"}, {"keys", "revoke", "a", "b"}} {
		h := newHarness(t)
		if code := h.run(args...); code != apperr.ExitUsage {
			t.Fatalf("%v: code = %d", args, code)
		}
	}
}

func TestKeysServerFailures(t *testing.T) {
	for name, args := range map[string][]string{
		"create": {"keys", "create", "--label", "x", "--scope", "admin"},
		"list":   {"keys", "list"},
		"rotate": {"keys", "rotate", newKeyID},
		"revoke": {"keys", "revoke", newKeyID},
	} {
		t.Run(name+" no key", func(t *testing.T) {
			h := newHarness(t)
			h.env[auth.EnvURL] = "https://memoria.example.com"
			if code := h.run(args...); code != apperr.ExitAuth {
				t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
			}
		})
		t.Run(name+" server down", func(t *testing.T) {
			api := newFakeAPI(t, jsonReply(`{}`))
			url := api.URL
			api.Close()
			h := newHarness(t)
			h.login(url)
			if code := h.run(args...); code != apperr.ExitUnavailable {
				t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
			}
		})
		t.Run(name+" unexpected success", func(t *testing.T) {
			h, _ := adminHarness(t, replyStatus(202, `{}`))
			if code := h.run(args...); code != apperr.ExitGeneric || !strings.Contains(h.stderr.String(), "cannot read") {
				t.Fatalf("code = %d, stderr = %q", code, h.stderr.String())
			}
		})
	}
}

func TestIssuedKeyWithoutAKeyIsRefused(t *testing.T) {
	h, _ := adminHarness(t, replyStatus(201, `{"key":"","info":{"id":"x","label":"l","scopes":[],"collections":[],"allCollections":false,"createdAt":"2026-01-01T00:00:00Z","expiresAt":null,"revokedAt":null,"lastUsedAt":null}}`))
	if code := h.run("keys", "create", "--label", "x", "--scope", "admin"); code != apperr.ExitGeneric {
		t.Fatalf("code = %d", code)
	}
}

func TestKeysGroupShowsHelp(t *testing.T) {
	h := newHarness(t)
	if code := h.run("keys"); code != 0 || !strings.Contains(h.stdout.String(), "create") {
		t.Fatalf("code = %d, stdout = %q", code, h.stdout.String())
	}
	if code := h.run("keys", "extra"); code != apperr.ExitUsage {
		t.Fatalf("code = %d", code)
	}
}
