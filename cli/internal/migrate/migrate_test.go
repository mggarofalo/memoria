package migrate

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func fixture(t *testing.T, root, name, text string) {
	t.Helper()
	path := filepath.Join(root, filepath.FromSlash(name))
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte(text), 0o600); err != nil {
		t.Fatal(err)
	}
}

func writeConfig(t *testing.T, text string) string {
	t.Helper()
	path := filepath.Join(t.TempDir(), "index.yml")
	if err := os.WriteFile(path, []byte(text), 0o600); err != nil {
		t.Fatal(err)
	}
	return path
}

func TestLoadCollectionsReadsNameOrderAndDefaults(t *testing.T) {
	collections, err := LoadCollections(writeConfig(t, `
global_context: ignored
collections:
  zed:
    path: /srv/zed
    pattern: "**/*.txt"
    ignore: ["drafts/**"]
    update: "deploy --token not-for-this-file"
  alpha:
    path: ~/alpha
`))
	if err != nil {
		t.Fatal(err)
	}
	if len(collections) != 2 || collections[0].Name != "alpha" || collections[1].Name != "zed" {
		t.Fatalf("collections = %+v", collections)
	}
	if collections[0].Pattern != defaultPattern || !filepath.IsAbs(collections[0].Path) {
		t.Fatalf("alpha = %+v", collections[0])
	}
	if collections[1].Pattern != "**/*.txt" || collections[1].Ignore[0] != "drafts/**" {
		t.Fatalf("zed = %+v", collections[1])
	}
}

func TestExpandHomeLeavesOtherPathsAlone(t *testing.T) {
	if got := expandHome("/srv/x"); got != "/srv/x" {
		t.Fatalf("path = %q", got)
	}
	t.Setenv("HOME", "")
	t.Setenv("USERPROFILE", "")
	if got := expandHome("~/x"); got != "~/x" {
		t.Fatalf("with no home known, path = %q", got)
	}
}

func TestLoadCollectionsRefusalsQuoteNothing(t *testing.T) {
	// The parser quotes a short prefix of a value, so a prefix is what must not appear.
	const secret = "deploy-token-value"
	if _, err := LoadCollections(filepath.Join(t.TempDir(), "absent.yml")); err == nil {
		t.Fatal("a missing config was read")
	}
	// A syntax error and a type error: the parser quotes the value in the second.
	for _, text := range []string{"collections: [unclosed " + secret, "collections: " + secret} {
		_, err := LoadCollections(writeConfig(t, text))
		if err == nil || strings.Contains(err.Error(), secret[:6]) {
			t.Fatalf("err = %v", err)
		}
	}
	if _, err := LoadCollections(writeConfig(t, strings.Repeat("x", maxConfigBytes+1))); err == nil || !strings.Contains(err.Error(), "too large") {
		t.Fatalf("err = %v", err)
	}
	dir := t.TempDir()
	if _, err := LoadCollections(dir); err == nil {
		t.Fatal("a folder was read as a config")
	}
}

func TestInventorySelectsAsQMDDoes(t *testing.T) {
	dir := t.TempDir()
	for name, text := range map[string]string{
		"a.md":              "12345",
		"sub/b.md":          "123",
		"notes.txt":         "not markdown",
		".hidden.md":        "hidden",
		".obsidian/c.md":    "hidden folder",
		"node_modules/d.md": "excluded",
		"sub/vendor/e.md":   "excluded",
		"dist/f.md":         "excluded",
		"build/g.md":        "excluded",
		".cache/h.md":       "excluded",
		"drafts/i.md":       "ignored by rule",
	} {
		fixture(t, dir, name, text)
	}
	plan, files := Inventory(Collection{Name: "n", Path: dir, Pattern: defaultPattern, Ignore: []string{"drafts/**"}}, "t")
	if plan.Problem != "" || plan.Files != 2 || plan.Bytes != 8 || plan.Largest != 5 || plan.Target != "t" {
		t.Fatalf("plan = %+v", plan)
	}
	if len(files) != 2 || files[0] != filepath.Join(dir, "a.md") || files[1] != filepath.Join(dir, "sub", "b.md") {
		t.Fatalf("files = %v", files)
	}
}

