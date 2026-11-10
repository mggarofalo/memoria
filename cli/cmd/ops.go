package cmd

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"regexp"

	"github.com/spf13/cobra"

	"github.com/mggarofalo/memoria/cli/internal/api"
	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/output"
)

// The server and the sidecar refuse anything else; checking here keeps a mistyped
// argument out of a URL path and gives a clearer message.
var (
	workerNamePattern = regexp.MustCompile(`^[A-Za-z0-9_.:\[\]-]{1,64}$`)
	jobIDPattern      = regexp.MustCompile(`^[A-Za-z0-9-]{1,64}$`)
)

const opsNeeds = `These commands need a key with the admin scope that is not bound to particular
collections: an admin-only key, or one with --all-collections. They act on every
collection at once, so a key that lists collections is refused.`

func oneWorkerName(cmd *cobra.Command, args []string) error {
	if len(args) != 1 {
		return apperr.New(apperr.KindUsage, "%q takes exactly one worker name", cmd.CommandPath())
	}
	if !workerNamePattern.MatchString(args[0]) || args[0] == "." || args[0] == ".." {
		return apperr.New(apperr.KindUsage, "a worker name is 1 to 64 letters, digits, dots, dashes or underscores, or a host and port, as 'memoria workers list' shows it")
	}
	return nil
}

func oneJobID(cmd *cobra.Command, args []string) error {
	if len(args) != 1 {
		return apperr.New(apperr.KindUsage, "%q takes exactly one job id", cmd.CommandPath())
	}
	if !jobIDPattern.MatchString(args[0]) {
		return apperr.New(apperr.KindUsage, "that is not a job id. Pass an id from 'memoria queue status'")
	}
	return nil
}

// hasField reports whether a JSON object body carries the key, so that null or {} is
// not read as a state whose every field is false.
func hasField(body []byte, key string) bool {
	var fields map[string]json.RawMessage
	if json.Unmarshal(body, &fields) != nil {
		return false
	}
	_, ok := fields[key]
	return ok
}

// presentLine shows a one-line result as table or markdown, and the response itself as json.
// The line is built for the format, so server text in it is escaped the way that format needs.
func (a *app) presentLine(ctx context.Context, raw []byte, line func(output.Escape) string) error {
	write := func(esc output.Escape) func(io.Writer) error {
		return func(w io.Writer) error {
			_, err := io.WriteString(w, line(esc)+"\n")
			return err
		}
	}
	return a.present(ctx, output.Table, raw, map[output.Format]func(io.Writer) error{output.Table: write(output.Plain), output.Markdown: write(output.Inline)})
}

// --- workers ---------------------------------------------------------------------

func newWorkersCmd(a *app) *cobra.Command {
	cmd := &cobra.Command{
		Use:   "workers",
		Short: "See and drain the inference workers (needs an admin key)",
		Long: `List the GPU workers the sidecar sends model work to, and take one out of
rotation or put it back.

` + opsNeeds + `

A drain is held in the sidecar's memory: restarting the sidecar puts every worker
back. To keep a worker out, remove it from the sidecar's configuration.`,
		Args: noArgs,
		RunE: func(cmd *cobra.Command, _ []string) error { return cmd.Help() },
	}
	cmd.AddCommand(newWorkersListCmd(a), newWorkerActionCmd(a, "drain"), newWorkerActionCmd(a, "enable"))
	return cmd
}

func newWorkersListCmd(a *app) *cobra.Command {
	return &cobra.Command{
		Use:   "list",
		Short: "List workers with their state, circuits, load and errors",
		Args:  noArgs,
		RunE:  func(cmd *cobra.Command, _ []string) error { return a.workersList(cmd.Context()) },
	}
}

func (a *app) workersList(ctx context.Context) error {
	s, err := a.connect()
	if err != nil {
		return err
	}
	resp, err := s.client.ListWorkersWithResponse(ctx)
	if err != nil {
		return apperr.FromTransport(err)
	}
	if err := expectStatus(http.StatusOK, resp.HTTPResponse, resp.Body); err != nil {
		return err
	}
	if err := requireShape(resp.JSON200 != nil && resp.JSON200.Workers != nil); err != nil {
		return err
	}
	list := resp.JSON200
	return a.present(ctx, output.Table, resp.Body, map[output.Format]func(io.Writer) error{
		output.Markdown: func(w io.Writer) error { return output.WorkersMarkdown(w, list) },
		output.Table:    func(w io.Writer) error { return output.WorkersTable(w, list) },
	})
}

