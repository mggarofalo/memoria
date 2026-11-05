package cmd

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"time"

	"github.com/spf13/cobra"

	"github.com/mggarofalo/memoria/cli/internal/api"
	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/output"
	"github.com/mggarofalo/memoria/cli/internal/sources"
)

const (
	// A crash loses at most this many entries from the cursor, and sending those again is harmless.
	saveEvery = 25

	// A server that asks for longer than this is left alone, and the next sync resumes.
	maxRetryWait = 2 * time.Minute
	maxRetries   = 5
)

var errFileUnreadable = errors.New("it could not be read")

type syncOptions struct {
	apply  bool
	dryRun bool
}

func newSourcesSyncCmd(a *app) *cobra.Command {
	var o syncOptions
	cmd := &cobra.Command{
		Use:   "sync [name...]",
		Short: "Send what changed. The first run for a source is a dry run",
		Long: `Send the Markdown files that changed since the last sync, with every source or the
ones you name.

The first run for a source on a server only reports what it would send, and needs
no key and no network. Pass --apply to send. After that, sync sends without it.

A run stops at the first sign the server cannot be reached and saves its place, so
running it again continues where it left off. Sending the same file twice is safe.
A file the server refuses is reported and does not stop the others.

Sync never deletes anything on the server. A file gone from the folder is counted
as missing locally, because an unmounted drive looks just like every file deleted.
Hidden files, links, and anything but Markdown are left out.`,
		Example: `  memoria sources sync
  memoria sources sync notes --apply
  memoria sources sync --dry-run`,
		RunE: func(cmd *cobra.Command, args []string) error { return a.sourcesSync(cmd.Context(), args, o) },
	}
	f := cmd.Flags()
	f.BoolVar(&o.apply, "apply", false, "Send, even on the first run")
	f.BoolVar(&o.dryRun, "dry-run", false, "Only report what would be sent")
	return cmd
}

func chosenSources(names []string) ([]sources.Source, error) {
	config, err := sources.Load()
	if err != nil {
		return nil, err
	}
	if len(config.Sources) == 0 {
		return nil, apperr.New(apperr.KindUsage, "no sources are configured; add one with 'memoria sources add'")
	}
	if len(names) == 0 {
		return config.Sources, nil
	}
	chosen := make([]sources.Source, 0, len(names))
	for _, name := range names {
		source, found := config.Find(name)
		if !found {
			return nil, apperr.New(apperr.KindNotFound, "no source has that name; see 'memoria sources list'")
		}
		chosen = append(chosen, source)
	}
	return chosen, nil
}

func (a *app) sourcesSync(ctx context.Context, names []string, o syncOptions) error {
	if o.apply && o.dryRun {
		return apperr.New(apperr.KindUsage, "use --apply or --dry-run, not both")
	}
	chosen, err := chosenSources(names)
	if err != nil {
		return err
	}
	origin, err := a.localOrigin()
	if err != nil {
		return err
	}
	run := &syncRun{a: a, ctx: ctx, origin: origin, options: o}
	reports := make([]sources.Report, 0, len(chosen))
	var stopped error
	failed := 0
	for _, source := range chosen {
		report, err := run.source(source)
		reports = append(reports, report)
		failed += report.Failed
		if err != nil {
			stopped = err
			break
		}
	}
	return firstOf(stopped, a.presentReports(ctx, reports), failedError(failed))
}

func (a *app) presentReports(ctx context.Context, reports []sources.Report) error {
	raw, err := json.Marshal(reports)
	if err != nil {
		return err
	}
	return a.present(ctx, output.Table, raw, map[output.Format]func(io.Writer) error{
		output.Markdown: func(w io.Writer) error { return output.SyncMarkdown(w, reports) },
		output.Table:    func(w io.Writer) error { return output.SyncTable(w, reports) },
	})
}

// syncRun connects only once something needs sending, so a run with nothing to do
// works offline and with no key.
type syncRun struct {
	a       *app
	ctx     context.Context
	origin  string
	options syncOptions
	session *session
}

func (r *syncRun) source(source sources.Source) (sources.Report, error) {
	report := sources.Report{Name: source.Name, Collection: source.Collection}
	tree, err := sources.OpenTree(source.Path)
	if err != nil {
		// The cursor is left alone: this may be a drive that is not mounted.
		return report, apperr.New(apperr.KindUsage, "source %s: its folder cannot be opened", source.Name)
	}
	defer func() { _ = tree.Close() }()

	state, exists, err := sources.LoadState(r.origin, source)
	if err != nil {
		return report, err
	}
	paths, skipped, err := tree.List()
	if err != nil {
		return report, apperr.Wrap(apperr.KindGeneric, err, "source "+source.Name+": its folder could not be listed")
	}
	plan := state.Plan(tree, paths)
	report.Unchanged, report.Missing, report.Skipped = plan.Unchanged, plan.Missing, append(skipped, plan.Skipped...)
	r.noteSkipped(source, report.Skipped)

	if r.options.dryRun || (!r.options.apply && !exists) {
		report.DryRun, report.WouldSend = true, plan.Changes
		r.a.info(dryRunHint(source, r.options.dryRun))
		return report, nil
	}
	return report, r.send(source, tree, state, plan.Changes, &report)
}

