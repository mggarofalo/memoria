package cmd

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/spf13/cobra"

	"github.com/mggarofalo/memoria/cli/internal/api"
	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/auth"
	"github.com/mggarofalo/memoria/cli/internal/migrate"
	"github.com/mggarofalo/memoria/cli/internal/output"
	"github.com/mggarofalo/memoria/cli/internal/transport"
)

const codexFormat = "codex"

type migrateRerenderOptions struct {
	qmdConfig string
	targets   []string
	codexDirs []string
	apply     bool
}

func newMigrateRerenderCmd(a *app) *cobra.Command {
	var o migrateRerenderOptions
	cmd := &cobra.Command{
		Use:   "rerender --qmd-config <index.yml> --codex-sessions <folder>",
		Short: "Send over-cap Codex exports again from their raw transcripts. A dry run unless you pass --apply",
		Long: `A rendered document over the server's size cap cannot be stored as it is. For each one,
this finds the raw Codex transcript its frontmatter session_id names, in the folders given by
--codex-sessions, and sends that through the session route, which splits it into parts the cap
allows. Without --apply it sends nothing and needs no key.

A document is over the cap when it is larger than the server's default of 2 MiB, or when the
last push to this server reported it over the cap.

A transcript counts only if its session_meta names that session, as the server checks. If none
does, the over-cap export is that document's only copy, and this says so and exits 1. If more
than one does, it sends neither and says so.

Running it again is safe. It asks the server first, and a session it already holds complete, at
this transcript's version or later, is not sent again.

A Codex session takes over documents in the collection root whose frontmatter names it, with no
proof of which copy it is, as the server's rules allow. One that would lose messages is refused
with session_shrank, and nothing changes. This never sends allowShrink.

Only a private report names the files.`,
		Example: `  memoria migrate rerender --qmd-config ~/.config/qmd/index.yml --codex-sessions ~/.codex/sessions
  memoria migrate rerender --qmd-config index.yml --codex-sessions ~/.codex/sessions --apply --timeout 10m`,
		Args: noArgs,
		RunE: func(cmd *cobra.Command, _ []string) error { return a.migrateRerender(cmd.Context(), o) },
	}
	f := cmd.Flags()
	f.StringVar(&o.qmdConfig, "qmd-config", "", "Path to QMD's index.yml (required). There is no default")
	f.StringArrayVar(&o.targets, "target", nil, "A collection that went to another name, as local=target; repeatable")
	f.StringArrayVar(&o.codexDirs, "codex-sessions", nil, "A folder of raw Codex transcripts (required); repeatable, as for archived sessions")
	f.BoolVar(&o.apply, "apply", false, "Send. Without this, only report what would be sent")
	return cmd
}

// rerenderItem is one over-cap document and what was found for it.
type rerenderItem struct {
	name, sessionID, transcript string
	// newest is the latest file time among the session's exports here. Stored chunks carry their export's time as their version.
	newest time.Time
}

// sessionExports is what this collection's exports of one session say together: the newest file time, and the furthest message.
type sessionExports struct {
	newest time.Time
	reach  int64
	known  bool
}

// exportsOf reads the frontmatter of every selected file once, for the sessions these over-cap documents name.
func exportsOf(job pushJob, root *os.Root) map[string]*sessionExports {
	found := map[string]*sessionExports{}
	for _, name := range job.paths {
		head, updated, err := readHead(root, name)
		if err != nil {
			continue
		}
		id := migrate.SessionIDOf(head)
		if id == "" {
			continue
		}
		facts := found[id]
		if facts == nil {
			facts = &sessionExports{}
			found[id] = facts
		}
		facts.add(head, updated)
	}
	return found
}

func (s *sessionExports) add(head []byte, updated time.Time) {
	if updated.After(s.newest) {
		s.newest = updated
	}
	if reach, known := migrate.ReachOf(head); known {
		s.reach, s.known = max(s.reach, reach), true
	}
}

// headLimit matches what the frontmatter readers look at.
const headLimit = 64 * 1024

func readHead(root *os.Root, name string) ([]byte, time.Time, error) {
	updated, _, err := fileVersion(root, name)
	if err != nil {
		return nil, time.Time{}, err
	}
	file, err := root.Open(filepath.FromSlash(name))
	if err != nil {
		return nil, time.Time{}, err
	}
	defer func() { _ = file.Close() }()
	head, err := io.ReadAll(io.LimitReader(file, headLimit))
	return head, updated, err
}

type rerendering struct {
	job      pushJob
	summary  migrate.RerenderSummary
	problems []migrate.RerenderProblem
	items    []rerenderItem
}

