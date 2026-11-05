package migrate

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func writeTestFile(t *testing.T, path, text string) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte(text), 0o600); err != nil {
		t.Fatal(err)
	}
}

// tempPaths is a temporary home with both clients, so nothing here can reach a real one.
func tempPaths(t *testing.T) ClientPaths {
	t.Helper()
	t.Setenv("XDG_CONFIG_HOME", t.TempDir())
	paths := PathsUnder(t.TempDir(), "", "")
	writeTestFile(t, paths.ClaudeConfig, `{"mcpServers":{"qmd":{"command":"qmd","args":["mcp"],"env":{"K":"hidden"}}}}`)
	writeTestFile(t, paths.CodexConfig, "[mcp_servers.qmd]\ncommand = \"qmd\"\n")
	return paths
}

func planned(t *testing.T, o CutoverOptions) []FileEdit {
	t.Helper()
	edits, _, err := PlanCutover(o)
	if err != nil {
		t.Fatal(err)
	}
	return edits
}

func TestPathsFollowEachClientsOwnSetting(t *testing.T) {
	home := filepath.Join("h")
	defaults := PathsUnder(home, "", "")
	if defaults.ClaudeConfig != filepath.Join(home, ".claude.json") || defaults.CodexConfig != filepath.Join(home, ".codex", "config.toml") || defaults.ClaudeSkills != filepath.Join(home, ".claude", "skills") {
		t.Fatalf("defaults = %+v", defaults)
	}
	own := PathsUnder(home, "c", "x")
	if own.ClaudeConfig != filepath.Join("c", ".claude.json") || own.ClaudeSettings != filepath.Join("c", "settings.json") || own.CodexSkills != filepath.Join("x", "skills") {
		t.Fatalf("own = %+v", own)
	}
}

func TestADiffShowsWhatAnEntryRunsAndNothingElse(t *testing.T) {
	edits := planned(t, CutoverOptions{Paths: tempPaths(t)})
	if len(edits) != 2 || !strings.Contains(edits[0].Summary[0], `command "qmd"`) || strings.Contains(strings.Join(edits[0].Summary, " "), "hidden") {
		t.Fatalf("edits = %+v", edits)
	}
	for raw, want := range map[string]string{
		`{"url":"https://x.test/mcp?key=hidden"}`: "url https://x.test/mcp",
		`{}`:       "no command",
		`not json`: "cannot read",
	} {
		if got := entrySummary([]byte(raw)); !strings.Contains(got, want) || strings.Contains(got, "hidden") {
			t.Errorf("%s = %q", raw, got)
		}
	}
}

func TestConfigsItCannotUseChangeNothing(t *testing.T) {
	paths := tempPaths(t)
	writeTestFile(t, paths.ClaudeConfig, `{"mcpServers":[]}`)
	if _, _, err := PlanCutover(CutoverOptions{Paths: paths}); err == nil || !strings.Contains(err.Error(), "not an object") {
		t.Fatalf("err = %v", err)
	}
	writeTestFile(t, paths.ClaudeConfig, `{}`)
	writeTestFile(t, paths.ClaudeSettings, "not json")
	if _, _, err := PlanCutover(CutoverOptions{Paths: paths, HookCommand: "h"}); err == nil || !strings.Contains(err.Error(), "not valid JSON") {
		t.Fatalf("err = %v", err)
	}
}

func TestWhatIsAlreadyDoneIsNotDoneAgain(t *testing.T) {
	paths := tempPaths(t)
	skill := []byte("skill")
	writeTestFile(t, paths.ClaudeConfig, `{"mcpServers":{"memoria":{"command":"memoria"}}}`)
	writeTestFile(t, paths.CodexConfig, "[mcp_servers.memoria]\ncommand = \"memoria\"\n")
	writeTestFile(t, paths.ClaudeSettings, `{"hooks":{"SessionEnd":[{"hooks":[{"type":"command","command":"h"}]}]}}`)
	writeTestFile(t, filepath.Join(paths.ClaudeSkills, "memoria", "SKILL.md"), "skill")
	edits, notes, err := PlanCutover(CutoverOptions{Paths: paths, Skill: skill, HookCommand: "h"})
	if err != nil {
		t.Fatal(err)
	}
	if len(edits) != 1 || !strings.HasSuffix(edits[0].Path, filepath.Join(".codex", "skills", "memoria", "SKILL.md")) {
		t.Fatalf("edits = %+v", edits)
	}
	if text := strings.Join(notes, "\n"); !strings.Contains(text, "already uses memoria") || !strings.Contains(text, "already there") || !strings.Contains(text, "already installed") {
		t.Fatalf("notes = %q", text)
	}
}

func TestConfigsWithNoQMDSayThat(t *testing.T) {
	paths := tempPaths(t)
	writeTestFile(t, paths.ClaudeConfig, `{}`)
	writeTestFile(t, paths.CodexConfig, "model = \"m\"\n")
	edits, notes, err := PlanCutover(CutoverOptions{Paths: paths})
	if err != nil || len(edits) != 0 || strings.Count(strings.Join(notes, "\n"), "no qmd entry") != 2 {
		t.Fatalf("edits = %+v, notes = %v, err = %v", edits, notes, err)
	}
}

