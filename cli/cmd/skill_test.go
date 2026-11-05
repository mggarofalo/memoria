package cmd

import (
	"encoding/json"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"

	"github.com/spf13/cobra"
)

const skillPath = "../../skills/memoria/SKILL.md"

func readSkill(t *testing.T) string {
	t.Helper()
	data, err := os.ReadFile(skillPath)
	if err != nil {
		t.Fatal(err)
	}
	return strings.ReplaceAll(string(data), "\r\n", "\n")
}

// commandLines returns the memoria commands in the skill's sh blocks.
func commandLines(skill string) []string {
	var lines []string
	inBlock := false
	for _, line := range strings.Split(skill, "\n") {
		trimmed := strings.TrimSpace(line)
		switch {
		case strings.HasPrefix(trimmed, "```"):
			inBlock = trimmed == "```sh"
		case inBlock && strings.HasPrefix(trimmed, "memoria "):
			lines = append(lines, trimmed)
		}
	}
	return lines
}

func flagsOf(fields []string) []string {
	var names []string
	for _, field := range fields {
		if strings.HasPrefix(field, "--") {
			names = append(names, strings.TrimPrefix(field, "--"))
		}
	}
	return names
}

// resolves reports whether a command line names a real command and only real flags.
func resolves(t *testing.T, root *cobra.Command, line string) {
	t.Helper()
	fields := strings.Fields(strings.TrimPrefix(line, "memoria "))
	found, rest, err := root.Find(fields)
	if err != nil || found == root {
		t.Errorf("the skill names a command that does not exist: %q", line)
		return
	}
	if len(rest) == len(fields) {
		t.Errorf("the skill's command resolved to nothing: %q", line)
	}
	for _, name := range flagsOf(fields) {
		if found.Flags().Lookup(name) == nil && found.InheritedFlags().Lookup(name) == nil {
			t.Errorf("the skill uses a flag %s does not have: --%s", found.CommandPath(), name)
		}
	}
}

func TestEveryCommandInTheSkillExists(t *testing.T) {
	skill := readSkill(t)
	lines := commandLines(skill)
	if len(lines) < 4 {
		t.Fatalf("found only %d commands, so the search is not reading the skill: %v", len(lines), lines)
	}
	root := newHarness(t).app().rootCmd()
	for _, line := range lines {
		resolves(t, root, line)
	}
}

// Backticked mentions in the text are checked too: a table row can go stale as easily as a block.
func TestEveryCommandAndFlagMentionedInTheSkillExists(t *testing.T) {
	skill := readSkill(t)
	root := newHarness(t).app().rootCmd()
	mentioned := regexp.MustCompile("`memoria ([a-z-]+)").FindAllStringSubmatch(skill, -1)
	if len(mentioned) < 5 {
		t.Fatalf("found only %d mentions: %v", len(mentioned), mentioned)
	}
	for _, match := range mentioned {
		if found, _, err := root.Find([]string{match[1]}); err != nil || found == root {
			t.Errorf("the skill mentions a command that does not exist: %q", match[1])
		}
	}
	query, _, err := root.Find([]string{"query"})
	if err != nil {
		t.Fatal(err)
	}
	for _, match := range regexp.MustCompile("`--([a-z-]+)`").FindAllStringSubmatch(skill, -1) {
		if query.Flags().Lookup(match[1]) == nil && query.InheritedFlags().Lookup(match[1]) == nil {
			t.Errorf("the skill mentions a flag query does not have: --%s", match[1])
		}
	}
}

func TestTheSkillHoldsNoPrivateDetail(t *testing.T) {
	skill := readSkill(t)
	for name, pattern := range map[string]string{
		"a link":             `https?://`,
		"an IP address":      `\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b`,
		"a work-item number": `\b[A-Z][A-Z0-9]+-\d+\b`,
		"a key":              `mem_[0-9a-f]`,
		"a home folder":      `(?i)\b(Users|home)[/\\][a-z]`,
	} {
		if found := regexp.MustCompile(pattern).FindString(skill); found != "" {
			t.Errorf("the skill holds %s: %q", name, found)
		}
	}
}

// Claude Code and Codex both read name and description, and Codex caps the description.
func TestTheSkillHasFrontmatterBothAgentsRead(t *testing.T) {
	skill := readSkill(t)
	parts := strings.SplitN(skill, "---\n", 3)
	if len(parts) != 3 || parts[0] != "" {
		t.Fatal("the skill does not start with frontmatter")
	}
	if !strings.Contains(parts[1], "name: memoria\n") {
		t.Fatalf("frontmatter = %q", parts[1])
	}
	_, description, found := strings.Cut(parts[1], "description: >-\n")
	if !found {
		t.Fatalf("frontmatter = %q", parts[1])
	}
	if size := len(strings.Join(strings.Fields(description), " ")); size == 0 || size > 1024 {
		t.Fatalf("the description is %d characters", size)
	}
}

// Under .claude/skills it would be live for anyone working in this repository.
func TestTheSkillStaysInactiveUntilCutover(t *testing.T) {
	for _, path := range []string{"../../.claude/skills/memoria", "../../.agents/skills/memoria", "../../.codex/skills/memoria"} {
		if _, err := os.Stat(filepath.FromSlash(path)); err == nil {
			t.Errorf("the skill is active at %s", path)
		}
	}
}

func TestTheTriggerPromptsAreBalanced(t *testing.T) {
	data, err := os.ReadFile("../../skills/evals/memoria-triggers.json")
	if err != nil {
		t.Fatal(err)
	}
	var evals struct {
		Skill            string   `json:"skill"`
		ShouldTrigger    []string `json:"shouldTrigger"`
		ShouldNotTrigger []string `json:"shouldNotTrigger"`
	}
	if err := json.Unmarshal(data, &evals); err != nil {
		t.Fatal(err)
	}
	if evals.Skill != "memoria" || len(evals.ShouldTrigger) < 5 || len(evals.ShouldNotTrigger) < 5 {
		t.Fatalf("evals = %+v", evals)
	}
}
