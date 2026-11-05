package cmd

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strings"

	"github.com/spf13/cobra"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/migrate"
	"github.com/mggarofalo/memoria/cli/internal/output"
	"github.com/mggarofalo/memoria/cli/internal/sources"
)

const defaultCodexTarget = "codex-sessions"

func newMigrateCmd(a *app) *cobra.Command {
	cmd := &cobra.Command{
		Use:   "migrate",
		Short: "Plan moving a local QMD setup to a Memoria server. Read-only",
		Long: `Look at a local QMD setup before moving it to a Memoria server.

These commands only read, and only the paths you give them. They have no default
location, need no key or server, and never open QMD's index.sqlite.`,
		Args: noArgs,
		RunE: func(cmd *cobra.Command, _ []string) error { return cmd.Help() },
	}
	cmd.AddCommand(newMigratePlanCmd(a), newMigrateReportCmd(a))
	return cmd
}

type migratePlanOptions struct {
	qmdConfig   string
	targets     []string
	codexDir    string
	codexTarget string
}

func newMigratePlanCmd(a *app) *cobra.Command {
	var o migratePlanOptions
	cmd := &cobra.Command{
		Use:   "plan --qmd-config <index.yml>",
		Short: "Count what each QMD collection holds, and where it would go",
		Long: `Read QMD's index.yml and count what QMD would index in each collection: files, bytes,
the largest file, and how many are over a server's default 2 MiB limit on a document.
No file's text is read.

A migration will need each target collection to be empty, so choose names that are.`,
		Example: `  memoria migrate plan --qmd-config ~/.config/qmd/index.yml
  memoria migrate plan --qmd-config index.yml --target notes=archive-notes --codex-sessions ~/.codex/sessions`,
		Args: noArgs,
		RunE: func(cmd *cobra.Command, _ []string) error { return a.migratePlan(cmd.Context(), o) },
	}
	f := cmd.Flags()
	f.StringVar(&o.qmdConfig, "qmd-config", "", "Path to QMD's index.yml (required). There is no default")
	f.StringArrayVar(&o.targets, "target", nil, "Send a collection to another name, as local=target; repeatable. Each keeps its name otherwise")
	f.StringVar(&o.codexDir, "codex-sessions", "", "Also count the raw Codex transcripts in this folder")
	f.StringVar(&o.codexTarget, "codex-target", defaultCodexTarget, "The collection raw Codex sessions would go to")
	return cmd
}

// parseTargets reads local=target pairs. It does not repeat what it refuses.
func parseTargets(pairs []string) (map[string]string, error) {
	targets := make(map[string]string, len(pairs))
	for _, pair := range pairs {
		local, target, found := strings.Cut(pair, "=")
		if !found || local == "" || !sources.ValidCollection(target) {
			return nil, apperr.New(apperr.KindUsage, "--target takes local=target, with a target name that has no slash or surrounding space")
		}
		targets[local] = target
	}
	return targets, nil
}

// loadCollections requires the path to be given, and a --target to name a real collection.
func loadCollections(path string, targets map[string]string) ([]migrate.Collection, error) {
	if path == "" {
		return nil, apperr.New(apperr.KindUsage, "--qmd-config is required: there is no default, so nothing reads a real setup by accident")
	}
	collections, err := migrate.LoadCollections(path)
	if err != nil {
		return nil, apperr.Wrap(apperr.KindUsage, err, err.Error())
	}
	known := make(map[string]bool, len(collections))
	for _, collection := range collections {
		known[collection.Name] = true
	}
	for local := range targets {
		if !known[local] {
			return nil, apperr.New(apperr.KindUsage, "--target names a collection the QMD config does not have")
		}
	}
	return collections, nil
}

func targetOf(name string, targets map[string]string) string {
	if target, renamed := targets[name]; renamed {
		return target
	}
	return name
}

