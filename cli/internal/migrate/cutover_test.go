package migrate

import (
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"runtime"
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

func readTestFile(t *testing.T, path string) string {
	t.Helper()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	return string(data)
}

// tempPaths is a temporary home with the owner's layout, so nothing here can reach a real one.
func tempPaths(t *testing.T) ClientPaths {
	t.Helper()
	t.Setenv("XDG_CONFIG_HOME", t.TempDir())
	paths := PathsUnder(t.TempDir(), "", "")
	writeTestFile(t, paths.MCPConfig, `{"mcpServers":{"qmd":{"command":"/opt/qmd","args":["mcp","--api-key","hidden"],"env":{"K":"hidden"}}}}`)
	writeTestFile(t, paths.ClaudeConfig, `{"zeta":1,"mcpServers":{"other":{"command":"other"}},"alpha":2}`)
	writeTestFile(t, paths.CodexConfig, "[mcp_servers.other]\ncommand = \"other\"\n")
	return paths
}

func planned(t *testing.T, o CutoverOptions) *CutoverPlan {
	t.Helper()
	plan, err := PlanCutover(o)
	if err != nil {
		t.Fatal(err)
	}
	return plan
}

func activeOf(t *testing.T, paths ClientPaths) *CutoverManifest {
	t.Helper()
	manifest, err := ActiveManifest(paths.Home)
	if err != nil || manifest == nil {
		t.Fatalf("manifest = %+v, err = %v", manifest, err)
	}
	return manifest
}

func applied(t *testing.T, o CutoverOptions) {
	t.Helper()
	if err := ApplyCutover(planned(t, o).Edits); err != nil {
		t.Fatal(err)
	}
}

func rollBack(t *testing.T, paths ClientPaths) []RollbackStep {
	t.Helper()
	manifest := activeOf(t, paths)
	steps, err := PlanRollback(manifest)
	if err != nil {
		t.Fatal(err)
	}
	if err := ApplyRollback(manifest, steps); err != nil {
		t.Fatal(err)
	}
	return steps
}

func TestPathsFollowEachClientsOwnSetting(t *testing.T) {
	home := filepath.Join("h")
	defaults := PathsUnder(home, "", "")
	if defaults.MCPConfig != filepath.Join(home, ".mcp.json") || defaults.ClaudeConfig != filepath.Join(home, ".claude.json") || defaults.CodexConfig != filepath.Join(home, ".codex", "config.toml") || defaults.AgentsSkills != filepath.Join(home, ".agents", "skills") {
		t.Fatalf("defaults = %+v", defaults)
	}
	own := PathsUnder(home, "c", "x")
	if own.ClaudeConfig != filepath.Join("c", ".claude.json") || own.ClaudeLocalSettings != filepath.Join("c", "settings.local.json") || own.CodexSkills != filepath.Join("x", "skills") {
		t.Fatalf("own = %+v", own)
	}
}

func TestQMDIsSwappedWhereItIsDefinedAndNowhereElse(t *testing.T) {
	paths := tempPaths(t)
	claude := readTestFile(t, paths.ClaudeConfig)
	codex := readTestFile(t, paths.CodexConfig)
	plan := planned(t, CutoverOptions{Paths: paths})
	if !plan.Found || len(plan.Edits) != 1 || plan.Edits[0].Path != paths.MCPConfig {
		t.Fatalf("plan = %+v", plan)
	}
	if err := ApplyCutover(plan.Edits); err != nil {
		t.Fatal(err)
	}
	text := readTestFile(t, paths.MCPConfig)
	if strings.Contains(text, `"qmd"`) || !strings.Contains(text, `"memoria"`) {
		t.Fatalf(".mcp.json = %s", text)
	}
	if readTestFile(t, paths.ClaudeConfig) != claude || readTestFile(t, paths.CodexConfig) != codex {
		t.Fatal("a file without qmd changed")
	}
	if notes := strings.Join(plan.Notes, "\n"); !strings.Contains(notes, "memoria was not added there") {
		t.Fatalf("notes = %q", notes)
	}
}

func TestQMDIsSwappedInEveryPlaceItIsDefined(t *testing.T) {
	paths := tempPaths(t)
	writeTestFile(t, paths.ClaudeConfig, `{"mcpServers":{"qmd":{"command":"qmd"}}}`)
	writeTestFile(t, paths.CodexConfig, "[mcp_servers.qmd]\ncommand = \"qmd\"\n")
	if plan := planned(t, CutoverOptions{Paths: paths}); len(plan.Edits) != 3 {
		t.Fatalf("plan = %+v", plan)
	}
}

func TestNoQMDAnywhereIsFoundFalse(t *testing.T) {
	paths := tempPaths(t)
	writeTestFile(t, paths.MCPConfig, `{"mcpServers":{}}`)
	if plan := planned(t, CutoverOptions{Paths: paths}); plan.Found || len(plan.Searched) != 3 {
		t.Fatalf("plan = %+v", plan)
	}
}

func TestADiffShowsNoArgumentOrValue(t *testing.T) {
	plan := planned(t, CutoverOptions{Paths: tempPaths(t)})
	summary := strings.Join(plan.Edits[0].Summary, " ")
	if !strings.Contains(summary, `runs "qmd"`) || strings.Contains(summary, "hidden") || strings.Contains(summary, "api-key") || strings.Contains(summary, "/opt") {
		t.Fatalf("summary = %q", summary)
	}
	for raw, want := range map[string]string{
		`{"url":"https://user:hidden@x.test/mcp?key=hidden"}`: "x.test",
		`{}`:       "not shown",
		`nonsense`: "cannot read",
	} {
		if got := entrySummary(json.RawMessage(raw)); !strings.Contains(got, want) || strings.Contains(got, "hidden") {
			t.Errorf("%s = %q", raw, got)
		}
	}
}

func TestAHookIsShownByNameAndNotAdded(t *testing.T) {
	paths := tempPaths(t)
	settings := `{"hooks":{"SessionEnd":[{"hooks":[{"type":"command","command":"python3 /secret/place/session-end-export.py --token hidden"}]}]}}`
	writeTestFile(t, paths.ClaudeSettings, settings)
	plan := planned(t, CutoverOptions{Paths: paths})
	notes := strings.Join(plan.Notes, "\n")
	if !strings.Contains(notes, "python3 session-end-export.py") || strings.Contains(notes, "hidden") || strings.Contains(notes, "secret") {
		t.Fatalf("notes = %q", notes)
	}
	if len(plan.Edits) != 1 {
		t.Fatalf("settings would change: %+v", plan.Edits)
	}
	writeTestFile(t, paths.ClaudeSettings, `{}`)
	if notes := strings.Join(planned(t, CutoverOptions{Paths: paths}).Notes, "\n"); !strings.Contains(notes, "no SessionEnd hook") {
		t.Fatalf("notes = %q", notes)
	}
	writeTestFile(t, paths.ClaudeSettings, `{"hooks":[]}`)
	if notes := strings.Join(planned(t, CutoverOptions{Paths: paths}).Notes, "\n"); !strings.Contains(notes, "could not be read") {
		t.Fatalf("notes = %q", notes)
	}
}

func TestMemoriasToolsAreAllowedWhereQMDsWere(t *testing.T) {
	paths := tempPaths(t)
	writeTestFile(t, paths.ClaudeLocalSettings, `{"z":1,"permissions":{"allow":["Read","mcp__qmd__query","mcp__qmd__get","mcp__qmd__multi_get","mcp__qmd__status","mcp__qmd__*","mcp__qmd","Bash(qmd:*)","mcp__qmd__unknown"]},"enabledMcpjsonServers":["qmd"],"a":2}`)
	plan := planned(t, CutoverOptions{Paths: paths})
	if len(plan.Edits) != 2 {
		t.Fatalf("plan = %+v", plan.Edits)
	}
	after := string(plan.Edits[1].After)
	for _, want := range []string{"mcp__memoria__search", "mcp__memoria__get_document\"", "mcp__memoria__get_documents", "mcp__memoria__status", "mcp__memoria__*", `"mcp__memoria"`, `"memoria"`, "mcp__qmd__query", "Bash(qmd:*)"} {
		if !strings.Contains(after, want) {
			t.Errorf("%q is missing from %s", want, after)
		}
	}
	if strings.Index(after, `"z"`) > strings.Index(after, `"a"`) {
		t.Fatalf("the keys were reordered: %s", after)
	}
	if notes := strings.Join(plan.Notes, "\n"); !strings.Contains(notes, "8 rule(s) naming qmd were left") {
		t.Fatalf("notes = %q", notes)
	}
	applied(t, CutoverOptions{Paths: paths})
	if plan := planned(t, CutoverOptions{Paths: paths}); len(plan.Edits) != 0 {
		t.Fatalf("a second plan would change %+v", plan.Edits)
	}
}

func TestBadShapesAreRefusedAndNeverReplaced(t *testing.T) {
	paths := tempPaths(t)
	for name, text := range map[string]string{
		"null":               `null`,
		"a list":             `[]`,
		"text after":         `{"a":1} trailing`,
		"second value":       `{"a":1}{"b":2}`,
		"permissions a list": `{"permissions":[]}`,
		"allow a string":     `{"permissions":{"allow":"mcp__qmd"}}`,
		"enabled a string":   `{"enabledMcpjsonServers":"qmd"}`,
		"empty":              ``,
	} {
		writeTestFile(t, paths.ClaudeLocalSettings, text)
		if _, err := PlanCutover(CutoverOptions{Paths: paths}); err == nil || !strings.Contains(err.Error(), "nothing was changed") {
			t.Errorf("%s: err = %v", name, err)
		}
	}
	writeTestFile(t, paths.ClaudeLocalSettings, `{}`)
	for name, text := range map[string]string{
		"null": `null`, "mcpServers a list": `{"mcpServers":[]}`, "text after": `{"mcpServers":{}} x`,
	} {
		writeTestFile(t, paths.MCPConfig, text)
		if _, err := PlanCutover(CutoverOptions{Paths: paths}); err == nil {
			t.Errorf("%s was accepted", name)
		}
	}
}

func TestMalformedTOMLIsRefusedBeforeAnyEdit(t *testing.T) {
	paths := tempPaths(t)
	for _, text := range []string{"[mcp_servers.qmd\ncommand = \"qmd\"\n", "a = = 1\n", "mcp_servers = 3\n"} {
		writeTestFile(t, paths.CodexConfig, text)
		if _, err := PlanCutover(CutoverOptions{Paths: paths}); err == nil || !strings.Contains(err.Error(), "nothing was changed") {
			t.Errorf("%q: err = %v", text, err)
		}
	}
}

func TestTOMLHeadersAreReadAsTOMLReadsThem(t *testing.T) {
	for line, want := range map[string]string{
		"[mcp_servers.qmd]":            "mcp_servers qmd",
		"  [mcp_servers.qmd] # a note": "mcp_servers qmd",
		"[ mcp_servers . \"qmd\" ]":    "mcp_servers qmd",
		"[mcp_servers.'qmd'.env]":      "mcp_servers qmd env",
		"[[things.list]] # note":       "things list",
		"key = 1":                      "",
		"[unterminated":                "",
		"[a] trailing":                 "",
		"[\"unterminated]":             "",
		"[]":                           "",
		"":                             "",
	} {
		keys, ok := tableHeader(line)
		if got := strings.Join(keys, " "); (ok && got != want) || (!ok && want != "") {
			t.Errorf("%q = %q %v, want %q", line, got, ok, want)
		}
	}
}

func TestATableAfterQMDSurvivesWithItsComment(t *testing.T) {
	paths := tempPaths(t)
	text := "model = \"m\"\n\n[mcp_servers.qmd] # local search\ncommand = \"qmd\"\n[mcp_servers.qmd.env]\nK = \"hidden\"\n\n# about other\n[other] # note\nkey = 1\n"
	writeTestFile(t, paths.CodexConfig, text)
	plan := planned(t, CutoverOptions{Paths: paths})
	after := string(plan.Edits[1].After)
	if strings.Contains(after, "hidden") || strings.Contains(after, "mcp_servers.qmd") {
		t.Fatalf("qmd stayed: %s", after)
	}
	for _, want := range []string{"model = \"m\"", "# about other", "[other] # note", "key = 1", "[mcp_servers.memoria]"} {
		if !strings.Contains(after, want) {
			t.Errorf("%q is missing from %s", want, after)
		}
	}
	if summary := strings.Join(plan.Edits[1].Summary, " "); strings.Contains(summary, "hidden") {
		t.Fatalf("summary = %q", summary)
	}
}

func TestFormsItCannotEditSafelyAreRefused(t *testing.T) {
	paths := tempPaths(t)
	for name, text := range map[string]string{
		"dotted":                           "[mcp_servers]\nqmd.command = \"qmd\"\n",
		"inline":                           "[mcp_servers]\nqmd = { command = \"qmd\" }\n",
		"top":                              "mcp_servers.qmd.command = \"qmd\"\n",
		"a string that looks like a table": "[mcp_servers.qmd]\ncommand = \"qmd\"\nnote = \"\"\"\n[other]\n\"\"\"\n",
	} {
		writeTestFile(t, paths.CodexConfig, text)
		if _, err := PlanCutover(CutoverOptions{Paths: paths}); err == nil || !strings.Contains(err.Error(), "cannot edit safely") {
			t.Errorf("%s: err = %v", name, err)
		}
	}
}

func TestLineEndingsAndOtherBytesStayInCodexConfig(t *testing.T) {
	paths := tempPaths(t)
	writeTestFile(t, paths.CodexConfig, "a   =   1 # spaced\r\n[mcp_servers.qmd]\r\ncommand = \"qmd\"\r\n[z]\r\nb=2\r\n")
	after := string(planned(t, CutoverOptions{Paths: paths}).Edits[1].After)
	if !strings.HasPrefix(after, "a   =   1 # spaced\r\n[z]\r\nb=2\r\n") || strings.Contains(strings.ReplaceAll(after, "\r\n", ""), "\n") {
		t.Fatalf("after = %q", after)
	}
}

func TestProjectEntriesAreSaidAndLeftAlone(t *testing.T) {
	paths := tempPaths(t)
	writeTestFile(t, paths.ClaudeConfig, `{"projects":{"/p":{"mcpServers":{"qmd":{}}},"/q":{},"/r":[]},"mcpServers":{}}`)
	notes := strings.Join(planned(t, CutoverOptions{Paths: paths}).Notes, "\n")
	if !strings.Contains(notes, "1 project(s) name qmd") {
		t.Fatalf("notes = %q", notes)
	}
}

func TestAnExistingMemoriaIsKept(t *testing.T) {
	paths := tempPaths(t)
	writeTestFile(t, paths.MCPConfig, `{"mcpServers":{"qmd":{"command":"qmd"},"memoria":{"command":"mine"}}}`)
	writeTestFile(t, paths.CodexConfig, "[mcp_servers.qmd]\ncommand = \"qmd\"\n[mcp_servers.memoria]\ncommand = \"mine\"\n")
	edits := planned(t, CutoverOptions{Paths: paths}).Edits
	if len(edits) != 2 || !strings.Contains(string(edits[0].After), "mine") || edits[0].AddedServer || edits[1].AddedServer || strings.Count(string(edits[1].After), "mcp_servers.memoria") != 1 {
		t.Fatalf("edits = %+v", edits)
	}
}

func makeFolders(t *testing.T, folders ...string) {
	t.Helper()
	for _, folder := range folders {
		if err := os.MkdirAll(folder, 0o700); err != nil {
			t.Fatal(err)
		}
	}
}

func editedPaths(plan *CutoverPlan) string {
	var written []string
	for _, edit := range plan.Edits {
		written = append(written, edit.Path)
	}
	return strings.Join(written, "\n")
}

func TestSkillsGoToEveryRealFolderAndThroughNoLink(t *testing.T) {
	paths := tempPaths(t)
	repo := t.TempDir()
	makeFolders(t, paths.AgentsSkills, filepath.Dir(paths.CodexSkills))
	if err := os.Symlink(repo, paths.CodexSkills); err != nil {
		t.Skipf("this host cannot make links: %v", err)
	}
	plan := planned(t, CutoverOptions{Paths: paths, Skill: []byte("skill")})
	text := editedPaths(plan)
	if !strings.Contains(text, filepath.Join(paths.ClaudeSkills, "memoria")) || !strings.Contains(text, filepath.Join(paths.AgentsSkills, "memoria")) || strings.Contains(text, paths.CodexSkills) {
		t.Fatalf("written = %q", text)
	}
	if notes := strings.Join(plan.Notes, "\n"); !strings.Contains(notes, "is a link") {
		t.Fatalf("notes = %q", notes)
	}
	if err := ApplyCutover(plan.Edits); err != nil {
		t.Fatal(err)
	}
	if entries, err := os.ReadDir(repo); err != nil || len(entries) != 0 {
		t.Fatalf("it wrote through the link: %v, %v", entries, err)
	}
}

func TestALinkedOrReadOnlyConfigIsRefused(t *testing.T) {
	paths := tempPaths(t)
	target := filepath.Join(t.TempDir(), "real.json")
	writeTestFile(t, target, readTestFile(t, paths.MCPConfig))
	if err := os.Remove(paths.MCPConfig); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(target, paths.MCPConfig); err != nil {
		writeTestFile(t, paths.MCPConfig, readTestFile(t, target))
	} else if _, err := PlanCutover(CutoverOptions{Paths: paths}); err == nil || !strings.Contains(err.Error(), "is a link") {
		t.Fatalf("err = %v", err)
	} else {
		if err := os.Remove(paths.MCPConfig); err != nil {
			t.Fatal(err)
		}
		writeTestFile(t, paths.MCPConfig, readTestFile(t, target))
	}
	if err := os.Chmod(paths.MCPConfig, 0o400); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = os.Chmod(paths.MCPConfig, 0o600) })
	if _, err := PlanCutover(CutoverOptions{Paths: paths}); err == nil || !strings.Contains(err.Error(), "read-only") {
		t.Fatalf("err = %v", err)
	}
}

