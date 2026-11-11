package cmd

import (
	"os"
	"strings"
	"testing"

	"github.com/spf13/cobra"

	"github.com/mggarofalo/memoria/cli/internal/migrate"
	"github.com/mggarofalo/memoria/cli/internal/output"
)

// The runbook is read by someone moving their only copy of a corpus, so what it says about a command is pinned to the command.
func readRunbook(t *testing.T) string {
	t.Helper()
	data, err := os.ReadFile(repoPath("docs/migration.md"))
	if err != nil {
		t.Fatal(err)
	}
	return strings.ReplaceAll(string(data), "\r\n", "\n")
}

// requiredFlags are what each command refuses to run without. The commands check these themselves, so cobra does not know them.
var requiredFlags = map[string][]string{
	"memoria migrate plan":     {"qmd-config"},
	"memoria migrate report":   {"qmd-config"},
	"memoria migrate push":     {"qmd-config"},
	"memoria migrate verify":   {"qmd-config"},
	"memoria migrate rerender": {"qmd-config", "codex-sessions"},
}

// runbookSteps must each appear as a command, so a step cannot quietly go missing.
var runbookSteps = []string{
	"memoria auth login", "memoria migrate plan", "memoria migrate report", "memoria migrate push", "memoria migrate verify",
	"memoria migrate rerender", "memoria migrate cutover", "memoria migrate rollback",
}

// runbookFlags must each be shown in use, as a command the owner can copy.
var runbookFlags = []string{"memoria migrate rerender --accept-uncounted", "memoria migrate push --accept-over-cap"}

// codeCommands is every memoria command in the runbook's code blocks, with a line continued by a backslash joined to the next,
// and a prompt or .exe taken off.
func codeCommands(runbook string) []string {
	var commands []string
	inCode, pending := false, ""
	for _, line := range strings.Split(runbook, "\n") {
		if strings.HasPrefix(strings.TrimSpace(line), "```") {
			inCode = !inCode
			continue
		}
		if !inCode {
			continue
		}
		text := pending + strings.TrimSpace(line)
		if continued, found := strings.CutSuffix(text, "\\"); found {
			pending = continued
			continue
		}
		pending = ""
		if command, ok := asMemoria(text); ok {
			commands = append(commands, command)
		}
	}
	return commands
}

func asMemoria(line string) (string, bool) {
	for _, prompt := range []string{"$ ", "> ", "PS> "} {
		line = strings.TrimPrefix(line, prompt)
	}
	line = strings.TrimSpace(line)
	for _, name := range []string{"memoria.exe ", "./memoria ", "memoria "} {
		if rest, found := strings.CutPrefix(line, name); found {
			return "memoria " + rest, true
		}
	}
	return "", false
}

// words splits a command line as a shell would for these, keeping a quoted placeholder as one word.
func words(line string) []string {
	var quote rune
	return strings.FieldsFunc(line, func(r rune) bool {
		switch {
		case quote != 0:
			if r == quote {
				quote = 0
			}
		case r == '"' || r == '\'':
			quote = r
		default:
			return r == ' ' || r == '\t'
		}
		return false
	})
}

// judge has cobra read a command line as it would run it: the command must exist and be one that runs, its flags and their
// values must parse, its positional arguments must be allowed, and its required flags must be there.
func judge(root *cobra.Command, line string) string {
	cmd, rest, err := root.Find(words(line)[1:])
	if err != nil || cmd == root || !cmd.Runnable() || cmd.HasSubCommands() {
		return "no such command"
	}
	if err := cmd.ParseFlags(rest); err != nil {
		return err.Error()
	}
	if err := cmd.ValidateArgs(cmd.Flags().Args()); err != nil {
		return err.Error()
	}
	for _, name := range requiredFlags[cmd.CommandPath()] {
		if !cmd.Flags().Changed(name) {
			return "--" + name + " is missing"
		}
	}
	return ""
}

func TestTheRunbooksCommandsAreRealWithRealFlags(t *testing.T) {
	commands := codeCommands(readRunbook(t))
	for _, line := range commands {
		if problem := judge(newHarness(t).app().rootCmd(), line); problem != "" {
			t.Errorf("%q: %s", line, problem)
		}
	}
	for _, use := range runbookFlags {
		step, flag, _ := strings.Cut(use, " --")
		if !hasCommand(commands, step, "--"+flag) {
			t.Errorf("the runbook shows no %q", use)
		}
	}
	for _, step := range runbookSteps {
		if !hasCommand(commands, step, "") {
			t.Errorf("the runbook has no %q step", step)
		}
	}
}

// hasCommand is a command line for this step, using this flag if one is named.
func hasCommand(commands []string, step, flag string) bool {
	for _, line := range commands {
		if strings.HasPrefix(line+" ", step+" ") && (flag == "" || strings.Contains(line+" ", " "+flag+" ")) {
			return true
		}
	}
	return false
}

// What the runbook test must refuse, so the test above is shown to read commands as cobra does.
func TestTheRunbookCheckRefusesWhatCobraWould(t *testing.T) {
	for line, want := range map[string]string{
		"memoria migrate rollbak": "no such command",
		"memoria migrate":         "no such command",
		"memoria migrate verify --qmd-config x --timeout 10q":                    "invalid argument",
		"memoria migrate rerender --qmd-config x":                                "--codex-sessions is missing",
		"memoria migrate push --qmd-config x extra":                              "takes no positional arguments",
		"memoria migrate push --qmd-config x -Z":                                 "unknown shorthand flag",
		"memoria migrate push --qmd-config x --nonesuch":                         "unknown flag",
		`memoria migrate rerender --qmd-config x --codex-sessions "a b" --apply`: "",
	} {
		if got := judge(newHarness(t).app().rootCmd(), line); (want == "" && got != "") || !strings.Contains(got, want) {
			t.Errorf("%q = %q, want %q", line, got, want)
		}
	}
	block := "```sh\n$ memoria.exe migrate plan --qmd-config x\n  memoria migrate push \\\n    --qmd-config x --apply\n```\nmemoria not code\n"
	if got := strings.Join(codeCommands(block), "|"); got != "memoria migrate plan --qmd-config x|memoria migrate push --qmd-config x --apply" {
		t.Fatalf("commands = %q", got)
	}
}

func TestTheRunbookQuotesWhatRerenderSaysAndUses(t *testing.T) {
	runbook := readRunbook(t)
	for _, said := range []string{output.RerenderNoTranscriptNote, output.RerenderShorterNote, output.RerenderTooLargeNote, "`transcriptTooLarge`", "`--max-session-bytes`", output.RerenderShrankNote, output.RerenderUncountedNote, output.RerenderAcceptedNote, "format=codex", "409 `session_shrank`", "`allowShrink`", "`accepted_uncounted`", "`acceptedUncounted`"} {
		if !strings.Contains(runbook, said) {
			t.Errorf("the runbook does not say %q", said)
		}
	}
	if migrate.DefaultMaxDocumentBytes != 2*1024*1024 || !strings.Contains(runbook, "larger than the default 2 MiB cap") {
		t.Error("the runbook's cap is not the one rerender uses")
	}
	if codexFormat != "codex" {
		t.Error("the format rerender sends is not the one the runbook names")
	}
	if migrate.RerenderAcceptedUncounted != "accepted_uncounted" || !strings.Contains(migrate.ExporterFormat, "3478baa") || !strings.Contains(runbook, "agent-skills at `3478baa`") {
		t.Error("the runbook does not name the exporter format, or the report status, that rerender uses")
	}
}
