package cmd

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
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
	// acceptUncounted sends exports that neither count their messages nor show it in their body. Never on by default.
	acceptUncounted bool
	// maxSessionBytes is the server's limit on one session upload. A transcript over it is not sent.
	maxSessionBytes int64
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

A transcript is a .jsonl file, or a .jsonl.zst one Codex has compressed, which is sent
decompressed. It counts only if its session_meta names that session, as the server checks. If
none does, the over-cap export is that document's only copy, and this says so and exits 1. If
more than one does, it sends neither and says so. Give Codex's archived_sessions folder too, as
it moves transcripts there.

Running it again is safe. It asks the server first, and a session it already holds complete, at
this transcript's version or later, is not sent again.

Before sending, the transcript's messages are counted as the server counts them. An export that says how far its
messages reach must be covered by that count. One that does not say must have a body that is, exactly, the first messages of
the session's threads, one thread after another, as the Codex exporters render them: its own transcript's, and those of each
subagent whose transcript names the session as its own. Each of those threads is then sent, as its own session. If neither
shows the transcripts hold everything, it is not sent, as the export is the only copy of what it would lose.
--accept-uncounted sends those anyway, as a last resort after looking, and each is named in the report and counted in the
summary.

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
	f.BoolVar(&o.acceptUncounted, "accept-uncounted", false, "Send an export that neither counts its messages nor shows them all in its body. Named in the report")
	f.Int64Var(&o.maxSessionBytes, "max-session-bytes", migrate.DefaultMaxSessionBytes, "The server's Memoria:Ingest:MaxSessionBytes. A transcript larger than this, decompressed, is not sent")
	return cmd
}

// rerenderItem is one over-cap document and what was found for it.
type rerenderItem struct {
	name, sessionID, transcript string
	// threads is what is sent for it, each under its own id: the session's own transcript, and, for a session whose export holds
	// its subagents' messages too, each subagent's.
	threads []migrate.Thread
	// newest is the latest file time among the session's exports here. Stored chunks carry their export's time as their version.
	newest time.Time
}

// sessionExports is what this collection's exports of one session say together: the newest file time, the furthest message
// those that count reach, and those that do not count.
type sessionExports struct {
	newest    time.Time
	reach     int64
	uncounted []string
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
		facts.add(name, head, updated)
	}
	return found
}

func (s *sessionExports) add(name string, head []byte, updated time.Time) {
	if updated.After(s.newest) {
		s.newest = updated
	}
	if reach, known := migrate.ReachOf(head); known {
		s.reach = max(s.reach, reach)
		return
	}
	s.uncounted = append(s.uncounted, name)
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
	job             pushJob
	acceptUncounted bool
	maxSessionBytes int64
	summary         migrate.RerenderSummary
	problems        []migrate.RerenderProblem
	items           []rerenderItem
}

func (g *rerendering) problem(name, status, reason string) {
	g.problems = append(g.problems, migrate.RerenderProblem{Collection: g.job.collection.Name, Path: name, Status: status, Reason: reason})
}