func (r *syncRun) noteSkipped(source sources.Source, skipped []sources.Skipped) {
	for _, entry := range skipped {
		r.a.warn(fmt.Sprintf("%s: left out %s: %s", source.Name, entry.Path, entry.Reason))
	}
}

func dryRunHint(source sources.Source, asked bool) string {
	if asked {
		return source.Name + ": dry run, so nothing was sent."
	}
	return source.Name + ": first run, so nothing was sent. Pass --apply to send."
}

func (r *syncRun) send(source sources.Source, tree *sources.Tree, state *sources.State, changes []sources.Change, report *sources.Report) error {
	// Saved first, so a run that was asked to send counts as having started, however it ends.
	if err := state.Save(); err != nil {
		return err
	}
	for i, change := range changes {
		err := r.sendOne(source, tree, state, change, report)
		if err != nil && !fileOnly(err) {
			report.Remaining = len(changes) - i
			return firstOf(stopMessage(err, report), state.Save())
		}
		if err != nil {
			report.Failed++
			r.a.warn(fmt.Sprintf("%s: %s was not sent: %s", source.Name, change.Path, err.Error()))
			continue
		}
		if report.Sent%saveEvery == 0 {
			if err := state.Save(); err != nil {
				return err
			}
		}
	}
	now := time.Now().UTC()
	state.LastSync = &now
	return state.Save()
}

// stopMessage keeps the error's kind, and so its exit code, and says the place is saved.
func stopMessage(err error, report *sources.Report) error {
	var classified *apperr.Error
	if errors.As(err, &classified) {
		stopped := *classified
		stopped.Message = fmt.Sprintf("%s; %d sent and %d not reached. The place is saved, so sync again to continue", classified.Message, report.Sent, report.Remaining)
		return &stopped
	}
	return err
}

// fileOnly separates a refusal of this one file from a sign that nothing else will get
// through either. Everything not named here stops the run, which is safe: it resumes.
//
// A 404 is not here: this route exists on every server that can take the file, so a 404
// means an older server, or one with authentication off, and every file would get it.
func fileOnly(err error) bool {
	if errors.Is(err, errFileUnreadable) {
		return true
	}
	var classified *apperr.Error
	if !errors.As(err, &classified) {
		return false
	}
	switch classified.Status {
	case http.StatusBadRequest, http.StatusRequestTimeout, http.StatusConflict, http.StatusRequestEntityTooLarge, http.StatusUnprocessableEntity:
		return true
	}
	return false
}

func (r *syncRun) sendOne(source sources.Source, tree *sources.Tree, state *sources.State, change sources.Change, report *sources.Report) error {
	content, err := tree.Read(change.Path)
	if err != nil {
		return errFileUnreadable
	}
	version := state.Version(change.Path, content.ModTime)
	result, err := r.post(api.DocumentIngestRequest{
		Collection: &source.Collection,
		Source:     &source.Name,
		SourceId:   &change.Path,
		Title:      &change.Path,
		Body:       &content.Body,
		Updated:    &version,
	})
	if err != nil {
		return err
	}
	state.Record(change.Path, content.SHA256, version)
	report.Sent++
	noteRedactions(report, result)
	return nil
}

func noteRedactions(report *sources.Report, result *api.DocumentIngestResult) {
	if result.Redaction == "disabled" {
		report.Unredacted++
	}
	for rule, count := range result.Redactions {
		if report.Redactions == nil {
			report.Redactions = map[string]int{}
		}
		report.Redactions[rule] += int(count)
	}
}

func (r *syncRun) connected() (*session, error) {
	if r.session == nil {
		s, err := r.a.connect()
		if err != nil {
			return nil, err
		}
		r.session = s
	}
	return r.session, nil
}

func (r *syncRun) post(request api.DocumentIngestRequest) (*api.DocumentIngestResult, error) {
	s, err := r.connected()
	if err != nil {
		return nil, err
	}
	for attempt := 0; ; attempt++ {
		resp, err := s.client.IngestDocumentWithResponse(r.ctx, request)
		if err != nil {
			return nil, apperr.FromTransport(err)
		}
		if resp.StatusCode() == http.StatusAccepted {
			if err := requireShape(resp.JSON202 != nil && resp.JSON202.Path != ""); err != nil {
				return nil, err
			}
			return resp.JSON202, nil
		}
		refused := expectStatus(http.StatusAccepted, resp.HTTPResponse, resp.Body)
		wait, again := retryWait(resp.HTTPResponse, attempt)
		if !again {
			return nil, refused
		}
		r.a.info("The server is busy; waiting " + wait.String() + ".")
		if err := r.a.sleep(r.ctx, wait); err != nil {
			return nil, apperr.Wrap(apperr.KindGeneric, err, "stopped while waiting for the server")
		}
	}
}

// retryWait waits only when the server names a short time to come back. Without one, or
// with a long one, stopping and resuming later serves better than holding a terminal.
func retryWait(resp *http.Response, attempt int) (time.Duration, bool) {
	if resp.StatusCode != http.StatusTooManyRequests && resp.StatusCode != http.StatusServiceUnavailable {
		return 0, false
	}
	wait := apperr.ParseRetryAfter(resp.Header.Get("Retry-After"), time.Now())
	return wait, wait > 0 && wait <= maxRetryWait && attempt < maxRetries
}
