package cmd

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
)

const claudeConfigWithQMD = `{
  "numStartups": 12345678901234567890,
  "oauthAccount": {"emailAddress": "someone@example.test"},
  "mcpServers": {
    "qmd": {"command": "qmd", "args": ["mcp"], "env": {"SECRET_VALUE": "do-not-show-me"}},
    "other": {"command": "other", "url": "https://x.test/a?b=<c>&d"}
  }
}
`

const codexConfigWithQMD = "model = \"m\"\n\n[mcp_servers.qmd]\ncommand = \"qmd\"\nargs = [\"mcp\"]\n\n[mcp_servers.qmd.env]\nSECRET_VALUE = \"do-not-show-me\"\n\n[mcp_servers.other]\ncommand = \"other\"\n"

type cutoverHome struct {
	h                                      *harness
	home                                   string
	claude, codex, settings, skill, source string
}

// newCutoverHome is a temporary home with both clients and a local QMD, so nothing here can reach a real one.
func newCutoverHome(t *testing.T) *cutoverHome {
	t.Helper()
	home := t.TempDir()
	t.Setenv("HOME", home)
	t.Setenv("USERPROFILE", home)
	c := &cutoverHome{
		h: newHarness(t), home: home,
		claude: filepath.Join(home, ".claude.json"), codex: filepath.Join(home, ".codex", "config.toml"),
		settings: filepath.Join(home, ".claude", "settings.json"), skill: filepath.Join(home, ".claude", "skills", "memoria", "SKILL.md"),
		source: filepath.Join(t.TempDir(), "SKILL.md"),
	}
	writeFile(t, c.claude, claudeConfigWithQMD)
	writeFile(t, c.codex, codexConfigWithQMD)
	writeFile(t, c.settings, `{"theme": "dark"}`)
	writeFile(t, c.source, "the skill\n")
	writeFile(t, filepath.Join(home, ".config", "qmd", "index.yml"), "collections: {}\n")
	writeFile(t, filepath.Join(home, ".cache", "qmd", "index.sqlite"), "an index")
	return c
}

func (c *cutoverHome) cutover(extra ...string) int {
	return c.h.run(append([]string{"migrate", "cutover", "--skill", c.source, "--hook-command", "push-hook"}, extra...)...)
}

func readText(t *testing.T, path string) string {
	t.Helper()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	return string(data)
}

func checkOriginals(t *testing.T, c *cutoverHome) {
	t.Helper()
	if readText(t, c.claude) != claudeConfigWithQMD || readText(t, c.codex) != codexConfigWithQMD || readText(t, c.settings) != `{"theme": "dark"}` {
		t.Fatal("a client file is not its original")
	}
	if _, err := os.Stat(c.skill); !os.IsNotExist(err) {
		t.Fatalf("the skill is still there: %v", err)
	}
}

func TestCutoverIsDryByDefaultAndShowsTheChange(t *testing.T) {
	c := newCutoverHome(t)
	if code := c.cutover(); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, c.h.stderr.String())
	}
	out := c.h.stdout.String()
	for _, want := range []string{c.claude, "- mcpServers.qmd", "+ mcpServers.memoria", c.codex, "- [mcp_servers.qmd]", "+ hooks.SessionEnd", "+ the memoria skill", "Nothing was changed"} {
		if !strings.Contains(out, want) {
			t.Errorf("%q is missing from %q", want, out)
		}
	}
	if strings.Contains(out, "do-not-show-me") || strings.Contains(out, "someone@example.test") {
		t.Fatalf("a value reached the output: %q", out)
	}
	checkOriginals(t, c)
}

func TestCutoverAsksFirstAndRefusesWithNoOneToAsk(t *testing.T) {
	c := newCutoverHome(t)
	if code := c.cutover("--apply"); code != apperr.ExitUsage {
		t.Fatalf("no terminal: code = %d", code)
	}
	checkOriginals(t, c)
	c.h.terminal, c.h.stdin = true, strings.NewReader("no\n")
	if code := c.cutover("--apply"); code != apperr.ExitGeneric {
		t.Fatalf("declined: code = %d", code)
	}
	checkOriginals(t, c)
	c.h.stdin = strings.NewReader("yes\n")
	if code := c.cutover("--apply"); code != 0 || !strings.Contains(readText(t, c.claude), "memoria") {
		t.Fatalf("agreed: code = %d, stderr = %q", code, c.h.stderr.String())
	}
}

func TestCutoverSwapsTheEntriesAndKeepsTheRest(t *testing.T) {
	c := newCutoverHome(t)
	if code := c.cutover("--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, c.h.stderr.String())
	}
	checkClaudeAfterCutover(t, c)
	checkOthersAfterCutover(t, c)
}