func (a *app) migrateRerender(ctx context.Context, o migrateRerenderOptions) error {
	if len(o.codexDirs) == 0 {
		return apperr.New(apperr.KindUsage, "--codex-sessions is required: it is where the raw transcripts are")
	}
	if o.maxSessionBytes <= 0 {
		return apperr.New(apperr.KindUsage, "--max-session-bytes must be a positive number of bytes")
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
	if archive := missingArchive(o.codexDirs); archive != "" {
		a.warn("Codex also keeps transcripts in " + archive + ", which was not given. Add --codex-sessions " + archive + " so a thread archived there is found")
	}
	if !o.apply {
		return a.rerenderDry(ctx, jobs, transcripts, o)
	}
	return a.rerenderApply(ctx, jobs, transcripts, o)
}

// missingArchive is Codex's archived_sessions folder beside a sessions folder given, when it is there and was not given too.
func missingArchive(dirs []string) string {
	given := map[string]bool{}
	for _, dir := range dirs {
		given[strings.ToLower(absolute(dir))] = true
	}
	for _, dir := range dirs {
		clean := absolute(dir)
		if !strings.EqualFold(filepath.Base(clean), "sessions") {
			continue
		}
		archive := filepath.Join(filepath.Dir(clean), "archived_sessions")
		if info, err := os.Stat(archive); err == nil && info.IsDir() && !given[strings.ToLower(archive)] {
			return archive
		}
	}
	return ""
}

func absolute(dir string) string {
	if full, err := filepath.Abs(dir); err == nil {
		return full
	}
	return filepath.Clean(dir)
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
func find(job pushJob, transcripts *migrate.Transcripts, reported map[string]bool, o migrateRerenderOptions) (*rerendering, error) {
	g := &rerendering{job: job, acceptUncounted: o.acceptUncounted, maxSessionBytes: o.maxSessionBytes, summary: migrate.RerenderSummary{Name: job.collection.Name, Target: job.target, Refused: map[string]int{}}}
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
		g.check(root, rerenderItem{name: name, sessionID: id, transcript: matching[0]}, exports[id], transcripts)
	}
}

// check sends nothing a transcript cannot fully stand in for. An over-cap export was never stored, so the server cannot refuse a
// shorter transcript for it: this is the only place that sees those messages would be lost.
func (g *rerendering) check(root *os.Root, item rerenderItem, exports *sessionExports, transcripts *migrate.Transcripts) {
	// Sized first, so a transcript too large to send is not read whole only to be turned away.
	if !g.fitsUpload(item, []migrate.Thread{{ID: item.sessionID, File: item.transcript}}) {
		return
	}
	messages, err := migrate.TranscriptMessages(item.transcript)
	count := int64(len(messages))
	switch {
	case err != nil:
		g.summary.Unreadable++
		g.problem(item.name, migrate.RerenderUnreadable, "transcript")
	case count < exports.reach:
		g.summary.Shorter++
		g.problem(item.name, migrate.RerenderShorter, strconv.FormatInt(exports.reach-count, 10)+" messages")
	default:
		g.checkUncounted(root, item, exports, transcripts)
	}
}

// checkUncounted reads the body of each export that does not count its messages against every thread of the session, as the hook
// exporter wrote them all into one export. When they cover it, every thread is sent, so each message in the export is in one sent.
func (g *rerendering) checkUncounted(root *os.Root, item rerenderItem, exports *sessionExports, transcripts *migrate.Transcripts) {
	item.threads = []migrate.Thread{{ID: item.sessionID, File: item.transcript}}
	if len(exports.uncounted) > 0 {
		family, err := transcripts.Family(item.sessionID, item.transcript)
		if err != nil {
			family, err = ownCovers(root, item, exports.uncounted, err)
		}
		if err != nil {
			g.familyUnread(item.name, err)
			return
		}
		covered := uncountedCovered(root, exports.uncounted, family)
		if !covered && !g.acceptUncounted {
			g.summary.Uncounted++
			g.problem(item.name, migrate.RerenderUncounted, "")
			return
		}
		item.threads = withoutMessages(family, item.transcript)
		if !g.fitsUpload(item, othersOf(item.threads, item.transcript)) {
			return
		}
		if !covered {
			g.summary.AcceptedUncounted++
			g.problem(item.name, migrate.RerenderAcceptedUncounted, "")
		}
	}
	item.newest = exports.newest
	g.summary.Threads += len(item.threads)
	g.items = append(g.items, item)
}

// fitsUpload says whether every thread of a document is within the server's limit on one session upload, counted as it would be
// sent, decompressed. One over it is not sent, and nor is the rest of the document: the server would refuse it whole, so its
// export would still be the only copy of its messages. Sending it anyway would only be cut off.
func (g *rerendering) fitsUpload(item rerenderItem, threads []migrate.Thread) bool {
	for _, thread := range threads {
		size, exact, err := migrate.TranscriptSizeUpTo(thread.File, g.maxSessionBytes)
		if err != nil {
			g.summary.Unreadable++
			g.threadProblem(item.name, migrate.RerenderUnreadable, "transcript", otherThread(item, thread.ID))
			return false
		}
		if size > g.maxSessionBytes {
			g.summary.TooLarge++
			g.threadProblem(item.name, migrate.RerenderTooLarge, tooLargeReason(size, exact, g.maxSessionBytes), otherThread(item, thread.ID))
			return false
		}
	}
	return true
}

// tooLargeReason says how large, for the report. A compressed one is decompressed only as far as one byte past the limit.
func tooLargeReason(size int64, exact bool, limit int64) string {
	if exact {
		return fmt.Sprintf("%d bytes, over the %d byte limit", size, limit)
	}
	return fmt.Sprintf("more than %d bytes decompressed, over the limit", limit)
}

// othersOf is a document's threads other than the session's own.
func othersOf(threads []migrate.Thread, own string) []migrate.Thread {
	var others []migrate.Thread
	for _, thread := range threads {
		if thread.File != own {
			others = append(others, thread)
		}
	}
	return others
}

// otherThread is a thread's id for the report, or "" for the session's own.
func otherThread(item rerenderItem, id string) string {
	if id == item.sessionID {
		return ""
	}
	return id
}

func (g *rerendering) familyUnread(name string, err error) {
	if errors.Is(err, migrate.ErrSeveralThreads) {
		g.summary.Several++
		g.problem(name, migrate.RerenderSeveral, "thread")
		return
	}
	g.summary.Unreadable++
	g.problem(name, migrate.RerenderUnreadable, "thread")
}

// ownCovers is the session's own transcript alone, when a sibling thread cannot be read but the exports hold nothing past it:
// no other thread is then needed to hold them. Otherwise it is the error that stopped the family.
func ownCovers(root *os.Root, item rerenderItem, uncounted []string, failed error) ([]migrate.Thread, error) {
	messages, err := migrate.TranscriptMessages(item.transcript)
	own := []migrate.Thread{{ID: item.sessionID, File: item.transcript, Messages: messages}}
	if err != nil || !uncountedCovered(root, uncounted, own) {
		return nil, failed
	}
	return own, nil
}

// withoutMessages keeps what sending needs, so a session's messages are not held while it is sent. A subagent thread with no
// messages adds nothing to the export and the server would only answer it as partial, so it is left out. The session's own thread
// always goes, as it is the session the export names.
func withoutMessages(threads []migrate.Thread, own string) []migrate.Thread {
	var kept []migrate.Thread
	for _, thread := range threads {
		if thread.File == own || len(thread.Messages) > 0 {
			kept = append(kept, migrate.Thread{ID: thread.ID, File: thread.File})
		}
	}
	return kept
}

// uncountedCovered is whether every export of the session that does not count its messages has a body that is its threads'.
func uncountedCovered(root *os.Root, names []string, family []migrate.Thread) bool {
	threads := make([][]migrate.Message, len(family))
	for i := range family {
		threads[i] = family[i].Messages
	}
	for _, name := range names {
		data, err := root.ReadFile(filepath.FromSlash(name))
		if err != nil || !migrate.BodyCovered(data, threads...) {
			return false
		}
	}
	return true
}

// rerenderDry needs no server, so its private report is not one server's. It still names what could not be matched.
func (a *app) rerenderDry(ctx context.Context, jobs []pushJob, transcripts *migrate.Transcripts, o migrateRerenderOptions) error {
	report, err := migrate.PushReportPath("dry-run")
	if err != nil {
		return err
	}
	result := &migrate.RerenderResult{DryRun: true, ReportFile: strings.TrimSuffix(report, "-report.json") + "-rerender.json"}
	var problems []migrate.RerenderProblem
	for _, job := range jobs {
		g, err := find(job, transcripts, nil, o)
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

func (a *app) rerenderApply(ctx context.Context, jobs []pushJob, transcripts *migrate.Transcripts, o migrateRerenderOptions) error {
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
		g, err := find(job, transcripts, reportedOverCap(pushReport, job), o)
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
		outcome := r.rerenderOne(c, item)
		g.note(item, outcome)
	}
}

// threadOutcome is how sending a document's threads ended: the first that did not go, or how they all went.
type threadOutcome struct {
	status, reason, thread string
}

func (g *rerendering) note(item rerenderItem, outcome threadOutcome) {
	thread := outcome.thread
	if thread == item.sessionID {
		thread = ""
	}
	switch outcome.status {
	case migrate.RerenderSent:
		g.summary.Sent++
	case migrate.RerenderAlready:
		g.summary.Already++
	case migrate.RerenderPartial:
		g.summary.Partial++
		g.threadProblem(item.name, outcome.status, outcome.reason, thread)
	case migrate.RerenderNotReached:
		g.summary.NotReached++
		g.threadProblem(item.name, outcome.status, "", thread)
	default:
		g.summary.Refused[outcome.reason]++
		g.threadProblem(item.name, migrate.RerenderRefused, outcome.reason, thread)
	}
}

func (g *rerendering) threadProblem(name, status, reason, thread string) {
	g.problems = append(g.problems, migrate.RerenderProblem{Collection: g.job.collection.Name, Path: name, Status: status, Reason: reason, Thread: thread})
}

// rerenderOne sends each of a document's threads. It is held only when all of them are, so the first that is not is what it
// reports. The rest are still sent, as each is a session of its own, except when the server cannot be reached.
func (r *pushRun) rerenderOne(c *pushCollection, item rerenderItem) threadOutcome {
	var failed *threadOutcome
	sent := false
	for _, thread := range item.threads {
		if r.ctx.Err() != nil {
			return threadOutcome{status: migrate.RerenderNotReached, thread: thread.ID}
		}
		status, reason := r.rerenderThread(c, item, thread)
		switch {
		case status == migrate.RerenderNotReached:
			return threadOutcome{status: status, thread: thread.ID}
		case status == migrate.RerenderSent:
			sent = true
		case status != migrate.RerenderAlready && failed == nil:
			failed = &threadOutcome{status: status, reason: reason, thread: thread.ID}
		}
	}
	switch {
	case failed != nil:
		return *failed
	case sent:
		return threadOutcome{status: migrate.RerenderSent}
	}
	return threadOutcome{status: migrate.RerenderAlready}
}

// rerenderThread sends one transcript unless the server already holds it complete at this version or later.
func (r *pushRun) rerenderThread(c *pushCollection, item rerenderItem, thread migrate.Thread) (string, string) {
	info, err := os.Stat(thread.File)
	if err != nil {
		return migrate.RerenderRefused, "transcript_unreadable"
	}
	// The transcript is what its exports came from, so it is at least as current as the newest of them. Its own file time is
	// often older, and a stored chunk carries its export's time, so that alone would be refused as stale_version.
	updated := info.ModTime().UTC().Truncate(time.Millisecond)
	if item.newest.After(updated) {
		updated = item.newest
	}
	held, answered := r.sessionHeld(c, thread.ID)
	if !answered {
		return migrate.RerenderNotReached, ""
	}
	if held != nil && !held.Before(updated) {
		return migrate.RerenderAlready, ""
	}
	return r.sendTranscript(c, thread, updated)
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
func (r *pushRun) sendTranscript(c *pushCollection, thread migrate.Thread, updated time.Time) (string, string) {
	var waited time.Duration
	for {
		status, answered, answer := r.postTranscript(c, thread, updated)
		wait := sessionInProgressWait(status, answered, answer)
		if wait == 0 || waited >= inProgressBudget {
			return judgeSession(status, answered, answer)
		}
		waited += wait
		if !r.sleep(wait, waitInProgress) {
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

func (r *pushRun) postTranscript(c *pushCollection, thread migrate.Thread, updated time.Time) (int, bool, *api.IngestSessionResponse) {
	var answer *api.IngestSessionResponse
	format := codexFormat
	params := &api.IngestSessionParams{Collection: &c.job.target, Format: &format, SessionId: &thread.ID, Updated: &updated}
	status, answered := r.askServer(c, false, func() (*http.Response, []byte, error) {
		// A compressed transcript goes decompressed, as it is read, since the server reads JSON lines.
		body, err := migrate.OpenTranscript(thread.File)
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