func TestLinkedSettingsAreLeftAloneAndSaid(t *testing.T) {
	paths := tempPaths(t)
	target := filepath.Join(t.TempDir(), "settings.json")
	text := `{"permissions":{"allow":["mcp__qmd__*"]}}`
	writeTestFile(t, target, text)
	if err := os.MkdirAll(filepath.Dir(paths.ClaudeLocalSettings), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(target, paths.ClaudeLocalSettings); err != nil {
		t.Skipf("this host cannot make links: %v", err)
	}
	plan := planned(t, CutoverOptions{Paths: paths})
	if len(plan.Edits) != 1 || !strings.Contains(strings.Join(plan.Notes, "\n"), "is a link, so it was left alone") {
		t.Fatalf("plan = %+v", plan)
	}
	if err := ApplyCutover(plan.Edits); err != nil {
		t.Fatal(err)
	}
	if readTestFile(t, target) != text {
		t.Fatal("it wrote through the link")
	}
}

func TestAFileChangedAfterItWasShownStopsTheChange(t *testing.T) {
	paths := tempPaths(t)
	plan := planned(t, CutoverOptions{Paths: paths})
	writeTestFile(t, paths.MCPConfig, `{"mcpServers":{"qmd":{}},"written":"by the client"}`)
	if err := ApplyCutover(plan.Edits); err == nil || !strings.Contains(err.Error(), "changed after it was shown") {
		t.Fatalf("err = %v", err)
	}
	if !strings.Contains(readTestFile(t, paths.MCPConfig), "by the client") {
		t.Fatal("what the client wrote was lost")
	}
	if manifest, err := ActiveManifest(paths.Home); err != nil || manifest != nil {
		t.Fatalf("a backup record was made: %+v, %v", manifest, err)
	}
}

func TestARollbackPutsEverythingBack(t *testing.T) {
	paths := tempPaths(t)
	mcp := readTestFile(t, paths.MCPConfig)
	local := `{"permissions":{"allow":["mcp__qmd__*"]},"enabledMcpjsonServers":["qmd"]}`
	writeTestFile(t, paths.ClaudeLocalSettings, local)
	applied(t, CutoverOptions{Paths: paths, Skill: []byte("skill")})
	skill := filepath.Join(paths.ClaudeSkills, "memoria", "SKILL.md")
	if readTestFile(t, skill) != "skill" {
		t.Fatal("the skill was not installed")
	}
	if steps := rollBack(t, paths); len(steps) != 4 {
		t.Fatalf("%d steps", len(steps))
	}
	if readTestFile(t, paths.MCPConfig) != mcp || readTestFile(t, paths.ClaudeLocalSettings) != local {
		t.Fatal("not restored")
	}
	for _, folder := range []string{paths.ClaudeSkills, paths.CodexSkills} {
		if _, err := os.Stat(folder); !os.IsNotExist(err) {
			t.Fatalf("a folder the cutover made stayed: %v", err)
		}
	}
	if active, err := ActiveManifest(paths.Home); err != nil || active != nil {
		t.Fatalf("active = %+v, err = %v", active, err)
	}
}

func TestAFolderWithSomethingElseInItStays(t *testing.T) {
	paths := tempPaths(t)
	applied(t, CutoverOptions{Paths: paths, Skill: []byte("skill")})
	mine := filepath.Join(paths.ClaudeSkills, "another", "SKILL.md")
	writeTestFile(t, mine, "another")
	rollBack(t, paths)
	if readTestFile(t, mine) != "another" {
		t.Fatal("something else was removed")
	}
}

func TestARollbackKeepsWhatAClientWroteSince(t *testing.T) {
	paths := tempPaths(t)
	writeTestFile(t, paths.ClaudeConfig, `{"numStartups":1,"mcpServers":{"qmd":{"command":"qmd","env":{"K":"v"}}}}`)
	writeTestFile(t, paths.ClaudeLocalSettings, `{"permissions":{"allow":["mcp__qmd__*"]},"enabledMcpjsonServers":["qmd"]}`)
	writeTestFile(t, paths.CodexConfig, "[mcp_servers.qmd]\ncommand = \"qmd\"\n[keep]\na = 1\n")
	applied(t, CutoverOptions{Paths: paths})
	writeTestFile(t, paths.ClaudeConfig, strings.Replace(readTestFile(t, paths.ClaudeConfig), `"numStartups": 1`, `"numStartups": 99`, 1))
	writeTestFile(t, paths.ClaudeLocalSettings, strings.Replace(readTestFile(t, paths.ClaudeLocalSettings), `"mcp__qmd__*"`, `"mcp__qmd__*", "Added"`, 1))
	writeTestFile(t, paths.CodexConfig, readTestFile(t, paths.CodexConfig)+"\n[later]\nb = 2\n")
	steps := rollBack(t, paths)
	for _, step := range steps {
		if step.Action != UndoOwnChange && step.Entry.Path != paths.MCPConfig {
			t.Errorf("%s: action %d", step.Entry.Path, step.Action)
		}
	}
	checkKeptAndUndone(t, paths)
}

func checkKeptAndUndone(t *testing.T, paths ClientPaths) {
	t.Helper()
	claude := readTestFile(t, paths.ClaudeConfig)
	if !strings.Contains(claude, "99") || !strings.Contains(claude, `"qmd"`) || !strings.Contains(claude, `"K"`) || strings.Contains(claude, "memoria") {
		t.Fatalf("claude = %s", claude)
	}
	local := readTestFile(t, paths.ClaudeLocalSettings)
	if !strings.Contains(local, "Added") || strings.Contains(local, "memoria") {
		t.Fatalf("local = %s", local)
	}
	codex := readTestFile(t, paths.CodexConfig)
	if !strings.Contains(codex, "[later]") || !strings.Contains(codex, "[mcp_servers.qmd]") || strings.Contains(codex, "memoria") {
		t.Fatalf("codex = %q", codex)
	}
}

func TestWhatCannotBeUndoneAloneIsFlagged(t *testing.T) {
	paths := tempPaths(t)
	applied(t, CutoverOptions{Paths: paths, Skill: []byte("skill")})
	writeTestFile(t, filepath.Join(paths.ClaudeSkills, "memoria", "SKILL.md"), "edited")
	writeTestFile(t, paths.MCPConfig, "not json any more")
	steps, err := PlanRollback(activeOf(t, paths))
	if err != nil {
		t.Fatal(err)
	}
	flagged := 0
	for _, step := range steps {
		if step.Changed {
			flagged++
		}
	}
	if flagged != 2 {
		t.Fatalf("steps = %+v", steps)
	}
}

func TestAFileDeletedSinceCountsAsChanged(t *testing.T) {
	paths := tempPaths(t)
	applied(t, CutoverOptions{Paths: paths})
	if err := os.Remove(paths.MCPConfig); err != nil {
		t.Fatal(err)
	}
	steps, err := PlanRollback(activeOf(t, paths))
	if err != nil || len(steps) != 1 || !steps[0].Changed {
		t.Fatalf("steps = %+v, err = %v", steps, err)
	}
}

func TestASecondCutoverKeepsTheOriginalAndNotesAnEditBetween(t *testing.T) {
	paths := tempPaths(t)
	original := readTestFile(t, paths.MCPConfig)
	applied(t, CutoverOptions{Paths: paths})
	// Something brings qmd back and changes the file, and a second cutover swaps it again.
	writeTestFile(t, paths.MCPConfig, `{"between":"an edit","mcpServers":{"qmd":{"command":"qmd"}}}`)
	applied(t, CutoverOptions{Paths: paths})
	manifest := activeOf(t, paths)
	if len(manifest.Entries) != 1 || !manifest.Entries[0].EditedSince {
		t.Fatalf("entries = %+v", manifest.Entries)
	}
	backup, err := os.ReadFile(filepath.Join(manifest.dir, manifest.Entries[0].Backup))
	if err != nil || string(backup) != original {
		t.Fatalf("the original backup changed: %q, %v", backup, err)
	}
	steps := rollBack(t, paths)
	text := readTestFile(t, paths.MCPConfig)
	if steps[0].Action != UndoOwnChange || !strings.Contains(text, "an edit") || !strings.Contains(text, `"qmd"`) {
		t.Fatalf("steps = %+v, text = %s", steps, text)
	}
}

func TestARollbackStopsIfAFileChangesAfterItWasShown(t *testing.T) {
	paths := tempPaths(t)
	applied(t, CutoverOptions{Paths: paths})
	manifest := activeOf(t, paths)
	steps, err := PlanRollback(manifest)
	if err != nil {
		t.Fatal(err)
	}
	writeTestFile(t, paths.MCPConfig, `{"just":"written"}`)
	if err := ApplyRollback(manifest, steps); err == nil || !strings.Contains(err.Error(), "changed after it was shown") {
		t.Fatalf("err = %v", err)
	}
	if !strings.Contains(readTestFile(t, paths.MCPConfig), "just") {
		t.Fatal("what was written was lost")
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
	if _, err := ActiveManifest("h"); err == nil || !strings.Contains(err.Error(), "damaged") {
		t.Fatalf("err = %v", err)
	}
}

func TestAMissingBackupStopsARollback(t *testing.T) {
	paths := tempPaths(t)
	applied(t, CutoverOptions{Paths: paths})
	manifest := activeOf(t, paths)
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
	if err := os.Chmod(path, 0o640); err != nil {
		t.Fatal(err)
	}
	if err := writeAtomic(path, []byte("two")); err != nil {
		t.Fatal(err)
	}
	info, err := os.Stat(path)
	if err != nil || readTestFile(t, path) != "two" {
		t.Fatalf("info = %v, err = %v", info, err)
	}
	if runtime.GOOS != "windows" && info.Mode().Perm() != 0o640 {
		t.Fatalf("mode = %v", info.Mode())
	}
	if entries, err := os.ReadDir(filepath.Dir(path)); err != nil || len(entries) != 1 {
		t.Fatalf("entries = %v, err = %v", entries, err)
	}
}

// A write that fails stops before the rename, so a cut-off file can never land on the real config.
func TestAFailedWriteLeavesTheFileAndNoTemporaryFile(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "config")
	writeTestFile(t, path, "the real config")
	real := fillFile
	t.Cleanup(func() { fillFile = real })
	fillFile = func(temp *os.File, _ string, _ []byte, _ os.FileMode) error {
		_, _ = temp.WriteString("cut o")
		_ = temp.Close()
		return errors.New("the disk is full")
	}
	if err := writeAtomic(path, []byte("the whole new text")); err == nil {
		t.Fatal("a failed write reported success")
	}
	if readTestFile(t, path) != "the real config" {
		t.Fatal("a cut-off file replaced the config")
	}
	if entries, err := os.ReadDir(dir); err != nil || len(entries) != 1 {
		t.Fatalf("a temporary file stayed: %v, %v", entries, err)
	}
}

type failingFile struct{ write, sync, closing error }

func (f failingFile) Write(data []byte) (int, error) { return len(data), f.write }
func (f failingFile) Sync() error                    { return f.sync }
func (f failingFile) Close() error                   { return f.closing }

func TestEachStepOfAWriteCanStopIt(t *testing.T) {
	full := errors.New("the disk is full")
	for name, file := range map[string]failingFile{
		"write": {write: full}, "sync": {sync: full}, "close": {closing: full},
	} {
		if err := fillTemp(file, filepath.Join(t.TempDir(), "absent"), nil, 0o600); !errors.Is(err, full) {
			t.Errorf("%s: err = %v", name, err)
		}
	}
}

func TestAMissingTemporaryFileStopsAWrite(t *testing.T) {
	if err := fillTemp(failingFile{}, filepath.Join(t.TempDir(), "absent"), nil, 0o600); err == nil {
		t.Fatal("setting the mode of a missing file succeeded")
	}
}

func TestParseObjectKeepsOrderAndExactValues(t *testing.T) {
	object, err := parseObject([]byte(`{"z":12345678901234567890,"a":"<b>&","z2":{"y":1,"x":2}}`))
	if err != nil {
		t.Fatal(err)
	}
	text, err := object.file()
	if err != nil {
		t.Fatal(err)
	}
	out := string(text)
	if !strings.Contains(out, "12345678901234567890") || !strings.Contains(out, "<b>&") || strings.Index(out, `"z"`) > strings.Index(out, `"a"`) || strings.Index(out, `"y"`) > strings.Index(out, `"x"`) {
		t.Fatalf("out = %s", out)
	}
}