func checkOthersAfterCutover(t *testing.T, c *cutoverHome) {
	t.Helper()
	codex := readText(t, c.codex)
	if strings.Contains(codex, "mcp_servers.qmd") || strings.Contains(codex, "do-not-show-me") || !strings.Contains(codex, "[mcp_servers.memoria]") || !strings.Contains(codex, "[mcp_servers.other]") || !strings.Contains(codex, `model = "m"`) {
		t.Fatalf("codex = %q", codex)
	}
	checkHookAndSkill(t, c)
}

func checkHookAndSkill(t *testing.T, c *cutoverHome) {
	t.Helper()
	settings := readText(t, c.settings)
	if !strings.Contains(settings, "push-hook") || !strings.Contains(settings, "SessionEnd") || !strings.Contains(settings, `"theme"`) {
		t.Fatalf("settings = %q", settings)
	}
	if readText(t, c.skill) != "the skill\n" || readText(t, filepath.Join(c.home, ".codex", "skills", "memoria", "SKILL.md")) != "the skill\n" {
		t.Fatal("the skill was not installed for both")
	}
}

func checkClaudeAfterCutover(t *testing.T, c *cutoverHome) {
	t.Helper()
	text := readText(t, c.claude)
	if !strings.Contains(text, "12345678901234567890") || !strings.Contains(text, "someone@example.test") || !strings.Contains(text, "b=<c>&d") {
		t.Fatalf("something else changed: %q", text)
	}
	var config struct {
		MCP map[string]struct {
			Command string   `json:"command"`
			Args    []string `json:"args"`
		} `json:"mcpServers"`
	}
	if err := json.Unmarshal([]byte(text), &config); err != nil {
		t.Fatal(err)
	}
	if _, found := config.MCP["qmd"]; found || config.MCP["memoria"].Command != "memoria" || config.MCP["memoria"].Args[0] != "mcp" || config.MCP["other"].Command != "other" {
		t.Fatalf("mcp = %+v", config.MCP)
	}
}

