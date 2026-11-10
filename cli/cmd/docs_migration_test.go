package cmd

import (
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"

	"github.com/mggarofalo/memoria/cli/internal/migrate"
	"github.com/mggarofalo/memoria/cli/internal/output"
)

// The runbook is read by someone moving their only copy of a corpus, so what it says about a command is pinned to the command.
func readRunbook(t *testing.T) string {
	t.Helper()
	data, err := os.ReadFile(filepath.Join("..", "..", "docs", "migration.md"))
	if err != nil {
		t.Fatal(err)
	}
	return strings.ReplaceAll(string(data), "\r\n", "\n")
}

var flagPattern = regexp.MustCompile(`\s(--[a-z-]+)`)

// Every command line in the runbook's shell blocks names a real command, with flags it takes.
func TestTheRunbooksCommandsAreRealWithRealFlags(t *testing.T) {
	a := newHarness(t).app()
	root := a.rootCmd()
	for _, line := range strings.Split(readRunbook(t), "\n") {
		if !strings.HasPrefix(line, "memoria ") {
			continue
		}
		fields := strings.Fields(line)
		cmd, rest, err := root.Find(fields[1:])
		if err != nil || cmd == root {
			t.Errorf("%q: no such command", line)
			continue
		}
		for _, flag := range flagPattern.FindAllStringSubmatch(" "+strings.Join(rest, " "), -1) {
			name := strings.TrimPrefix(flag[1], "--")
			if cmd.Flags().Lookup(name) == nil && cmd.InheritedFlags().Lookup(name) == nil {
				t.Errorf("%q: %s takes no --%s", line, cmd.CommandPath(), name)
			}
		}
	}
}

func TestTheRunbookQuotesWhatRerenderSaysAndUses(t *testing.T) {
	runbook := readRunbook(t)
	for _, said := range []string{output.RerenderNoTranscriptNote, "format=codex", "409 `session_shrank`", "`allowShrink`"} {
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
}