var workerActions = map[string]struct {
	short, long string
}{
	"drain": {
		short: "Take a worker out of rotation. Its calls in flight finish",
		long:  "Take a worker out of rotation. Calls in flight finish and it gets no new ones; 'memoria workers list' shows them leave. A sidecar restart puts it back. With every worker drained, search runs degraded and embedding waits.",
	},
	"enable": {
		short: "Put a drained worker back in rotation",
		long:  "Put a drained worker back in rotation. Enabling a worker that is not drained changes nothing.",
	},
}

func newWorkerActionCmd(a *app, action string) *cobra.Command {
	text := workerActions[action]
	return &cobra.Command{
		Use:   action + " <name>",
		Short: text.short,
		Long:  text.long,
		Args:  oneWorkerName,
		RunE:  func(cmd *cobra.Command, args []string) error { return a.workerAction(cmd.Context(), action, args[0]) },
	}
}

func (a *app) workerAction(ctx context.Context, action, name string) error {
	s, err := a.connect()
	if err != nil {
		return err
	}
	var status int
	var header http.Header
	var body []byte
	var worker *api.WorkerInfo
	if action == "drain" {
		resp, err := s.client.DrainWorkerWithResponse(ctx, name)
		if err != nil {
			return apperr.FromTransport(err)
		}
		status, header, body, worker = resp.StatusCode(), resp.HTTPResponse.Header, resp.Body, resp.JSON200
	} else {
		resp, err := s.client.EnableWorkerWithResponse(ctx, name)
		if err != nil {
			return apperr.FromTransport(err)
		}
		status, header, body, worker = resp.StatusCode(), resp.HTTPResponse.Header, resp.Body, resp.JSON200
	}
	if err := checkResponse(status, header, body); err != nil {
		return err
	}
	if err := requireShape(worker != nil && worker.Name != ""); err != nil {
		return err
	}
	return a.presentLine(ctx, body, func(esc output.Escape) string { return output.WorkerLine(worker, esc) })
}

// --- queue -----------------------------------------------------------------------

func newQueueCmd(a *app) *cobra.Command {
	cmd := &cobra.Command{
		Use:   "queue",
		Short: "Inspect and steer background indexing (needs an admin key)",
		Long: `See the background job queue, retry or cancel a job, and pause or resume
background work.

` + opsNeeds + `

A pause is held in the sidecar's memory: restarting the sidecar resumes the queue.`,
		Args: noArgs,
		RunE: func(cmd *cobra.Command, _ []string) error { return cmd.Help() },
	}
	cmd.AddCommand(newQueueStatusCmd(a), newJobActionCmd(a, "retry"), newJobActionCmd(a, "cancel"), newQueueToggleCmd(a, "pause"), newQueueToggleCmd(a, "resume"))
	return cmd
}

func newQueueStatusCmd(a *app) *cobra.Command {
	var limit int
	cmd := &cobra.Command{
		Use:   "status",
		Short: "Jobs by state, whether the queue is paused, and recent jobs",
		Long:  "Show jobs by state, whether the queue is paused and persists across restarts, documents set aside, model calls waiting, and the most recent jobs with their ids, for 'queue retry' and 'queue cancel'.",
		Args:  noArgs,
		RunE:  func(cmd *cobra.Command, _ []string) error { return a.queueStatus(cmd.Context(), limit) },
	}
	cmd.Flags().IntVar(&limit, "jobs", 10, "Most recent jobs to list; 0 lists every one the sidecar remembers")
	return cmd
}