func (g *rerendering) problem(name, status, reason string) {
	g.problems = append(g.problems, migrate.RerenderProblem{Collection: g.job.collection.Name, Path: name, Status: status, Reason: reason})
}

func (a *app) migrateRerender(ctx context.Context, o migrateRerenderOptions) error {
	if len(o.codexDirs) == 0 {
		return apperr.New(apperr.KindUsage, "--codex-sessions is required: it is where the raw transcripts are")
	}
	targets, err := parseTargets(o.targets)
	if err != nil {
		return err
	}
	collections, err := loadCollections(o.qmdConfig, targets)
	if err != nil {
		return err
	}
	jobs, err := selectForPush(collections, targets)
	if err != nil {
		return err
	}
	transcripts, err := migrate.FindTranscripts(o.codexDirs)
	if err != nil {
		return apperr.New(apperr.KindUsage, "%s", err.Error())
	}
	if transcripts.Unreadable > 0 {
		a.warn("some files or folders in the raw sessions folders could not be read, so a transcript there would not be found")
	}
	if !o.apply {
		return a.rerenderDry(ctx, jobs, transcripts)
	}
	return a.rerenderApply(ctx, jobs, transcripts)
}

// overCap is what push could not store for size: larger than the default cap, or named over the cap by the last push here.
func overCap(job pushJob, root *os.Root, reported map[string]bool) []string {
	var names []string
	for _, name := range job.paths {
		_, size, err := fileVersion(root, name)
		if reported[name] || (err == nil && size > migrate.DefaultMaxDocumentBytes) {
			names = append(names, name)
		}
	}
	return names
}

// find matches each over-cap document to its transcript, and counts what cannot be matched.
func find(job pushJob, transcripts *migrate.Transcripts, reported map[string]bool) (*rerendering, error) {
	g := &rerendering{job: job, summary: migrate.RerenderSummary{Name: job.collection.Name, Target: job.target, Refused: map[string]int{}}}
	root, err := os.OpenRoot(job.collection.Path)
	if err != nil {
		return nil, apperr.New(apperr.KindUsage, "collection %s: its folder cannot be opened", job.collection.Name)
	}
	defer func() { _ = root.Close() }()
	names := overCap(job, root, reported)
	if len(names) == 0 {
		return g, nil
	}
	exports := exportsOf(job, root)
	for _, name := range names {
		g.summary.OverCap++
		g.match(root, name, transcripts, exports)
	}
	return g, nil
}

func (g *rerendering) match(root *os.Root, name string, transcripts *migrate.Transcripts, exports map[string]*sessionExports) {
	data, err := root.ReadFile(filepath.FromSlash(name))
	if err != nil {
		g.summary.Unreadable++
		g.problem(name, migrate.RerenderUnreadable, "")
		return
	}
	id := migrate.SessionIDOf(data)
	matching := transcripts.Matching(id)
	switch {
	case id == "":
		g.summary.NoSessionID++
		g.problem(name, migrate.RerenderNoSessionID, "")
	case len(matching) == 0:
		g.summary.NoTranscript++
		g.problem(name, migrate.RerenderNoTranscript, "")
	case len(matching) > 1:
		g.summary.Several++
		g.problem(name, migrate.RerenderSeveral, "")
	default:
		g.check(rerenderItem{name: name, sessionID: id, transcript: matching[0]}, exports[id])
	}
}

// check sends nothing a transcript cannot fully stand in for. An over-cap export was never stored, so the server cannot refuse a
// shorter transcript for it: this is the only place that sees those messages would be lost.
func (g *rerendering) check(item rerenderItem, exports *sessionExports) {
	if exports == nil || !exports.known {
		g.summary.Uncounted++
		g.problem(item.name, migrate.RerenderUncounted, "")
		return
	}
	count, err := migrate.CountMessages(item.transcript)
	switch {
	case err != nil:
		g.summary.Unreadable++
		g.problem(item.name, migrate.RerenderUnreadable, "transcript")
	case count < exports.reach:
		g.summary.Shorter++
		g.problem(item.name, migrate.RerenderShorter, strconv.FormatInt(exports.reach-count, 10)+" messages")
	default:
		item.newest = exports.newest
		g.items = append(g.items, item)
	}
}

// rerenderDry needs no server, so its private report is not one server's. It still names what could not be matched.
func (a *app) rerenderDry(ctx context.Context, jobs []pushJob, transcripts *migrate.Transcripts) error {
	report, err := migrate.PushReportPath("dry-run")
	if err != nil {
		return err
	}
	result := &migrate.RerenderResult{DryRun: true, ReportFile: strings.TrimSuffix(report, "-report.json") + "-rerender.json"}
	var problems []migrate.RerenderProblem
	for _, job := range jobs {
		g, err := find(job, transcripts, nil)
		if err != nil {
			return err
		}
		g.summary.WouldSend = len(g.items)
		result.Collections = append(result.Collections, g.summary)
		problems = append(problems, g.problems...)
	}
	a.info("Dry run, so nothing was sent. Pass --apply to send.")
	written := writeRerenderReport(result.ReportFile, problems)
	return firstOf(a.presentRerender(ctx, result), written, rerenderOutcome(result))
}

