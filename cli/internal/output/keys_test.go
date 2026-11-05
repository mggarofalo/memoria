package output

import (
	"bytes"
	"strings"
	"testing"
	"time"

	"github.com/mggarofalo/memoria/cli/internal/api"
)

func keyList() *api.ApiKeyList {
	past := time.Now().Add(-time.Hour)
	future := time.Now().Add(time.Hour)
	return &api.ApiKeyList{Keys: []api.ApiKeyInfo{
		{Id: "a1", Label: "one", Scopes: []string{"read"}, Collections: []string{"notes", "docs"}, ExpiresAt: &future, LastUsedAt: &past},
		{Id: "a2", Label: "two", Scopes: []string{"read", "ingest"}, AllCollections: true, RevokedAt: &past},
		{Id: "a3", Label: "three", Scopes: []string{"admin"}, ExpiresAt: &past},
	}}
}

func TestKeysTableShowsStateAndReach(t *testing.T) {
	var out bytes.Buffer
	if err := KeysTable(&out, keyList()); err != nil {
		t.Fatal(err)
	}
	lines := strings.Split(strings.TrimSpace(out.String()), "\n")
	if len(lines) != 4 {
		t.Fatalf("out = %q", out.String())
	}
	for i, want := range map[int][]string{
		1: {"a1", "active", "notes,docs"},
		2: {"a2", "revoked", "ALL collections"},
		3: {"a3", "expired", "no collections"},
	} {
		for _, text := range want {
			if !strings.Contains(lines[i], text) {
				t.Errorf("line %d = %q lacks %q", i, lines[i], text)
			}
		}
	}
}

func TestKeysMarkdown(t *testing.T) {
	var out bytes.Buffer
	if err := KeysMarkdown(&out, keyList()); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.String(), "**one** (active): read; notes,docs") || !strings.Contains(out.String(), "ALL collections") {
		t.Fatalf("out = %q", out.String())
	}
}

func TestKeyListCannotCarryEscapesOrMarkup(t *testing.T) {
	hostileLabel := "x\x1b]52;c;ZXZpbA==\x07 ![i](http://evil/x)"
	list := &api.ApiKeyList{Keys: []api.ApiKeyInfo{{Id: "a\x1b[2J", Label: hostileLabel, Scopes: []string{"read\x1b[2J"}, Collections: []string{"c\x1b[2J"}}}}
	for name, render := range map[string]func(*bytes.Buffer) error{
		"table":    func(w *bytes.Buffer) error { return KeysTable(w, list) },
		"markdown": func(w *bytes.Buffer) error { return KeysMarkdown(w, list) },
	} {
		var out bytes.Buffer
		if err := render(&out); err != nil {
			t.Fatal(err)
		}
		if strings.Contains(out.String(), "\x1b") || strings.Contains(out.String(), "\x07") {
			t.Errorf("%s kept an escape: %q", name, out.String())
		}
	}
	var out bytes.Buffer
	if err := KeysMarkdown(&out, list); err != nil {
		t.Fatal(err)
	}
	if strings.Contains(out.String(), "![i](") {
		t.Fatalf("live markdown: %q", out.String())
	}
}