func (a *app) queueStatus(ctx context.Context, limit int) error {
	if limit < 0 {
		return apperr.New(apperr.KindUsage, "--jobs must be 0 or more")
	}
	s, err := a.connect()
	if err != nil {
		return err
	}
	resp, err := s.client.GetQueueStatusWithResponse(ctx)
	if err != nil {
		return apperr.FromTransport(err)
	}
	if err := expectStatus(http.StatusOK, resp.HTTPResponse, resp.Body); err != nil {
		return err
	}
	if err := requireShape(resp.JSON200 != nil && resp.JSON200.Jobs != nil); err != nil {
		return err
	}
	status := resp.JSON200
	return a.present(ctx, output.Table, resp.Body, map[output.Format]func(io.Writer) error{
		output.Markdown: func(w io.Writer) error { return output.QueueMarkdown(w, status, limit) },
		output.Table:    func(w io.Writer) error { return output.QueueTable(w, status, limit) },
	})
}

var jobActions = map[string]struct {
	short, long, verb string
}{
	"retry": {
		short: "Queue a failed job again under the same id",
		long:  "Queue a failed job again under the same id. Only a failed job can be retried.",
		verb:  "Retried",
	},
	"cancel": {
		short: "Stop a job that has not finished",
		long:  "Stop a job that has not finished. A waiting job ends at once; a running one stops at its next safe point, so it may still show as running. A cancelled job reports failed, with the error Cancelled, and can be retried.",
		verb:  "Cancelled",
	},
}

func newJobActionCmd(a *app, action string) *cobra.Command {
	text := jobActions[action]
	return &cobra.Command{
		Use:   action + " <id>",
		Short: text.short,
		Long:  text.long,
		Args:  oneJobID,
		RunE:  func(cmd *cobra.Command, args []string) error { return a.jobAction(cmd.Context(), action, args[0]) },
	}
}

func (a *app) jobAction(ctx context.Context, action, id string) error {
	s, err := a.connect()
	if err != nil {
		return err
	}
	var resp *http.Response
	var body []byte
	var job *api.JobInfo
	if action == "retry" {
		r, err := s.client.RetryJobWithResponse(ctx, id)
		if err != nil {
			return apperr.FromTransport(err)
		}
		resp, body, job = r.HTTPResponse, r.Body, r.JSON202
	} else {
		r, err := s.client.CancelJobWithResponse(ctx, id)
		if err != nil {
			return apperr.FromTransport(err)
		}
		resp, body, job = r.HTTPResponse, r.Body, r.JSON202
	}
	return a.presentJob(ctx, resp, body, job, jobActions[action].verb)
}

func (a *app) presentJob(ctx context.Context, resp *http.Response, body []byte, job *api.JobInfo, verb string) error {
	if err := expectStatus(http.StatusAccepted, resp, body); err != nil {
		return err
	}
	if err := requireShape(job != nil && job.Id != ""); err != nil {
		return err
	}
	return a.presentLine(ctx, body, func(esc output.Escape) string { return output.JobLine(verb, job, esc) })
}

var queueToggles = map[string]string{
	"pause":  "Start no more background jobs. A running job runs to its end, and writes still queue: until 'queue resume', new documents are not searchable and deleted ones still are",
	"resume": "Let waiting background jobs start again",
}

func newQueueToggleCmd(a *app, action string) *cobra.Command {
	return &cobra.Command{
		Use:   action,
		Short: queueToggles[action],
		Args:  noArgs,
		RunE:  func(cmd *cobra.Command, _ []string) error { return a.queueToggle(cmd.Context(), action) },
	}
}

func (a *app) queueToggle(ctx context.Context, action string) error {
	s, err := a.connect()
	if err != nil {
		return err
	}
	var resp *http.Response
	var body []byte
	var state *api.QueueState
	if action == "pause" {
		r, err := s.client.PauseQueueWithResponse(ctx)
		if err != nil {
			return apperr.FromTransport(err)
		}
		resp, body, state = r.HTTPResponse, r.Body, r.JSON200
	} else {
		r, err := s.client.ResumeQueueWithResponse(ctx)
		if err != nil {
			return apperr.FromTransport(err)
		}
		resp, body, state = r.HTTPResponse, r.Body, r.JSON200
	}
	if err := expectStatus(http.StatusOK, resp, body); err != nil {
		return err
	}
	if err := requireShape(state != nil && hasField(body, "paused")); err != nil {
		return err
	}
	return a.presentLine(ctx, body, func(esc output.Escape) string { return output.QueueLine(state, esc) })
}

// --- readiness -------------------------------------------------------------------