// refuseSharedTargets stops two collections merging into one: paths could collide, and
// nothing could tell afterward which one a document came from.
func refuseSharedTargets(collections []migrate.Collection, targets map[string]string, extra string) error {
	seen := map[string]bool{extra: extra != ""}
	for _, collection := range collections {
		target := targetOf(collection.Name, targets)
		if seen[target] {
			return apperr.New(apperr.KindUsage, "two sources would go to the collection %s; give each its own with --target", target)
		}
		seen[target] = true
	}
	return nil
}

type migratePlanResult struct {
	Collections []migrate.Plan       `json:"collections"`
	Codex       *migrate.RawSessions `json:"rawCodexSessions,omitempty"`
}

func (a *app) migratePlan(ctx context.Context, o migratePlanOptions) error {
	targets, err := parseTargets(o.targets)
	if err != nil {
		return err
	}
	collections, err := loadCollections(o.qmdConfig, targets)
	if err != nil {
		return err
	}
	if !sources.ValidCollection(o.codexTarget) {
		return apperr.New(apperr.KindUsage, "--codex-target must be a name with no slash or surrounding space")
	}
	extra := ""
	if o.codexDir != "" {
		extra = o.codexTarget
	}
	if err := refuseSharedTargets(collections, targets, extra); err != nil {
		return err
	}
	result := migratePlanResult{Collections: make([]migrate.Plan, 0, len(collections))}
	for _, collection := range collections {
		plan, _ := migrate.Inventory(collection, targetOf(collection.Name, targets))
		result.Collections = append(result.Collections, plan)
	}
	if o.codexDir != "" {
		raw := migrate.InventoryRaw(o.codexDir, o.codexTarget)
		result.Codex = &raw
	}
	raw, err := json.Marshal(result)
	if err != nil {
		return err
	}
	return a.present(ctx, output.Table, raw, map[output.Format]func(io.Writer) error{
		output.Markdown: func(w io.Writer) error { return output.MigratePlanMarkdown(w, result.Collections, result.Codex) },
		output.Table:    func(w io.Writer) error { return output.MigratePlanTable(w, result.Collections, result.Codex) },
	})
}

type migrateReportOptions struct {
	qmdConfig  string
	tool       string
	noGitleaks bool
}

func newMigrateReportCmd(a *app) *cobra.Command {
	var o migrateReportOptions
	cmd := &cobra.Command{
		Use:   "report --qmd-config <index.yml> --tool <RedactionCheck.dll>",
		Short: "Count what redaction would replace. Reports no value and no path",
		Long: `Run the redaction rules over exactly the files 'migrate plan' counts, and report how
many replacements each rule made and in how many files. It reports counts and rule
names, and never a value, a path or a line.

The rules live in the server's code, so this runs the RedactionCheck tool from a
checkout of the repository, which needs the .NET runtime:

  dotnet build tools/RedactionCheck -c Release

Gitleaks then looks over the redacted copies for anything left, so it must be on
PATH, or pass --no-gitleaks to go without that second look. The tool makes those
copies in a temporary folder, and deletes them on every exit path. This exits 1 if a
document was refused, or gitleaks found something.`,
		Example: `  memoria migrate report --qmd-config ~/.config/qmd/index.yml --tool tools/RedactionCheck/bin/Release/net10.0/RedactionCheck.dll`,
		Args:    noArgs,
		RunE:    func(cmd *cobra.Command, _ []string) error { return a.migrateReport(cmd.Context(), o) },
	}
	f := cmd.Flags()
	f.StringVar(&o.qmdConfig, "qmd-config", "", "Path to QMD's index.yml (required). There is no default")
	f.StringVar(&o.tool, "tool", "", "Path to the built RedactionCheck.dll (required)")
	f.BoolVar(&o.noGitleaks, "no-gitleaks", false, "Skip the second look at the redacted copies")
	return cmd
}