func TestInventoryPatterns(t *testing.T) {
	dir := t.TempDir()
	for _, name := range []string{"a.md", "b.txt", "c.org", "sub/d.md"} {
		fixture(t, dir, name, "x")
	}
	for pattern, want := range map[string]int{"**/*.md": 2, "*.md": 1, "**/*.md,**/*.txt": 3, "**/*.{md,txt}": 3, "**/*.{md,org},*.txt": 4} {
		plan, _ := Inventory(Collection{Path: dir, Pattern: pattern}, "t")
		if plan.Files != want {
			t.Errorf("%s: files = %d, want %d", pattern, plan.Files, want)
		}
	}
}

func TestInventoryFlagsFilesOverTheDefaultLimit(t *testing.T) {
	dir := t.TempDir()
	fixture(t, dir, "fits.md", strings.Repeat("x", DefaultMaxDocumentBytes))
	fixture(t, dir, "over.md", strings.Repeat("x", DefaultMaxDocumentBytes+1))
	plan, _ := Inventory(Collection{Path: dir, Pattern: defaultPattern}, "t")
	if plan.OverLimit != 1 || plan.Files != 2 {
		t.Fatalf("plan = %+v", plan)
	}
}

func TestInventoryNamesWhatItCannotCount(t *testing.T) {
	dir := t.TempDir()
	for name, collection := range map[string]Collection{
		"its path is not absolute":    {Path: "relative/path", Pattern: defaultPattern},
		"its folder cannot be opened": {Path: filepath.Join(dir, "absent"), Pattern: defaultPattern},
	} {
		plan, files := Inventory(collection, "t")
		if plan.Problem != name || files != nil {
			t.Errorf("%s: plan = %+v", name, plan)
		}
	}
	for name, collection := range map[string]Collection{
		"a pattern":  {Path: dir, Pattern: "[unclosed"},
		"an extglob": {Path: dir, Pattern: "**/*.@(md|txt)"},
		"an ignore":  {Path: dir, Pattern: defaultPattern, Ignore: []string{"[unclosed"}},
	} {
		if plan, files := Inventory(collection, "t"); !strings.Contains(plan.Problem, "refused") || files != nil {
			t.Errorf("%s: plan = %+v", name, plan)
		}
	}
}

func TestLinksAreNeitherFollowedNorCounted(t *testing.T) {
	dir, outside := t.TempDir(), t.TempDir()
	fixture(t, outside, "secret.md", "outside")
	if err := os.Symlink(filepath.Join(outside, "secret.md"), filepath.Join(dir, "link.md")); err != nil {
		t.Skipf("this host cannot make links: %v", err)
	}
	if err := os.Symlink(outside, filepath.Join(dir, "linked")); err != nil {
		t.Skipf("this host cannot make links: %v", err)
	}
	plan, files := Inventory(Collection{Path: dir, Pattern: defaultPattern}, "t")
	if plan.Files != 0 || len(files) != 0 {
		t.Fatalf("plan = %+v, files = %v", plan, files)
	}
}

func TestInventoryRawCountsTranscripts(t *testing.T) {
	dir := t.TempDir()
	fixture(t, dir, "2026/01/one.jsonl", "12345")
	fixture(t, dir, "two.JSONL", "123")
	fixture(t, dir, "notes.md", "not a transcript")
	fixture(t, dir, ".hidden/three.jsonl", "hidden")
	raw := InventoryRaw(dir, "codex")
	if raw.Files != 2 || raw.Bytes != 8 || raw.Target != "codex" || raw.Problem != "" {
		t.Fatalf("raw = %+v", raw)
	}
	if raw := InventoryRaw(filepath.Join(dir, "absent"), "codex"); raw.Problem == "" {
		t.Fatal("a missing folder gave no problem")
	}
}

func TestParseCheck(t *testing.T) {
	good := "a build line\n" + `[{"files":2,"changed":1,"refused":0,"replacements":{"r":3},"filesByRule":{"r":1},"gitleaksRan":true,"gitleaksFindings":{}}]` + "\n"
	results, err := ParseCheck([]byte(good), 1)
	if err != nil || results[0].Files != 2 || results[0].Replacements["r"] != 3 || !results[0].Clean() {
		t.Fatalf("results = %+v, err = %v", results, err)
	}
	if _, err := ParseCheck([]byte(good), 2); err == nil {
		t.Fatal("a wrong number of results was accepted")
	}
	if _, err := ParseCheck([]byte("not json"), 1); err == nil {
		t.Fatal("bad output was accepted")
	}
	for name, result := range map[string]CheckResult{"refused": {Refused: 1}, "found": {GitleaksResults: map[string]int{"r": 1}}} {
		if result.Clean() {
			t.Errorf("%s reads as clean", name)
		}
	}
}