func TestCutoverNeverTouchesLocalQMD(t *testing.T) {
	c := newCutoverHome(t)
	config, index := filepath.Join(c.home, ".config", "qmd", "index.yml"), filepath.Join(c.home, ".cache", "qmd", "index.sqlite")
	before, err := os.Stat(index)
	if err != nil {
		t.Fatal(err)
	}
	if code := c.cutover("--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if code := c.h.run("migrate", "rollback", "--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	after, err := os.Stat(index)
	if err != nil || !after.ModTime().Equal(before.ModTime()) || readText(t, index) != "an index" || readText(t, config) != "collections: {}\n" {
		t.Fatalf("local QMD changed: %v", err)
	}
}

func TestARollbackWithNoCutoverDoesNothing(t *testing.T) {
	c := newCutoverHome(t)
	if code := c.h.run("migrate", "rollback"); code != 0 || !strings.Contains(c.h.stdout.String(), "Nothing to roll back") {
		t.Fatalf("code = %d, stdout = %q", code, c.h.stdout.String())
	}
}

func TestRollbackPutsEverythingBackAndRepeatsSafely(t *testing.T) {
	c := newCutoverHome(t)
	if code := c.cutover("--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if code := c.h.run("migrate", "rollback"); code != 0 || !strings.Contains(c.h.stdout.String(), "remove, as the cutover created it") || strings.Contains(readText(t, c.claude), `"qmd"`) {
		t.Fatalf("dry: code = %d, stdout = %q", code, c.h.stdout.String())
	}
	if code := c.h.run("migrate", "rollback", "--apply"); code != apperr.ExitUsage {
		t.Fatalf("no terminal: code = %d", code)
	}
	if code := c.h.run("migrate", "rollback", "--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, c.h.stderr.String())
	}
	checkOriginals(t, c)
	if code := c.h.run("migrate", "rollback", "--apply", "--yes"); code != 0 || !strings.Contains(c.h.stdout.String(), "Nothing to roll back") {
		t.Fatalf("again: code = %d, stdout = %q", code, c.h.stdout.String())
	}
	checkOriginals(t, c)
}

func TestASecondCutoverKeepsTheOriginalBackup(t *testing.T) {
	c := newCutoverHome(t)
	if code := c.cutover("--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if code := c.cutover(); code != 0 || !strings.Contains(c.h.stdout.String(), "Nothing to change") {
		t.Fatalf("nothing left: code = %d, stdout = %q", code, c.h.stdout.String())
	}
	if code := c.cutover("--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	// Something brings the entry back, and a third cutover swaps it again.
	writeFile(t, c.claude, claudeConfigWithQMD+" ")
	if code := c.cutover("--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if code := c.h.run("migrate", "rollback", "--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, c.h.stderr.String())
	}
	checkOriginals(t, c)
}

func TestACutoverAfterARollbackBacksUpWhatIsThereThen(t *testing.T) {
	c := newCutoverHome(t)
	for _, args := range [][]string{{"--apply", "--yes"}} {
		if code := c.cutover(args...); code != 0 {
			t.Fatalf("code = %d", code)
		}
	}
	if code := c.h.run("migrate", "rollback", "--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	changed := strings.Replace(claudeConfigWithQMD, "12345678901234567890", "7", 1)
	writeFile(t, c.claude, changed)
	if code := c.cutover("--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if code := c.h.run("migrate", "rollback", "--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if readText(t, c.claude) != changed {
		t.Fatalf("a later edit was lost: %q", readText(t, c.claude))
	}
}

func TestRollbackWillNotLoseAnEditUnlessTold(t *testing.T) {
	c := newCutoverHome(t)
	if code := c.cutover("--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	writeFile(t, c.settings, `{"theme": "edited"}`)
	if code := c.h.run("migrate", "rollback", "--apply", "--yes"); code != apperr.ExitGeneric || !strings.Contains(c.h.stderr.String(), "edited since") {
		t.Fatalf("code = %d, stderr = %q", code, c.h.stderr.String())
	}
	if readText(t, c.settings) != `{"theme": "edited"}` || !strings.Contains(readText(t, c.claude), "memoria") {
		t.Fatal("a refused rollback changed something")
	}
	if code := c.h.run("migrate", "rollback", "--apply", "--yes", "--overwrite-edits"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	checkOriginals(t, c)
}

func TestAClientThatIsNotThereIsLeftAlone(t *testing.T) {
	c := newCutoverHome(t)
	home := t.TempDir()
	t.Setenv("HOME", home)
	t.Setenv("USERPROFILE", home)
	if code := c.h.run("migrate", "cutover", "--skill", c.source, "--apply", "--yes"); code != 0 || !strings.Contains(c.h.stdout.String(), "Nothing to change") {
		t.Fatalf("code = %d, stdout = %q", code, c.h.stdout.String())
	}
	if entries, err := os.ReadDir(home); err != nil || len(entries) != 0 {
		t.Fatalf("it made %v: %v", entries, err)
	}
}

func TestEachClientsOwnSettingForItsFolderIsHonoured(t *testing.T) {
	c := newCutoverHome(t)
	claudeDir, codexDir := t.TempDir(), t.TempDir()
	writeFile(t, filepath.Join(claudeDir, ".claude.json"), claudeConfigWithQMD)
	writeFile(t, filepath.Join(codexDir, "config.toml"), codexConfigWithQMD)
	c.h.env["CLAUDE_CONFIG_DIR"], c.h.env["CODEX_HOME"] = claudeDir, codexDir
	if code := c.h.run("migrate", "cutover", "--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if strings.Contains(readText(t, filepath.Join(claudeDir, ".claude.json")), `"qmd"`) || strings.Contains(readText(t, filepath.Join(codexDir, "config.toml")), "mcp_servers.qmd") {
		t.Fatal("not changed where each client said")
	}
	checkOriginalsExceptSkill(t, c)
}

func checkOriginalsExceptSkill(t *testing.T, c *cutoverHome) {
	t.Helper()
	if readText(t, c.claude) != claudeConfigWithQMD || readText(t, c.codex) != codexConfigWithQMD {
		t.Fatal("the default location was changed")
	}
}

func TestAConfigItCannotReadChangesNothing(t *testing.T) {
	c := newCutoverHome(t)
	writeFile(t, c.claude, "not json")
	if code := c.cutover("--apply", "--yes"); code != apperr.ExitGeneric || !strings.Contains(c.h.stderr.String(), "not valid JSON") {
		t.Fatalf("code = %d, stderr = %q", code, c.h.stderr.String())
	}
	if readText(t, c.codex) != codexConfigWithQMD {
		t.Fatal("another client changed anyway")
	}
}

func TestWindowsLineEndingsSurviveInCodexConfig(t *testing.T) {
	c := newCutoverHome(t)
	writeFile(t, c.codex, strings.ReplaceAll(codexConfigWithQMD, "\n", "\r\n"))
	if code := c.cutover("--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	text := readText(t, c.codex)
	if strings.Contains(strings.ReplaceAll(text, "\r\n", ""), "\n") || strings.Contains(text, "mcp_servers.qmd") {
		t.Fatalf("codex = %q", text)
	}
}

func TestSkippingTheSkillAndHookIsSaid(t *testing.T) {
	c := newCutoverHome(t)
	if code := c.h.run("migrate", "cutover"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	out := c.h.stdout.String()
	if !strings.Contains(out, "hook push: not enabled") || !strings.Contains(out, "the skill was not installed") {
		t.Fatalf("stdout = %q", out)
	}
	if code := c.h.run("migrate", "cutover", "--skill", filepath.Join(c.home, "absent.md")); code != apperr.ExitUsage {
		t.Fatalf("code = %d", code)
	}
}