func TestAnEntryAlreadyThereIsKeptWhenQMDGoes(t *testing.T) {
	paths := tempPaths(t)
	writeTestFile(t, paths.ClaudeConfig, `{"mcpServers":{"qmd":{"command":"qmd"},"memoria":{"command":"mine"}}}`)
	writeTestFile(t, paths.CodexConfig, "[mcp_servers.qmd]\ncommand = \"qmd\"\n[mcp_servers.memoria]\ncommand = \"mine\"\n")
	edits := planned(t, CutoverOptions{Paths: paths})
	if len(edits) != 2 || !strings.Contains(string(edits[0].After), "mine") || !strings.Contains(string(edits[1].After), "mine") || strings.Count(string(edits[1].After), "mcp_servers.memoria") != 1 {
		t.Fatalf("edits = %+v", edits)
	}
}

func TestACutoverAndRollbackRoundTrip(t *testing.T) {
	paths := tempPaths(t)
	claude := readTestFile(t, paths.ClaudeConfig)
	if err := ApplyCutover(planned(t, CutoverOptions{Paths: paths, Skill: []byte("skill"), HookCommand: "h"})); err != nil {
		t.Fatal(err)
	}
	manifest, err := ActiveManifest()
	if err != nil || manifest == nil || len(manifest.Entries) != 5 {
		t.Fatalf("manifest = %+v, err = %v", manifest, err)
	}
	rollBack(t, manifest, 5)
	if readTestFile(t, paths.ClaudeConfig) != claude {
		t.Fatal("not restored")
	}
	if _, err := os.Stat(paths.ClaudeSettings); !os.IsNotExist(err) {
		t.Fatalf("a created file stayed: %v", err)
	}
	if active, err := ActiveManifest(); err != nil || active != nil {
		t.Fatalf("active = %+v, err = %v", active, err)
	}
}

func rollBack(t *testing.T, manifest *CutoverManifest, want int) {
	t.Helper()
	steps, err := PlanRollback(manifest)
	if err != nil || len(steps) != want {
		t.Fatalf("steps = %+v, err = %v", steps, err)
	}
	if err := ApplyRollback(manifest, steps); err != nil {
		t.Fatal(err)
	}
}

func readTestFile(t *testing.T, path string) string {
	t.Helper()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	return string(data)
}

func TestAFileBackToItsOriginalNeedsNothing(t *testing.T) {
	paths := tempPaths(t)
	original := readTestFile(t, paths.CodexConfig)
	if err := ApplyCutover(planned(t, CutoverOptions{Paths: paths})); err != nil {
		t.Fatal(err)
	}
	writeTestFile(t, paths.CodexConfig, original)
	manifest, err := ActiveManifest()
	if err != nil {
		t.Fatal(err)
	}
	steps, err := PlanRollback(manifest)
	if err != nil || len(steps) != 1 || steps[0].Entry.Path != paths.ClaudeConfig || steps[0].Changed {
		t.Fatalf("steps = %+v, err = %v", steps, err)
	}
}

func TestAFileDeletedSinceCountsAsChanged(t *testing.T) {
	paths := tempPaths(t)
	if err := ApplyCutover(planned(t, CutoverOptions{Paths: paths})); err != nil {
		t.Fatal(err)
	}
	if err := os.Remove(paths.CodexConfig); err != nil {
		t.Fatal(err)
	}
	manifest, err := ActiveManifest()
	if err != nil {
		t.Fatal(err)
	}
	steps, err := PlanRollback(manifest)
	if err != nil || len(steps) != 2 || !steps[1].Changed {
		t.Fatalf("steps = %+v, err = %v", steps, err)
	}
}

func TestADamagedRecordChangesNothing(t *testing.T) {
	t.Setenv("XDG_CONFIG_HOME", t.TempDir())
	root, err := cutoverRoot()
	if err != nil {
		t.Fatal(err)
	}
	dir := filepath.Join(root, "1")
	if err := os.MkdirAll(dir, 0o700); err != nil {
		t.Fatal(err)
	}
	manifest := CutoverManifest{dir: dir}
	if err := manifest.save(); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, manifestName), []byte("not json"), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := ActiveManifest(); err == nil || !strings.Contains(err.Error(), "damaged") {
		t.Fatalf("err = %v", err)
	}
}

func TestAMissingBackupStopsARollback(t *testing.T) {
	paths := tempPaths(t)
	if err := ApplyCutover(planned(t, CutoverOptions{Paths: paths})); err != nil {
		t.Fatal(err)
	}
	manifest, err := ActiveManifest()
	if err != nil {
		t.Fatal(err)
	}
	if err := os.Remove(filepath.Join(manifest.dir, manifest.Entries[0].Backup)); err != nil {
		t.Fatal(err)
	}
	if _, err := PlanRollback(manifest); err == nil || !strings.Contains(err.Error(), "cannot be read") {
		t.Fatalf("err = %v", err)
	}
}

func TestAWriteKeepsAFilesModeAndLeavesNoTemporaryFile(t *testing.T) {
	path := filepath.Join(t.TempDir(), "deep", "file")
	if err := writeAtomic(path, []byte("one")); err != nil {
		t.Fatal(err)
	}
	if err := os.Chmod(path, 0o600); err != nil {
		t.Fatal(err)
	}
	if err := writeAtomic(path, []byte("two")); err != nil {
		t.Fatal(err)
	}
	info, err := os.Stat(path)
	if err != nil || readTestFile(t, path) != "two" {
		t.Fatalf("info = %v, err = %v", info, err)
	}
	if entries, err := os.ReadDir(filepath.Dir(path)); err != nil || len(entries) != 1 {
		t.Fatalf("entries = %v, err = %v", entries, err)
	}
}
