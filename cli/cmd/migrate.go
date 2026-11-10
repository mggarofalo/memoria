package cmd

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"os"
	"os/exec"
	"strings"

	"github.com/spf13/cobra"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/migrate"
	"github.com/mggarofalo/memoria/cli/internal/output"
	"github.com/mggarofalo/memoria/cli/internal/sources"
)

const (
	defaultCodexTarget = "codex-sessions"
)

func newMigrateCmd(a *app) *cobra.Command {
	cmd := &cobra.Command{
		Use:   "migrate",
		Short: "Move a local QMD setup to a Memoria server",
		Long: `Move a local QMD setup to a Memoria server.

The plan, report, push and verify commands only read your files, and only the paths you
give them, and never open QMD's index.sqlite. Only 'push --apply' sends anything. Then
'cutover' points your clients at the server, and 'rollback' puts them back. See
docs/migration.md for the order.`,
		Args: noArgs,
		RunE: func(cmd *cobra.Command, _ []string) error { return cmd.Help() },
	}
	cmd.AddCommand(newMigratePlanCmd(a), newMigrateReportCmd(a), newMigratePushCmd(a), newMigrateVerifyCmd(a), newMigrateRerenderCmd(a), newMigrateCutoverCmd(a), newMigrateRollbackCmd(a))
	return cmd
}

type migratePlanOptions struct {
	qmdConfig   string
	targets     []string
	codexDirs   []string
	codexTarget string
	acceptSkips bool
}