func (a *app) migrateReport(ctx context.Context, o migrateReportOptions) error {
	if err := checkReportTools(o); err != nil {
		return err
	}
	collections, err := loadCollections(o.qmdConfig, nil)
	if err != nil {
		return err
	}
	reports, err := a.runRedactionCheck(ctx, o, collections)
	if err != nil {
		return err
	}
	raw, err := json.Marshal(reports)
	if err != nil {
		return err
	}
	if err := a.present(ctx, output.Table, raw, map[output.Format]func(io.Writer) error{
		output.Markdown: func(w io.Writer) error { return output.MigrateReportMarkdown(w, reports) },
		output.Table:    func(w io.Writer) error { return output.MigrateReportTable(w, reports) },
	}); err != nil {
		return err
	}
	return uncleanError(reports)
}

func uncleanError(reports []migrate.Report) error {
	for i := range reports {
		if !reports[i].Clean() {
			return apperr.New(apperr.KindGeneric, "a document was refused, or gitleaks found something in the redacted copies; see the counts above")
		}
	}
	return nil
}

func checkReportTools(o migrateReportOptions) error {
	if info, err := os.Stat(o.tool); o.tool == "" || err != nil || info.IsDir() {
		return apperr.New(apperr.KindUsage, "--tool must name a built RedactionCheck.dll; build it with 'dotnet build tools/RedactionCheck -c Release'")
	}
	if !o.noGitleaks {
		if _, err := exec.LookPath("gitleaks"); err != nil {
			return apperr.New(apperr.KindUsage, "gitleaks is not on PATH; install it, or pass --no-gitleaks to go without that second look")
		}
	}
	return nil
}

// runRedactionCheck gives the tool a list of exactly the files the plan counts, so the two
// always agree. Those lists hold paths, so they stay in a private folder, deleted after.
func (a *app) runRedactionCheck(ctx context.Context, o migrateReportOptions, collections []migrate.Collection) ([]migrate.Report, error) {
	work, err := os.MkdirTemp("", "memoria-migrate-")
	if err != nil {
		return nil, apperr.Wrap(apperr.KindGeneric, err, "a temporary folder could not be made")
	}
	defer func() { _ = os.RemoveAll(work) }()

	args := []string{o.tool, "--json"}
	if o.noGitleaks {
		args = append(args, "--no-gitleaks")
	}
	reports := make([]migrate.Report, 0, len(collections))
	for i, collection := range collections {
		plan, files := migrate.Inventory(collection, collection.Name)
		if plan.Problem != "" {
			return nil, apperr.New(apperr.KindUsage, "collection %s: %s", collection.Name, plan.Problem)
		}
		list := filepath.Join(work, fmt.Sprintf("%d.txt", i))
		if err := os.WriteFile(list, []byte(strings.Join(files, "\n")), 0o600); err != nil {
			return nil, apperr.Wrap(apperr.KindGeneric, err, "a list of files could not be written")
		}
		args = append(args, "--manifest", list)
		reports = append(reports, migrate.Report{Name: collection.Name})
	}
	stdout, err := a.redactionCheck(ctx, args)
	if err != nil {
		return nil, err
	}
	results, err := migrate.ParseCheck(stdout, len(collections))
	if err != nil {
		return nil, apperr.Wrap(apperr.KindGeneric, err, err.Error())
	}
	for i := range reports {
		reports[i].CheckResult = results[i]
	}
	return reports, nil
}

func exitOneIsAResult(err error) bool {
	var exit *exec.ExitError
	return errors.As(err, &exit) && exit.ExitCode() == 1
}

// runDotnet runs the tool. Its stderr is left behind, because a failure there could quote a
// path or text. An exit of 1 only says the counts are not clean, and they are still the result.
func runDotnet(ctx context.Context, args []string) ([]byte, error) {
	command := exec.CommandContext(ctx, "dotnet", args...)
	var stdout bytes.Buffer
	command.Stdout = &stdout
	err := command.Run()
	if err == nil || exitOneIsAResult(err) {
		return stdout.Bytes(), nil
	}
	return nil, apperr.New(apperr.KindGeneric, "the redaction check did not finish; run it by hand for its own message")
}
