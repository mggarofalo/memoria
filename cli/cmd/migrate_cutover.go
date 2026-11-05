package cmd

import (
	"bufio"
	"context"
	"fmt"
	"os"
	"strings"

	"github.com/spf13/cobra"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/migrate"
	"github.com/mggarofalo/memoria/cli/internal/termsafe"
)

type migrateCutoverOptions struct {
	apply       bool
	yes         bool
	skill       string
	hookCommand string
}

func newMigrateCutoverCmd(a *app) *cobra.Command {
	var o migrateCutoverOptions
	cmd := &cobra.Command{
		Use:   "cutover",
		Short: "Point your clients at Memoria instead of local QMD. Shows the change unless you pass --apply",
		Long: `Change Claude Code and Codex to use Memoria: swap the qmd MCP entry for memoria, install the
skill, and turn on the hook that pushes sessions. Without --apply it only shows what it would
change. With --apply it shows that and asks you to confirm, unless you also pass --yes.

It backs up every file it changes first, under the CLI's config folder. Running it again is
safe: it changes only what is left to change, and keeps the original backup.

The skill is in a checkout of this repository, so pass its path with --skill. The hook is not
part of this repository, so pass the command that runs it with --hook-command. Without either,
that part is skipped and it says so.

It never touches local QMD or its index, so 'memoria migrate rollback' can put everything
back. It changes only user-level entries: a qmd entry in one project's own config stays.`,
		Example: `  memoria migrate cutover --skill skills/memoria/SKILL.md
  memoria migrate cutover --skill skills/memoria/SKILL.md --hook-command "my-hook" --apply`,
		Args: noArgs,
		RunE: func(cmd *cobra.Command, _ []string) error { return a.migrateCutover(cmd.Context(), o) },
	}
	f := cmd.Flags()
	f.BoolVar(&o.apply, "apply", false, "Make the change, after showing it and asking")
	f.BoolVar(&o.yes, "yes", false, "With --apply, do not ask")
	f.StringVar(&o.skill, "skill", "", "Path to skills/memoria/SKILL.md in a checkout of this repository")
	f.StringVar(&o.hookCommand, "hook-command", "", "The command that pushes sessions, to run when a Claude Code session ends")
	return cmd
}

func (a *app) clientPaths() (migrate.ClientPaths, error) {
	home, err := os.UserHomeDir()
	if err != nil {
		return migrate.ClientPaths{}, apperr.New(apperr.KindGeneric, "your home folder cannot be found")
	}
	return migrate.PathsUnder(home, a.getenv("CLAUDE_CONFIG_DIR"), a.getenv("CODEX_HOME")), nil
}

func (a *app) migrateCutover(_ context.Context, o migrateCutoverOptions) error {
	paths, err := a.clientPaths()
	if err != nil {
		return err
	}
	var skill []byte
	if o.skill != "" {
		if skill, err = os.ReadFile(o.skill); err != nil {
			return apperr.New(apperr.KindUsage, "--skill cannot be read")
		}
	}
	edits, notes, err := migrate.PlanCutover(migrate.CutoverOptions{Paths: paths, Skill: skill, HookCommand: o.hookCommand})
	if err != nil {
		return apperr.Wrap(apperr.KindGeneric, err, err.Error())
	}
	a.showCutover(edits, notes)
	if len(edits) == 0 {
		a.say("Nothing to change.")
		return nil
	}
	if !o.apply {
		a.say("Nothing was changed. Pass --apply to make this change.")
		return nil
	}
	if err := a.confirm(o.yes, "Make this change?"); err != nil {
		return err
	}
	if err := migrate.ApplyCutover(edits); err != nil {
		return apperr.Wrap(apperr.KindGeneric, err, err.Error())
	}
	a.say("Done. Your files were backed up first, and 'memoria migrate rollback' puts them back. Restart your clients to pick this up.")
	return nil
}

func (a *app) say(line string) {
	_, _ = fmt.Fprintln(a.stdout, termsafe.Line(line))
}

