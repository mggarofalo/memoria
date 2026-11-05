package cmd

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
)

const (
	mcpWithQMD    = `{"mcpServers":{"qmd":{"command":"qmd","args":["mcp","--key","do-not-show-me"],"env":{"SECRET_VALUE":"do-not-show-me"}}}}` + "\n"
	claudeNoQMD   = `{"numStartups":12345678901234567890,"oauthAccount":{"emailAddress":"someone@example.test"},"mcpServers":{"other":{"command":"other"}}}` + "\n"
	codexNoQMD    = "model = \"m\"\n\n[mcp_servers.other]\ncommand = \"other\"\n"
	settingsHook  = `{"theme":"dark","hooks":{"SessionEnd":[{"hooks":[{"type":"command","command":"python3 /h/session-end-export.py"}]}]}}` + "\n"
	settingsLocal = `{"permissions":{"allow":["mcp__qmd__*"]},"enabledMcpjsonServers":["qmd"]}` + "\n"
)

type cutoverHome struct {
	h                                                *harness
	home, mcp, claude, codex, settings, local, skill string
	source                                           string
}

// newCutoverHome is a temporary home laid out as the owner's is: qmd in ~/.mcp.json, none in Codex, an export hook already set, and
// a local QMD beside it. Nothing here can reach a real home.
func newCutoverHome(t *testing.T) *cutoverHome {
	t.Helper()
	home := t.TempDir()
	t.Setenv("HOME", home)
	t.Setenv("USERPROFILE", home)
	c := &cutoverHome{
		h: newHarness(t), home: home,
		mcp: filepath.Join(home, ".mcp.json"), claude: filepath.Join(home, ".claude.json"), codex: filepath.Join(home, ".codex", "config.toml"),
		settings: filepath.Join(home, ".claude", "settings.json"), local: filepath.Join(home, ".claude", "settings.local.json"),
		skill: filepath.Join(home, ".claude", "skills", "memoria", "SKILL.md"), source: filepath.Join(t.TempDir(), "SKILL.md"),
	}
	writeFile(t, c.mcp, mcpWithQMD)
	writeFile(t, c.claude, claudeNoQMD)
	writeFile(t, c.codex, codexNoQMD)
	writeFile(t, c.settings, settingsHook)
	writeFile(t, c.local, settingsLocal)
	writeFile(t, c.source, "the skill\n")
	writeFile(t, filepath.Join(home, ".config", "qmd", "index.yml"), "collections: {}\n")
	writeFile(t, filepath.Join(home, ".cache", "qmd", "index.sqlite"), "an index")
	return c
}