func (a *app) rerenderApply(ctx context.Context, jobs []pushJob, transcripts *migrate.Transcripts) error {
	s, err := a.connect()
	if err != nil {
		return err
	}
	pushReport, err := migrate.PushReportPath(transport.Origin(s.base))
	if err != nil {
		return err
	}
	run, cancel := newPushRun(ctx, a, s, migratePushOptions{})
	defer cancel()
	result := &migrate.RerenderResult{ReportFile: strings.TrimSuffix(pushReport, "-report.json") + "-rerender.json"}
	var problems []migrate.RerenderProblem
	for _, job := range jobs {
		g, err := find(job, transcripts, reportedOverCap(pushReport, job))
		if err != nil {
			return err
		}
		run.rerender(g)
		result.Collections = append(result.Collections, g.summary)
		problems = append(problems, g.problems...)
	}
	written := writeRerenderReport(result.ReportFile, problems)
	return firstOf(run.failure(), a.presentRerender(ctx, result), written, rerenderOutcome(result))
}

// reportedOverCap is what the last push to this server named over the cap in this collection. No report is no names.
func reportedOverCap(path string, job pushJob) map[string]bool {
	names := map[string]bool{}
	data, err := auth.ReadPrivateFile(path)
	if err != nil {
		return names
	}
	var problems []migrate.PushProblem
	if json.Unmarshal(data, &problems) != nil {
		return names
	}
	for _, problem := range problems {
		if problem.Status == migrate.ProblemOverCap && problem.Collection == job.collection.Name && problem.Target == job.target {
			names[problem.Path] = true
		}
	}
	return names
}

func (r *pushRun) rerender(g *rerendering) {
	c := &pushCollection{job: g.job}
	for i, item := range g.items {
		if r.ctx.Err() != nil {
			for _, rest := range g.items[i:] {
				g.summary.NotReached++
				g.problem(rest.name, migrate.RerenderNotReached, "")
			}
			return
		}
		status, reason := r.rerenderOne(c, item)
		g.note(item.name, status, reason)
	}
}

func (g *rerendering) note(name, status, reason string) {
	switch status {
	case migrate.RerenderSent:
		g.summary.Sent++
	case migrate.RerenderAlready:
		g.summary.Already++
	case migrate.RerenderPartial:
		g.summary.Partial++
		g.problem(name, status, reason)
	case migrate.RerenderNotReached:
		g.summary.NotReached++
		g.problem(name, status, "")
	default:
		g.summary.Refused[reason]++
		g.problem(name, migrate.RerenderRefused, reason)
	}
}

// rerenderOne sends one transcript unless the server already holds it complete at this version or later.
func (r *pushRun) rerenderOne(c *pushCollection, item rerenderItem) (string, string) {
	info, err := os.Stat(item.transcript)
	if err != nil {
		return migrate.RerenderRefused, "transcript_unreadable"
	}
	// The transcript is what its exports came from, so it is at least as current as the newest of them. Its own file time is
	// often older, and a stored chunk carries its export's time, so that alone would be refused as stale_version.
	updated := info.ModTime().UTC().Truncate(time.Millisecond)
	if item.newest.After(updated) {
		updated = item.newest
	}
	held, answered := r.sessionHeld(c, item.sessionID)
	if !answered {
		return migrate.RerenderNotReached, ""
	}
	if held != nil && !held.Before(updated) {
		return migrate.RerenderAlready, ""
	}
	return r.sendTranscript(c, item, updated)
}

// sessionHeld is the version the server holds complete for a session, or nil.
func (r *pushRun) sessionHeld(c *pushCollection, id string) (*time.Time, bool) {
	var answer *api.GetSessionIngestStatusResponse
	format := codexFormat
	_, answered := r.askServer(c, false, func() (*http.Response, []byte, error) {
		got, err := r.session.client.GetSessionIngestStatusWithResponse(r.ctx, &api.GetSessionIngestStatusParams{Collection: &c.job.target, Format: &format, SessionId: &id})
		if err != nil {
			return nil, nil, err
		}
		answer = got
		return got.HTTPResponse, got.Body, nil
	})
	if !answered || answer == nil || answer.JSON200 == nil || answer.JSON200.State != migrate.StateComplete {
		return nil, answered
	}
	return answer.JSON200.Updated, true
}