func (a *app) showCutover(edits []migrate.FileEdit, notes []string) {
	for _, edit := range edits {
		a.say(edit.Path)
		for _, line := range edit.Summary {
			a.say("  " + line)
		}
	}
	for _, note := range notes {
		a.say("Note: " + note)
	}
}

// confirm asks on a terminal. With no terminal to ask on, it refuses unless told --yes, so a script cannot change this by accident.
func (a *app) confirm(yes bool, question string) error {
	if yes {
		return nil
	}
	if !a.stdinIsTerminal() {
		return apperr.New(apperr.KindUsage, "this needs confirming, and there is no terminal to ask on. Pass --yes to go ahead")
	}
	_, _ = fmt.Fprint(a.stderr, question+" Type yes to go ahead: ")
	line, _ := bufio.NewReader(a.stdin).ReadString('\n')
	if strings.TrimSpace(line) != "yes" {
		return apperr.New(apperr.KindGeneric, "not confirmed, so nothing was changed")
	}
	return nil
}

type migrateRollbackOptions struct {
	apply     bool
	yes       bool
	overwrite bool
}

func newMigrateRollbackCmd(a *app) *cobra.Command {
	var o migrateRollbackOptions
	cmd := &cobra.Command{
		Use:   "rollback",
		Short: "Put back what the last cutover changed. Shows the change unless you pass --apply",
		Long: `Restore every file the last cutover changed from its backup, and remove files it created.
Without --apply it only shows what it would do. With --apply it shows that and asks you to
confirm, unless you also pass --yes. Running it again is safe, and does nothing.

A file edited since the cutover is not overwritten unless you pass --overwrite-edits, as a
rollback would lose that edit. Nothing on the server changes, and neither does local QMD.`,
		Example: `  memoria migrate rollback
  memoria migrate rollback --apply`,
		Args: noArgs,
		RunE: func(cmd *cobra.Command, _ []string) error { return a.migrateRollback(cmd.Context(), o) },
	}
	f := cmd.Flags()
	f.BoolVar(&o.apply, "apply", false, "Make the change, after showing it and asking")
	f.BoolVar(&o.yes, "yes", false, "With --apply, do not ask")
	f.BoolVar(&o.overwrite, "overwrite-edits", false, "Restore files edited since the cutover, losing those edits")
	return cmd
}

func (a *app) migrateRollback(_ context.Context, o migrateRollbackOptions) error {
	manifest, err := migrate.ActiveManifest()
	if err != nil {
		return apperr.Wrap(apperr.KindGeneric, err, err.Error())
	}
	if manifest == nil {
		a.say("Nothing to roll back.")
		return nil
	}
	steps, err := migrate.PlanRollback(manifest)
	if err != nil {
		return apperr.Wrap(apperr.KindGeneric, err, err.Error())
	}
	edited := a.showRollback(steps)
	if !o.apply {
		a.say("Nothing was changed. Pass --apply to make this change.")
		return nil
	}
	if edited > 0 && !o.overwrite {
		return apperr.New(apperr.KindGeneric, "%d file(s) were edited since the cutover, so nothing was changed. Pass --overwrite-edits to lose those edits", edited)
	}
	if err := a.confirm(o.yes, "Put these back?"); err != nil {
		return err
	}
	if err := migrate.ApplyRollback(manifest, steps); err != nil {
		return apperr.Wrap(apperr.KindGeneric, err, err.Error())
	}
	a.say("Done. Restart your clients to pick this up.")
	return nil
}

func (a *app) showRollback(steps []migrate.RollbackStep) int {
	edited := 0
	for _, step := range steps {
		action := "restore from backup"
		if !step.Entry.Existed {
			action = "remove, as the cutover created it"
		}
		if step.Changed {
			action += " (edited since the cutover)"
			edited++
		}
		a.say(step.Entry.Path + ": " + action)
	}
	return edited
}