func newMigratePlanCmd(a *app) *cobra.Command {
	var o migratePlanOptions
	cmd := &cobra.Command{
		Use:   "plan --qmd-config <index.yml>",
		Short: "Count what each QMD collection holds, and where it would go",
		Long: `Read QMD's index.yml and count what QMD would index in each collection: files, bytes,
the largest file, and how many are over a server's default 2 MiB limit on a document.
It reads each selected file to judge it, and keeps none of its text. A blank file is left
out and counted, as QMD does not index one.

A migration will need each target collection to be empty, so choose names that are.

A pattern or ignore rule this cannot read the way QMD would, such as an extglob, a
negation or brackets, is refused rather than guessed at. This exits 1 if any collection
could not be counted, or if anything was skipped, unless you pass --accept-skips for
the skips.

It also counts the selected files a server would always refuse at their path, by reason:
characters or device names Windows forbids, invisible characters, a name not ending in
.md, a folder QMD excludes, names differing only in case, text that is not valid UTF-8, or
a modification time over an hour ahead. Those exit 1 as well, and
--accept-skips does not excuse them, as they will not be stored. A name that looks like a
credential needs the server's own rules, so 'migrate report' counts those.`,
		Example: `  memoria migrate plan --qmd-config ~/.config/qmd/index.yml
  memoria migrate plan --qmd-config index.yml --target notes=archive-notes --codex-sessions ~/.codex/sessions`,
		Args: noArgs,
		RunE: func(cmd *cobra.Command, _ []string) error { return a.migratePlan(cmd.Context(), o) },
	}
	f := cmd.Flags()
	f.StringVar(&o.qmdConfig, "qmd-config", "", "Path to QMD's index.yml (required). There is no default")
	f.StringArrayVar(&o.targets, "target", nil, "Send a collection to another name, as local=target; repeatable. Each keeps its name otherwise")
	// An array so a second one is seen and refused: see planCodexDir.
	f.StringArrayVar(&o.codexDirs, "codex-sessions", nil, "Also count the raw Codex transcripts in this folder. One folder; run plan again for another")
	f.StringVar(&o.codexTarget, "codex-target", defaultCodexTarget, "The collection raw Codex sessions would go to")
	f.BoolVar(&o.acceptSkips, "accept-skips", false, "Exit 0 even if a file or folder could not be read. They stay counted")
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

// planCodexDir is the raw Codex folder plan counts, or "" for none, once its target is
// known to be valid and to clash with no collection's. Given twice, a plain string flag
// would keep only the last and count nothing of the first, so that is refused.
func planCodexDir(o migratePlanOptions, collections []migrate.Collection, targets map[string]string) (string, error) {
	if !sources.ValidCollection(o.codexTarget) {
		return "", apperr.New(apperr.KindUsage, "--codex-target must be a name with no slash or surrounding space")
	}
	if len(o.codexDirs) > 1 {
		return "", apperr.New(apperr.KindUsage, "--codex-sessions takes one folder here, and given more than once only the last would be counted: run plan once for each folder")
	}
	if len(o.codexDirs) == 0 {
		return "", refuseSharedTargets(collections, targets, "")
	}
	return o.codexDirs[0], refuseSharedTargets(collections, targets, o.codexTarget)
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
	codexDir, err := planCodexDir(o, collections, targets)
	if err != nil {
		return err
	}
	result := migratePlanResult{Collections: make([]migrate.Plan, 0, len(collections))}
	for _, collection := range collections {
		plan, _ := migrate.Inventory(collection, targetOf(collection.Name, targets))
		result.Collections = append(result.Collections, plan)
	}
	if codexDir != "" {
		raw := migrate.InventoryRaw(codexDir, o.codexTarget)
		result.Codex = &raw
	}
	raw, err := json.Marshal(result)
	if err != nil {
		return err
	}
	if err := a.present(ctx, output.Table, raw, map[output.Format]func(io.Writer) error{
		output.Markdown: func(w io.Writer) error { return output.MigratePlanMarkdown(w, result.Collections, result.Codex) },
		output.Table:    func(w io.Writer) error { return output.MigratePlanTable(w, result.Collections, result.Codex) },
	}); err != nil {
		return err
	}
	a.warnOfEmptyPlans(result.Collections)
	return planOutcome(result, o.acceptSkips)
}

func (a *app) warnOfEmptyPlans(plans []migrate.Plan) {
	for i := range plans {
		if plans[i].Problem == "" {
			a.warnIfNoFiles(plans[i].Name, plans[i].Files, plans[i].Unreadable)
		}
	}
}

// warnIfNoFiles keeps a folder with nothing to take from passing for one that was read and found clean.
func (a *app) warnIfNoFiles(name string, files, unread int) {
	if files == 0 && unread == 0 {
		a.warn("collection " + name + ": no file in its folder matches its pattern")
	}
}

// planOutcome makes a plan that could not see everything fail, as its counts would read as the whole of it.
func planOutcome(result migratePlanResult, acceptSkips bool) error {
	skipped := 0
	for i := range result.Collections {
		if problem := result.Collections[i].Problem; problem != "" {
			return apperr.New(apperr.KindGeneric, "collection %s: %s", result.Collections[i].Name, problem)
		}
		skipped += result.Collections[i].Unreadable
	}
	if result.Codex != nil {
		if result.Codex.Problem != "" {
			return apperr.New(apperr.KindGeneric, "raw Codex sessions: %s", result.Codex.Problem)
		}
		skipped += result.Codex.Unreadable
	}
	if err := unstorableError(result.Collections); err != nil {
		return err
	}
	return skipsError(skipped, acceptSkips)
}

// unstorableError says before any push what no push can store.
func unstorableError(plans []migrate.Plan) error {
	total := 0
	for i := range plans {
		for _, count := range plans[i].Unstorable {
			total += count
		}
	}
	if total == 0 {
		return nil
	}
	return apperr.New(apperr.KindGeneric, "%d selected file(s) cannot be stored as they are; see the reasons above. Fix them, or exclude them with an ignore rule", total)
}

// skipsError is one rule for both commands: something unread is not something clean.
func skipsError(skipped int, acceptSkips bool) error {
	if skipped == 0 || acceptSkips {
		return nil
	}
	return apperr.New(apperr.KindGeneric, "%d file(s) or folder(s) could not be read, so this does not cover them. Fix that, or pass --accept-skips to go on without them", skipped)
}

type migrateReportOptions struct {
	qmdConfig   string
	tool        string
	noGitleaks  bool
	acceptSkips bool
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

Gitleaks then looks over the redacted text for anything left, so it must be on PATH.
It is best effort: it can miss a token at a chunk boundary, so 0 findings is not proof
that nothing is left. A document gitleaks could not read is counted as unscanned.
--no-gitleaks goes without that second look, which makes this a weaker check: it only
says what the rules replaced, and nothing about what they missed. The text goes to
gitleaks on its standard input, so no copy is written to disk.

This exits 1 if a document was refused, gitleaks found something or could not read a
document, or anything could not be read. Those last are counted, never named, and
--accept-skips lets the exit be 0 for them alone.`,
		Example: `  memoria migrate report --qmd-config ~/.config/qmd/index.yml --tool tools/RedactionCheck/bin/Release/net10.0/RedactionCheck.dll`,
		Args:    noArgs,
		RunE:    func(cmd *cobra.Command, _ []string) error { return a.migrateReport(cmd.Context(), o) },
	}
	f := cmd.Flags()
	f.StringVar(&o.qmdConfig, "qmd-config", "", "Path to QMD's index.yml (required). There is no default")
	f.StringVar(&o.tool, "tool", "", "Path to the built RedactionCheck.dll (required)")
	f.BoolVar(&o.noGitleaks, "no-gitleaks", false, "Skip the second look at the redacted text, which makes this a weaker check")
	f.BoolVar(&o.acceptSkips, "accept-skips", false, "Exit 0 even if a file or folder could not be read. They stay counted")
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
	if o.noGitleaks {
		a.warn("--no-gitleaks makes this a weaker check: it says what the rules replaced, and nothing about what they missed")
	}
	if !o.noGitleaks {
		a.warn("gitleaks is best effort: it can miss a token at a chunk boundary, so 0 findings is not proof that nothing is left")
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
	for i := range reports {
		a.warnIfNoFiles(reports[i].Name, reports[i].Files, reports[i].Skipped)
	}
	return uncleanError(reports, o.acceptSkips)
}

func uncleanError(reports []migrate.Report, acceptSkips bool) error {
	skipped := 0
	for i := range reports {
		if !reports[i].Clean() {
			return apperr.New(apperr.KindGeneric, "a document was refused, a file's name looks like a credential, or gitleaks found something in the redacted text or could not read it; see the counts above")
		}
		skipped += reports[i].Skipped
	}
	return skipsError(skipped, acceptSkips)
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

// runRedactionCheck gives the tool a list of exactly the files the plan counts, so the two always agree.
// The lists hold paths, so they go on its standard input: nothing is written, and a killed run leaves nothing.
func (a *app) runRedactionCheck(ctx context.Context, o migrateReportOptions, collections []migrate.Collection) ([]migrate.Report, error) {
	args := []string{o.tool, "--json", "--lists-on-stdin"}
	if o.noGitleaks {
		args = append(args, "--no-gitleaks")
	}
	reports := make([]migrate.Report, 0, len(collections))
	unread := make([]int, 0, len(collections))
	lists := make([]migrate.CheckList, 0, len(collections))
	for _, collection := range collections {
		plan, files := migrate.Inventory(collection, collection.Name)
		if plan.Problem != "" {
			return nil, apperr.New(apperr.KindUsage, "collection %s: %s", collection.Name, plan.Problem)
		}
		unread = append(unread, plan.Unreadable)
		lists = append(lists, migrate.CheckList{Root: collection.Path, Paths: append([]string{}, files...)})
		reports = append(reports, migrate.Report{Name: collection.Name})
	}
	stdin, err := json.Marshal(lists)
	if err != nil {
		return nil, err
	}
	stdout, err := a.redactionCheck(ctx, args, stdin)
	if err != nil {
		return nil, err
	}
	results, err := migrate.ParseCheck(stdout, len(collections))
	if err != nil {
		return nil, apperr.Wrap(apperr.KindGeneric, err, err.Error())
	}
	for i := range reports {
		reports[i].CheckResult = results[i]
		reports[i].Skipped += unread[i]
	}
	return reports, nil
}

func exitOneIsAResult(err error) bool {
	var exit *exec.ExitError
	return errors.As(err, &exit) && exit.ExitCode() == 1
}

// runDotnet runs the tool. Its stderr is left behind, because a failure there could quote a
// path or text. An exit of 1 only says the counts are not clean, and they are still the result.
func runDotnet(ctx context.Context, args []string, stdin []byte) ([]byte, error) {
	command := exec.CommandContext(ctx, "dotnet", args...)
	command.Stdin = bytes.NewReader(stdin)
	var stdout bytes.Buffer
	command.Stdout = &stdout
	err := command.Run()
	if err == nil || exitOneIsAResult(err) {
		return stdout.Bytes(), nil
	}
	return nil, apperr.New(apperr.KindGeneric, "the redaction check did not finish; run it by hand for its own message")
}