func (c *cutoverHome) cutover(extra ...string) int {
	return c.h.run(append([]string{"migrate", "cutover", "--skill", c.source}, extra...)...)
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
	for path, want := range map[string]string{c.mcp: mcpWithQMD, c.claude: claudeNoQMD, c.codex: codexNoQMD, c.settings: settingsHook, c.local: settingsLocal} {
		if readText(t, path) != want {
			t.Errorf("%s is not its original: %q", path, readText(t, path))
		}
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
	for _, want := range []string{c.mcp, "- mcpServers.qmd", "+ mcpServers.memoria", c.local, "+ permissions.allow mcp__memoria__*", "+ enabledMcpjsonServers memoria", "+ the memoria skill", "session-end-export.py", "memoria was not added there", "Nothing was changed"} {
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
	if code := c.cutover("--apply"); code != 0 || !strings.Contains(readText(t, c.mcp), "memoria") {
		t.Fatalf("agreed: code = %d, stderr = %q", code, c.h.stderr.String())
	}
}

func TestCutoverSwapsWhereQMDIsAndLeavesTheRest(t *testing.T) {
	c := newCutoverHome(t)
	if code := c.cutover("--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, c.h.stderr.String())
	}
	mcp := readText(t, c.mcp)
	if strings.Contains(mcp, `"qmd"`) || strings.Contains(mcp, "do-not-show-me") || !strings.Contains(mcp, `"memoria"`) {
		t.Fatalf(".mcp.json = %q", mcp)
	}
	if readText(t, c.claude) != claudeNoQMD || readText(t, c.codex) != codexNoQMD || readText(t, c.settings) != settingsHook {
		t.Fatal("a file with no qmd in it changed")
	}
	checkPermissionsAndSkill(t, c)
}

func checkPermissionsAndSkill(t *testing.T, c *cutoverHome) {
	t.Helper()
	local := readText(t, c.local)
	if !strings.Contains(local, "mcp__memoria__*") || !strings.Contains(local, "mcp__qmd__*") || !strings.Contains(local, `"memoria"`) {
		t.Fatalf("settings.local.json = %q", local)
	}
	if readText(t, c.skill) != "the skill\n" {
		t.Fatal("the skill was not installed")
	}
}

func TestNoQMDAnywhereIsSaidAndFails(t *testing.T) {
	c := newCutoverHome(t)
	writeFile(t, c.mcp, `{"mcpServers":{}}`)
	if code := c.cutover("--apply", "--yes"); code != apperr.ExitGeneric || !strings.Contains(c.h.stderr.String(), "qmd was not found") || !strings.Contains(c.h.stderr.String(), c.mcp) {
		t.Fatalf("code = %d, stderr = %q", code, c.h.stderr.String())
	}
	if _, err := os.Stat(c.skill); !os.IsNotExist(err) {
		t.Fatalf("it changed something: %v", err)
	}
	if code := c.h.run("migrate", "cutover"); code != apperr.ExitGeneric {
		t.Fatalf("dry: code = %d", code)
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
	if code := c.h.run("migrate", "rollback"); code != 0 || !strings.Contains(c.h.stdout.String(), "remove, as the cutover created it") || strings.Contains(readText(t, c.mcp), `"qmd"`) {
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

func TestARepeatedCutoverChangesNothingMore(t *testing.T) {
	c := newCutoverHome(t)
	if code := c.cutover("--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if code := c.cutover(); code != 0 || !strings.Contains(c.h.stdout.String(), "Nothing to change") {
		t.Fatalf("code = %d, stdout = %q", code, c.h.stdout.String())
	}
	if code := c.cutover("--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if code := c.h.run("migrate", "rollback", "--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	checkOriginals(t, c)
}

func TestACutoverAfterARollbackBacksUpWhatIsThereThen(t *testing.T) {
	c := newCutoverHome(t)
	if code := c.cutover("--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if code := c.h.run("migrate", "rollback", "--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	changed := strings.Replace(mcpWithQMD, `"mcpServers"`, `"later":"an edit","mcpServers"`, 1)
	writeFile(t, c.mcp, changed)
	if code := c.cutover("--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if code := c.h.run("migrate", "rollback", "--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	if readText(t, c.mcp) != changed {
		t.Fatalf("a later edit was lost: %q", readText(t, c.mcp))
	}
}

func TestARollbackSaysWhenItOnlyUndoesItsOwnChange(t *testing.T) {
	c := newCutoverHome(t)
	if code := c.cutover("--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	writeFile(t, c.mcp, strings.Replace(readText(t, c.mcp), `"mcpServers"`, `"later": 1, "mcpServers"`, 1))
	if code := c.h.run("migrate", "rollback", "--apply", "--yes"); code != 0 || !strings.Contains(c.h.stdout.String(), "reverse only the cutover's change") {
		t.Fatalf("code = %d, stdout = %q, stderr = %q", code, c.h.stdout.String(), c.h.stderr.String())
	}
	if text := readText(t, c.mcp); !strings.Contains(text, `"later"`) || !strings.Contains(text, "SECRET_VALUE") || strings.Contains(text, "memoria") {
		t.Fatalf(".mcp.json = %q", text)
	}
}

func TestRollbackWillNotLoseAnEditItCannotSeparateUnlessTold(t *testing.T) {
	c := newCutoverHome(t)
	if code := c.cutover("--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d", code)
	}
	writeFile(t, c.skill, "edited by hand")
	if code := c.h.run("migrate", "rollback", "--apply", "--yes"); code != apperr.ExitGeneric || !strings.Contains(c.h.stderr.String(), "edited since") {
		t.Fatalf("code = %d, stderr = %q", code, c.h.stderr.String())
	}
	if readText(t, c.skill) != "edited by hand" || !strings.Contains(readText(t, c.mcp), "memoria") {
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
	if code := c.h.run("migrate", "cutover", "--skill", c.source, "--apply", "--yes"); code != apperr.ExitGeneric {
		t.Fatalf("code = %d, stdout = %q", code, c.h.stdout.String())
	}
	if entries, err := os.ReadDir(home); err != nil || len(entries) != 0 {
		t.Fatalf("it made %v: %v", entries, err)
	}
}

func TestEachClientsOwnSettingForItsFolderIsHonoured(t *testing.T) {
	c := newCutoverHome(t)
	claudeDir, codexDir := t.TempDir(), t.TempDir()
	writeFile(t, filepath.Join(claudeDir, ".claude.json"), mcpWithQMD)
	writeFile(t, filepath.Join(codexDir, "config.toml"), "[mcp_servers.qmd]\ncommand = \"qmd\"\n")
	c.h.env["CLAUDE_CONFIG_DIR"], c.h.env["CODEX_HOME"] = claudeDir, codexDir
	if code := c.h.run("migrate", "cutover", "--apply", "--yes"); code != 0 {
		t.Fatalf("code = %d, stderr = %q", code, c.h.stderr.String())
	}
	if strings.Contains(readText(t, filepath.Join(claudeDir, ".claude.json")), `"qmd"`) || strings.Contains(readText(t, filepath.Join(codexDir, "config.toml")), "mcp_servers.qmd") {
		t.Fatal("not changed where each client said")
	}
	if readText(t, c.claude) != claudeNoQMD || readText(t, c.codex) != codexNoQMD {
		t.Fatal("the default location was changed")
	}
}

func TestAConfigItCannotReadChangesNothing(t *testing.T) {
	c := newCutoverHome(t)
	writeFile(t, c.claude, "not json")
	if code := c.cutover("--apply", "--yes"); code != apperr.ExitGeneric || !strings.Contains(c.h.stderr.String(), "not a valid JSON object") {
		t.Fatalf("code = %d, stderr = %q", code, c.h.stderr.String())
	}
	if readText(t, c.mcp) != mcpWithQMD {
		t.Fatal("another file changed anyway")
	}
}

func TestSkippingTheSkillIsSaid(t *testing.T) {
	c := newCutoverHome(t)
	if code := c.h.run("migrate", "cutover"); code != 0 || !strings.Contains(c.h.stdout.String(), "the skill was not installed") {
		t.Fatalf("code = %d, stdout = %q", code, c.h.stdout.String())
	}
	if code := c.h.run("migrate", "cutover", "--skill", filepath.Join(c.home, "absent.md")); code != apperr.ExitUsage {
		t.Fatalf("code = %d", code)
	}
}

func TestEscapingForABatchFile(t *testing.T) {
	for arg, want := range map[string]string{
		"plain": `^^^"plain^^^"`,
		"a&b":   `^^^"a^^^&b^^^"`,
		`a"b`:   `^^^"a\^^^"b^^^"`,
		`a\`:    `^^^"a\\^^^"`,
	} {
		if got := EscapeForCmd(arg); got != want {
			t.Errorf("%q = %q, want %q", arg, got, want)
		}
	}
	if line := CmdLine(`C:\q md.cmd`, []string{"x"}); !strings.HasPrefix(line, `/d /s /c "^"C:\q^ md.cmd^"`) || !strings.HasSuffix(line, `"`) {
		t.Fatalf("line = %q", line)
	}
}