func newReadyCmd(a *app) *cobra.Command {
	var strict bool
	cmd := &cobra.Command{
		Use:   "ready",
		Short: "Is the server ready, and if not, why",
		Long: `Ask the server whether it can serve, and show why not. Any key gets the
status word: ready, warming, degraded or not_ready. A key with the admin scope
also gets the backend's report: workers up, roles no worker can serve, pending and
stuck jobs, and documents set aside. The trace id at the end finds this request in
the API's and the sidecar's logs.

It exits 0 when the server answers 200, and 5 when it answers 503: the sidecar
cannot be reached, or with --strict, anything but ready.`,
		Args: noArgs,
		RunE: func(cmd *cobra.Command, _ []string) error { return a.ready(cmd.Context(), strict) },
	}
	cmd.Flags().BoolVar(&strict, "strict", false, "Count degraded and warming as not ready")
	return cmd
}

func (a *app) ready(ctx context.Context, strict bool) error {
	s, err := a.connect()
	if err != nil {
		return err
	}
	params := &api.ReadyParams{}
	if strict {
		params.Strict = &strict
	}
	resp, err := s.client.ReadyWithResponse(ctx, params)
	if err != nil {
		return apperr.FromTransport(err)
	}
	report := resp.JSON200
	if resp.StatusCode() == http.StatusServiceUnavailable {
		report = resp.JSON503
	} else if err := checkResponse(resp.StatusCode(), resp.HTTPResponse.Header, resp.Body); err != nil {
		return err
	}
	if err := requireShape(report != nil && report.Status != ""); err != nil {
		return err
	}
	trace := resp.HTTPResponse.Header.Get("X-Trace-Id")
	if err := a.present(ctx, output.Table, resp.Body, map[output.Format]func(io.Writer) error{
		output.Markdown: func(w io.Writer) error { return output.ReadinessText(w, report, trace, output.Inline) },
		output.Table:    func(w io.Writer) error { return output.ReadinessText(w, report, trace, output.Plain) },
	}); err != nil {
		return err
	}
	if resp.StatusCode() == http.StatusServiceUnavailable {
		return apperr.New(apperr.KindUnavailable, "the server is not ready (HTTP 503)")
	}
	return nil
}

// --- reindex ---------------------------------------------------------------------

type reindexOptions struct {
	collections []string
	noEmbed     bool
	force       bool
}

func newReindexCmd(a *app) *cobra.Command {
	var o reindexOptions
	cmd := &cobra.Command{
		Use:   "reindex",
		Short: "Queue a rescan and embed of some or all collections (needs an admin key)",
		Long: `Queue a reindex: rescan the collections' directories and embed what changed.
With no --collection it covers every collection. Jobs run one at a time, and an
identical job already waiting is returned instead of a new one. Follow it with
'memoria queue status'.

--force re-embeds every document, not only changed ones. It is expensive: on a
large corpus it keeps the workers busy for a long time.

` + opsNeeds,
		Example: `  memoria reindex
  memoria reindex --collection claude-sessions
  memoria reindex --collection notes --no-embed`,
		Args: noArgs,
		RunE: func(cmd *cobra.Command, _ []string) error { return a.reindex(cmd.Context(), o) },
	}
	f := cmd.Flags()
	f.StringArrayVar(&o.collections, "collection", nil, "Collection to reindex; repeatable. Omit for every collection")
	f.BoolVar(&o.noEmbed, "no-embed", false, "Rescan only; do not embed")
	f.BoolVar(&o.force, "force", false, "Re-embed every document, not only changed ones. Expensive")
	return cmd
}

func (a *app) reindex(ctx context.Context, o reindexOptions) error {
	if o.noEmbed && o.force {
		return apperr.New(apperr.KindUsage, "--force re-embeds, so it cannot go with --no-embed")
	}
	embed := !o.noEmbed
	request := api.ReindexRequest{Collections: optionalList(o.collections), Embed: &embed}
	if o.force {
		request.Force = &o.force
	}
	s, err := a.connect()
	if err != nil {
		return err
	}
	resp, err := s.client.ReindexWithResponse(ctx, request)
	if err != nil {
		return apperr.FromTransport(err)
	}
	return a.presentJob(ctx, resp.HTTPResponse, resp.Body, resp.JSON202, "Queued")
}