// sendTranscript waits out a write of this session that is still running, as push does, for up to the server's hold.
func (r *pushRun) sendTranscript(c *pushCollection, item rerenderItem, updated time.Time) (string, string) {
	var waited time.Duration
	for {
		status, answered, answer := r.postTranscript(c, item, updated)
		wait := sessionInProgressWait(status, answered, answer)
		if wait == 0 || waited >= inProgressBudget {
			return judgeSession(status, answered, answer)
		}
		waited += wait
		if !r.sleep(wait) {
			r.stopForInterrupt()
			return migrate.RerenderNotReached, ""
		}
	}
}

func sessionInProgressWait(status int, answered bool, answer *api.IngestSessionResponse) time.Duration {
	if !answered || answer == nil || status != http.StatusConflict {
		return 0
	}
	refused := apperr.FromResponse(status, answer.HTTPResponse.Header, answer.Body)
	if refused.Code != "ingest_in_progress" {
		return 0
	}
	named := apperr.ParseRetryAfter(answer.HTTPResponse.Header.Get("Retry-After"), time.Now())
	return min(max(named, minServerWait), maxRetryWait)
}

func (r *pushRun) postTranscript(c *pushCollection, item rerenderItem, updated time.Time) (int, bool, *api.IngestSessionResponse) {
	var answer *api.IngestSessionResponse
	format := codexFormat
	params := &api.IngestSessionParams{Collection: &c.job.target, Format: &format, SessionId: &item.sessionID, Updated: &updated}
	status, answered := r.askServer(c, false, func() (*http.Response, []byte, error) {
		body, err := os.Open(item.transcript)
		if err != nil {
			return nil, nil, err
		}
		defer func() { _ = body.Close() }()
		got, err := r.session.client.IngestSessionWithBodyWithResponse(r.ctx, params, "application/x-ndjson", body)
		if err != nil {
			return nil, nil, err
		}
		answer = got
		return got.HTTPResponse, got.Body, nil
	})
	return status, answered, answer
}

// judgeSession reads the session route's answer. 202 is all of it, and 207 is a part, which is reported with what the server said.
func judgeSession(status int, answered bool, answer *api.IngestSessionResponse) (string, string) {
	switch {
	case !answered || answer == nil:
		return migrate.RerenderNotReached, ""
	case status == http.StatusAccepted && answer.JSON202 != nil && answer.JSON202.Complete:
		return migrate.RerenderSent, ""
	case status == http.StatusAccepted || status == http.StatusMultiStatus:
		return migrate.RerenderPartial, "partial"
	}
	refused := apperr.FromResponse(status, answer.HTTPResponse.Header, answer.Body)
	return migrate.RerenderRefused, migrate.Reason(status, refused.Code)
}

func writeRerenderReport(path string, problems []migrate.RerenderProblem) error {
	if problems == nil {
		problems = []migrate.RerenderProblem{}
	}
	data, err := json.MarshalIndent(problems, "", "  ")
	if err != nil {
		return err
	}
	return auth.WritePrivateFile(path, append(data, '\n'))
}

func (a *app) presentRerender(ctx context.Context, result *migrate.RerenderResult) error {
	raw, err := json.Marshal(result)
	if err != nil {
		return err
	}
	return a.present(ctx, output.Table, raw, map[output.Format]func(io.Writer) error{
		output.Markdown: func(w io.Writer) error { return output.MigrateRerenderMarkdown(w, result) },
		output.Table:    func(w io.Writer) error { return output.MigrateRerenderTable(w, result) },
	})
}

// rerenderOutcome fails for anything not now held, and says plainly when a transcript is missing, as that export is the only copy.
func rerenderOutcome(result *migrate.RerenderResult) error {
	missing, notHeld := 0, 0
	for i := range result.Collections {
		summary := &result.Collections[i]
		if !summary.Accounted() {
			return apperr.New(apperr.KindGeneric, "collection %s: the counts do not add up, so this summary cannot be trusted", summary.Name)
		}
		missing += summary.NoTranscript
		notHeld += summary.NotHeld()
	}
	if missing > 0 {
		return apperr.New(apperr.KindGeneric, "%d over-cap document(s) have no raw transcript here, so the export is their only copy and the server holds none of it. %s", missing, reportHint(result))
	}
	if notHeld > 0 {
		return apperr.New(apperr.KindGeneric, "%d over-cap document(s) are not held through their session. %s", notHeld, reportHint(result))
	}
	return nil
}

func reportHint(result *migrate.RerenderResult) string {
	return "The report at " + result.ReportFile + " names them"
}
