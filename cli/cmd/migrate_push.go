package cmd

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"sync"
	"time"

	"github.com/spf13/cobra"

	"github.com/mggarofalo/memoria/cli/internal/api"
	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/auth"
	"github.com/mggarofalo/memoria/cli/internal/migrate"
	"github.com/mggarofalo/memoria/cli/internal/output"
	"github.com/mggarofalo/memoria/cli/internal/transport"
)

const (
	// Only 8 documents over 256 KiB run at once on a server, and it answers everyone else 503.
	defaultPushWorkers = 4
	maxPushWorkers     = 16

	pushSaveEvery = 25
	pushSaveAfter = 5 * time.Second

	pauseAttempts  = 60
	holdAttempts   = 240
	resendAttempts = 8
	reportAttempts = 3

	// A write that was cut off holds its path for the server's 5-minute lease, so a later run must outlast that.
	inProgressBudget = 6 * time.Minute

	minServerWait = time.Second
	pauseDefault  = 2 * time.Second
	longestResend = 30 * time.Second
)

type migratePushOptions struct {
	qmdConfig     string
	targets       []string
	apply         bool
	workers       int
	acceptOverCap bool
	acceptSkips   bool
	ignoreState   bool
}

func newMigratePushCmd(a *app) *cobra.Command {
	var o migratePushOptions
	cmd := &cobra.Command{
		Use:   "push --qmd-config <index.yml>",
		Short: "Send a QMD setup to a Memoria server. A dry run unless you pass --apply",
		Long: `Send exactly the files 'migrate plan' counts, as they are, to the collection each one
goes to. Without --apply this is a dry run: it sends nothing and needs no key. It still reads
each selected file to judge it, as 'migrate plan' does, and keeps none of the text.

It only reads your files, and never opens QMD's index.sqlite.

A target must be empty when it is first opened, and the server refuses one that is not.
Once a collection is open the server no longer checks, so point this only at a target
meant for this migration.

An interrupted run continues where it left off when you run it again, and sending the
same document twice is safe: its path is its identity. Progress is kept in a private file
holding paths and progress, and no text. A file changed since it was sent goes again.

That file is only a note. Before skipping a document, a run asks the server for its state,
one request each, and anything the server does not confirm goes again. So a document counts
as stored only when the server says so, and "already sent" takes as long as the key's rate
limit allows.

This exits 1 if anything was not stored, and says how many, by the reason the server gave.
Only a private local report names the files, as this summary may reach shared logs. The
server's cap on a document's size refuses it with 413, and --accept-over-cap lets the exit
be 0 for those alone. --accept-skips does the same for files and folders that could not
be read. Both stay counted, and neither document is stored.

A file the server would always refuse at its path is still sent, as the server decides, and
is reported with its reason. 'migrate plan' counts those beforehand. A decomposed name, as a
Mac can hold, goes in composed form, the same every time, as the server refuses any other.

Two files whose names differ only in their Unicode form would be one document, so the first
is sent and the second is refused as second_spelling, until you rename one.

It always sends the newest copy of a file, and sends again until the server answers. A copy
older than one the server confirms it holds is not sent, as it would replace the newer one.

A document its session has since taken over is counted as migrated, not failed, once the
server confirms that state for its path. If the local copy has changed since, the summary
says those changes were not sent.

A blank file is left out and counted, as QMD does not index one.

A document whose frontmatter the server could not read is stored and searchable, but
matches no date, project or tag filter. Those are counted and named in the report.`,
		Example: `  memoria migrate push --qmd-config ~/.config/qmd/index.yml
  memoria migrate push --qmd-config index.yml --target notes=archive-notes --apply`,
		Args: noArgs,
		RunE: func(cmd *cobra.Command, _ []string) error { return a.migratePush(cmd.Context(), o) },
	}
	f := cmd.Flags()
	f.StringVar(&o.qmdConfig, "qmd-config", "", "Path to QMD's index.yml (required). There is no default")
	f.StringArrayVar(&o.targets, "target", nil, "Send a collection to another name, as local=target; repeatable. Each keeps its name otherwise")
	f.BoolVar(&o.apply, "apply", false, "Send. Without this, only report what would be sent")
	f.IntVar(&o.workers, "workers", defaultPushWorkers, "Documents sent at once")
	f.BoolVar(&o.acceptOverCap, "accept-over-cap", false, "Exit 0 even if the server refused documents as too large. They stay counted, and are not stored")
	f.BoolVar(&o.acceptSkips, "accept-skips", false, "Exit 0 even if a file or folder could not be read. They stay counted, and are not stored")
	f.BoolVar(&o.ignoreState, "ignore-state", false, "Send everything again, as for a server that lost what it was sent. This is safe")
	return cmd
}

// pushJob is one collection with what the plan selected for it. The selection is the plan's own.
type pushJob struct {
	collection migrate.Collection
	target     string
	plan       migrate.Plan
	paths      []string
	// secondSpellings would replace a file before them, so they are refused here.
	secondSpellings map[string]bool
}

func selectForPush(collections []migrate.Collection, targets map[string]string) ([]pushJob, error) {
	jobs := make([]pushJob, 0, len(collections))
	for _, collection := range collections {
		target := targetOf(collection.Name, targets)
		plan, files := migrate.Inventory(collection, target)
		if plan.Problem != "" {
			return nil, apperr.New(apperr.KindUsage, "collection %s: %s", collection.Name, plan.Problem)
		}
		paths := make([]string, 0, len(files))
		for _, file := range files {
			relative, err := filepath.Rel(collection.Path, file)
			if err != nil {
				return nil, apperr.New(apperr.KindGeneric, "collection %s: a selected file is not in its folder", collection.Name)
			}
			paths = append(paths, filepath.ToSlash(relative))
		}
		jobs = append(jobs, pushJob{collection: collection, target: target, plan: plan, paths: paths, secondSpellings: migrate.SecondSpellings(paths)})
	}
	return jobs, nil
}

func (a *app) migratePush(ctx context.Context, o migratePushOptions) error {
	if o.workers < 1 || o.workers > maxPushWorkers {
		return apperr.New(apperr.KindUsage, "--workers must be from 1 to %d", maxPushWorkers)
	}
	targets, err := parseTargets(o.targets)
	if err != nil {
		return err
	}
	collections, err := loadCollections(o.qmdConfig, targets)
	if err != nil {
		return err
	}
	if err := refuseSharedTargets(collections, targets, ""); err != nil {
		return err
	}
	jobs, err := selectForPush(collections, targets)
	if err != nil {
		return err
	}
	if !o.apply {
		return a.pushDryRun(ctx, jobs, o)
	}
	return a.pushApply(ctx, jobs, o)
}

func summaryOf(job pushJob) migrate.PushSummary {
	return migrate.PushSummary{
		Name: job.collection.Name, Target: job.target, Selected: job.plan.Files, Bytes: job.plan.Bytes,
		OverDefaultLimit: job.plan.OverLimit, FoldersSkipped: job.plan.Unreadable, Unstorable: job.plan.Unstorable, DecomposedNames: job.plan.Decomposed, Blank: job.plan.Blank,
		Refused: map[string]int{}, Frontmatter: map[string]int{}, Redactions: map[string]int{},
	}
}

func (a *app) presentPush(ctx context.Context, result *migrate.PushResult) error {
	raw, err := json.Marshal(result)
	if err != nil {
		return err
	}
	return a.present(ctx, output.Table, raw, map[output.Format]func(io.Writer) error{
		output.Markdown: func(w io.Writer) error { return output.MigratePushMarkdown(w, result) },
		output.Table:    func(w io.Writer) error { return output.MigratePushTable(w, result) },
	})
}

func (a *app) pushDryRun(ctx context.Context, jobs []pushJob, o migratePushOptions) error {
	result := &migrate.PushResult{DryRun: true}
	skipped := 0
	plans := make([]migrate.Plan, 0, len(jobs))
	for _, job := range jobs {
		summary := summaryOf(job)
		skipped += job.plan.Unreadable
		plans = append(plans, job.plan)
		a.warnIfNoFiles(job.collection.Name, job.plan.Files, job.plan.Unreadable)
		result.Collections = append(result.Collections, summary)
	}
	a.info("Dry run, so nothing was sent. Pass --apply to send.")
	return firstOf(a.presentPush(ctx, result), unstorableError(plans), skipsError(skipped, o.acceptSkips))
}

func (a *app) pushApply(ctx context.Context, jobs []pushJob, o migratePushOptions) error {
	s, err := a.connect()
	if err != nil {
		return err
	}
	origin := transport.Origin(s.base)
	reportPath, err := migrate.PushReportPath(origin)
	if err != nil {
		return err
	}
	run, cancel := newPushRun(ctx, a, s, o)
	defer cancel()
	result := &migrate.PushResult{ReportFile: reportPath}
	var problems []migrate.PushProblem
	for _, job := range jobs {
		summary, found, err := run.collection(job, origin)
		if err != nil {
			return err
		}
		result.Collections = append(result.Collections, summary)
		problems = append(problems, found...)
	}
	written := writePushReport(reportPath, problems)
	return firstOf(run.failure(), a.presentPush(ctx, result), written, pushOutcome(result, o))
}

func writePushReport(path string, problems []migrate.PushProblem) error {
	if problems == nil {
		problems = []migrate.PushProblem{}
	}
	data, err := json.MarshalIndent(problems, "", "  ")
	if err != nil {
		return err
	}
	return auth.WritePrivateFile(path, append(data, '\n'))
}

// pushOutcome is the honest exit: anything unstored fails, unless it is a kind the owner acknowledged by name.
func pushOutcome(result *migrate.PushResult, o migratePushOptions) error {
	notStored, overCap, skipped := 0, 0, 0
	for i := range result.Collections {
		summary := &result.Collections[i]
		if !summary.Accounted() {
			return apperr.New(apperr.KindGeneric, "collection %s: the counts do not add up to what was selected, so this summary cannot be trusted", summary.Name)
		}
		notStored += summary.RefusedTotal() + summary.NotReached
		overCap += summary.OverCap
		skipped += summary.Unreadable + summary.FoldersSkipped
	}
	if notStored > 0 {
		return apperr.New(apperr.KindGeneric, "%d document(s) were not stored. The report at %s names them; run this again to retry", notStored, result.ReportFile)
	}
	if overCap > 0 && !o.acceptOverCap {
		return apperr.New(apperr.KindGeneric, "%d document(s) are over the server's size limit and were not stored. The report at %s names them; --accept-over-cap acknowledges them", overCap, result.ReportFile)
	}
	return skipsError(skipped, o.acceptSkips)
}

// pushRun is one --apply. Stopping ends every worker, and a later run resumes.
type pushRun struct {
	a       *app
	ctx     context.Context
	stopAll context.CancelFunc
	session *session
	workers int
	ignore  bool

	mu         sync.Mutex
	stopped    error
	pauseUntil time.Time
}

func newPushRun(ctx context.Context, a *app, s *session, o migratePushOptions) (*pushRun, context.CancelFunc) {
	inner, cancel := context.WithCancel(ctx)
	return &pushRun{a: a, ctx: inner, stopAll: cancel, session: s, workers: o.workers, ignore: o.ignoreState}, cancel
}

func (r *pushRun) stop(err error) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.stopped == nil {
		r.stopped = err
	}
	r.stopAll()
}

// failure is why the run ended early, if it did. An interrupt that no worker noticed still counts.
func (r *pushRun) failure() error {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.stopped != nil {
		return r.stopped
	}
	if r.ctx.Err() != nil {
		return apperr.Wrap(apperr.KindGeneric, r.ctx.Err(), "stopped before the end. Progress is saved, so run this again to continue")
	}
	return nil
}

// pushCollection is the shared record of one collection's workers.
type pushCollection struct {
	job      pushJob
	root     *os.Root
	state    *migrate.PushState
	mu       sync.Mutex
	summary  migrate.PushSummary
	problems []migrate.PushProblem
	finished map[string]bool
	// held is what the server has said it holds on this run, which is all a summary may call stored.
	held    map[string]bool
	unsaved int
	saved   time.Time
}

// collection sends one collection. A run already stopped still accounts for what it did not reach.
func (r *pushRun) collection(job pushJob, origin string) (migrate.PushSummary, []migrate.PushProblem, error) {
	state, err := migrate.LoadPushState(origin, job.collection.Name, job.target, r.ignore)
	if err != nil {
		return migrate.PushSummary{}, nil, apperr.Wrap(apperr.KindGeneric, err, err.Error())
	}
	root, err := os.OpenRoot(job.collection.Path)
	if err != nil {
		return migrate.PushSummary{}, nil, apperr.New(apperr.KindUsage, "collection %s: its folder cannot be opened", job.collection.Name)
	}
	defer func() { _ = root.Close() }()
	c := &pushCollection{job: job, root: root, state: state, summary: summaryOf(job), finished: make(map[string]bool, len(job.paths)), held: map[string]bool{}, saved: time.Now()}
	for _, name := range job.plan.BlankNames {
		c.addProblem(name, migrate.ProblemBlank, "")
	}
	for _, name := range job.plan.UnreadableNames {
		c.addProblem(name, migrate.ProblemUnreadable, "")
	}
	r.a.warnIfNoFiles(job.collection.Name, job.plan.Files, job.plan.Unreadable)
	if err := state.Save(); err != nil {
		return migrate.PushSummary{}, nil, apperr.Wrap(apperr.KindGeneric, err, err.Error())
	}
	r.sendAll(c)
	if err := state.Save(); err != nil {
		r.stop(apperr.Wrap(apperr.KindGeneric, err, err.Error()))
	}
	c.finish()
	return c.summary, c.problems, nil
}

func (r *pushRun) sendAll(c *pushCollection) {
	paths := make(chan string)
	var workers sync.WaitGroup
	for range r.workers {
		workers.Add(1)
		go func() {
			defer workers.Done()
			for name := range paths {
				r.document(c, name)
			}
		}()
	}
	for _, name := range c.job.paths {
		if r.ctx.Err() != nil {
			break
		}
		paths <- name
	}
	close(paths)
	workers.Wait()
}

// finish counts what was never reached, and what the saved progress says of everything stored.
func (c *pushCollection) finish() {
	selected := make(map[string]bool, len(c.job.paths))
	for _, name := range c.job.paths {
		selected[name] = true
		if !c.finished[name] {
			c.summary.NotReached++
			c.addProblem(name, migrate.ProblemNotReached, "")
			continue
		}
		c.noteStored(name)
	}
	for name := range c.state.Files {
		if !selected[name] {
			c.summary.NoLongerSelected++
		}
	}
}

func (c *pushCollection) noteStored(name string) {
	entry, stored := c.state.Files[name]
	if !stored || !c.held[name] {
		return
	}
	if entry.Frontmatter != migrate.FrontmatterRead {
		c.summary.Frontmatter[entry.Frontmatter]++
		c.addProblem(name, migrate.ProblemFrontmatter, entry.Frontmatter)
	}
	if entry.Redaction == "disabled" {
		c.summary.StoredUnredacted++
	}
}

func (c *pushCollection) addProblem(name, status, reason string) {
	c.problems = append(c.problems, migrate.PushProblem{Collection: c.job.collection.Name, Target: c.job.target, Path: name, Status: status, Reason: reason})
}

// pushed is one document's end, one of: stored, refused, or left for a later run.
type pushed struct {
	result *api.RenderedDocumentResult
	action migrate.Action
	reason string
	// updated and size are the copy this answer is about, which may be newer than the one first looked at.
	updated time.Time
	size    int64
	// unreadable is a file gone or denied since it was selected.
	unreadable bool
	// takenOver is a refusal the server confirmed means its session now owns this path.
	takenOver bool
	// edited is a taken-over path whose local copy is not the one that was migrated.
	edited bool
}

func (r *pushRun) document(c *pushCollection, name string) {
	if r.ctx.Err() != nil {
		return
	}
	updated, size, err := fileVersion(c.root, name)
	if err != nil {
		c.record(name, func() {
			c.summary.Unreadable++
			c.addProblem(name, migrate.ProblemUnreadable, "")
		})
		return
	}
	if c.job.secondSpellings[name] {
		c.record(name, func() { c.refuse(name, migrate.ReasonSecondSpelling) })
		return
	}
	if step := r.checkSaved(c, name, updated, size); step != stepAgain {
		return
	}
	outcome, finished := r.deliver(c, name)
	if finished {
		r.settle(c, name, outcome)
	}
}

func (c *pushCollection) refuse(name, reason string) {
	c.summary.Refused[reason]++
	c.addProblem(name, migrate.ProblemRefused, reason)
}

// checkSaved asks the server about anything saved progress would skip, as that progress is only a note this machine made. A
// server wiped, or a document deleted there, is seen here. stepAgain means send it.
func (r *pushRun) checkSaved(c *pushCollection, name string, updated time.Time, size int64) pushStep {
	c.mu.Lock()
	entry, known := c.state.Files[name]
	same := c.state.IsSent(name, updated, size)
	c.mu.Unlock()
	older := known && updated.Before(entry.Updated)
	if !same && !older {
		return stepAgain
	}
	state, held, answered := r.serverStatus(c, name)
	switch {
	case !answered:
		return stepAbandoned
	case older:
		return c.settleOlder(name, state, held, entry.Updated)
	case held == nil || !held.Equal(updated):
		return stepAgain
	case state == migrate.StateComplete:
		c.record(name, func() {
			c.summary.AlreadySent++
			c.held[name] = true
		})
		return stepFinished
	case state == migrate.StateTakenOver:
		c.record(name, func() { c.summary.TakenOver++ })
		return stepFinished
	}
	return stepAgain
}

// settleOlder keeps back a copy older than one the server confirms it holds, which could replace it. With nothing confirmed, it goes.
func (c *pushCollection) settleOlder(name, state string, held *time.Time, sent time.Time) pushStep {
	if state != migrate.StateComplete || held == nil || held.Before(sent) {
		return stepAgain
	}
	c.record(name, func() { c.refuse(name, migrate.ReasonOlderThanSent) })
	return stepFinished
}

// serverStatus reads a path's ledger entry, riding out what a send would. False means the run stopped. A refusal that is not a
// stop leaves nothing confirmed, so the document goes, and the send decides.
func (r *pushRun) serverStatus(c *pushCollection, name string) (string, *time.Time, bool) {
	var answer *api.GetRenderedIngestStatusResponse
	_, answered := r.askServer(c, false, func() (*http.Response, []byte, error) {
		got, err := r.getStatus(c, name)
		if err != nil {
			return nil, nil, err
		}
		answer = got
		return got.HTTPResponse, got.Body, nil
	})
	if !answered {
		return "", nil, false
	}
	if answer == nil || answer.JSON200 == nil {
		return "", nil, true
	}
	return answer.JSON200.State, answer.JSON200.Updated, true
}

// askServer makes a read, riding out a limit, a busy server or a lost answer. False means the run stopped. Otherwise the status
// is the last answer's, which the caller judges.
func (r *pushRun) askServer(c *pushCollection, missingIsAnAnswer bool, call func() (*http.Response, []byte, error)) (int, bool) {
	tries := retries{}
	for {
		if r.waitForPause() != nil {
			return 0, false
		}
		// call's client has read the body into body and closed it, so nothing is left to close here.
		resp, body, err := call() //nolint:bodyclose // closed by the generated client that read it
		if err != nil {
			if !r.retryAfterLoss(err, tries) {
				return 0, false
			}
			continue
		}
		if resp.StatusCode < http.StatusMultipleChoices || (missingIsAnAnswer && resp.StatusCode == http.StatusNotFound) {
			return resp.StatusCode, true
		}
		again, answered := r.readRefused(c, resp, body, tries)
		if !again {
			return resp.StatusCode, answered
		}
	}
}

func (r *pushRun) readRefused(c *pushCollection, resp *http.Response, body []byte, tries retries) (again, answered bool) {
	refused := apperr.FromResponse(resp.StatusCode, resp.Header, body)
	action := migrate.Classify(resp.StatusCode, refused.Code)
	switch action {
	case migrate.ActionStop:
		r.stop(stopFor(c.job.target, refused))
		return false, false
	case migrate.ActionHold, migrate.ActionPause, migrate.ActionResend, migrate.ActionResendThenReport:
		outcome, retry := r.retryAnswer(pushed{action: action}, resp, tries)
		return retry, outcome.action != migrate.ActionStop
	}
	return false, true
}

func (r *pushRun) getStatus(c *pushCollection, name string) (*api.GetRenderedIngestStatusResponse, error) {
	sent := migrate.ServerPath(name)
	return r.session.client.GetRenderedIngestStatusWithResponse(r.ctx, &api.GetRenderedIngestStatusParams{Collection: &c.job.target, Path: &sent})
}

// fileVersion is the `updated` sent each time, as the route asks: the same value for the same file.
func fileVersion(root *os.Root, name string) (time.Time, int64, error) {
	info, err := root.Stat(filepath.FromSlash(name))
	if err != nil {
		return time.Time{}, 0, err
	}
	if !info.Mode().IsRegular() {
		return time.Time{}, 0, errors.New("not a regular file")
	}
	return info.ModTime().UTC().Truncate(time.Millisecond), info.Size(), nil
}

func (c *pushCollection) record(name string, note func()) {
	c.mu.Lock()
	defer c.mu.Unlock()
	note()
	c.finished[name] = true
}

func (r *pushRun) settle(c *pushCollection, name string, outcome pushed) {
	c.record(name, func() {
		switch {
		case outcome.unreadable:
			c.summary.Unreadable++
			c.addProblem(name, migrate.ProblemUnreadable, "")
		case outcome.takenOver:
			c.summary.TakenOver++
			c.noteEdited(name, outcome.edited)
		case outcome.action == migrate.ActionStored:
			c.stored(name, outcome.updated, outcome.size, outcome.result)
		case outcome.action == migrate.ActionOverCap:
			c.summary.OverCap++
			c.addProblem(name, migrate.ProblemOverCap, "")
		default:
			c.refuse(name, outcome.reason)
		}
	})
	if err := c.saveIfDue(); err != nil {
		r.stop(apperr.Wrap(apperr.KindGeneric, err, err.Error()))
	}
}

func (c *pushCollection) noteEdited(name string, edited bool) {
	if edited {
		c.summary.TakenOverEdited++
		c.addProblem(name, migrate.ProblemLocalEdits, "")
	}
}

func (c *pushCollection) stored(name string, updated time.Time, size int64, result *api.RenderedDocumentResult) {
	c.held[name] = true
	c.state.Files[name] = migrate.PushEntry{Updated: updated, Bytes: size, Frontmatter: result.Frontmatter, Redaction: result.Redaction}
	c.summary.Sent++
	c.unsaved++
	for rule, count := range result.Redactions {
		c.summary.Redactions[rule] += int(count)
	}
}

func (c *pushCollection) saveIfDue() error {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.unsaved < pushSaveEvery && time.Since(c.saved) < pushSaveAfter {
		return nil
	}
	c.unsaved, c.saved = 0, time.Now()
	return c.state.Save()
}

// deliver sends until the document has an end. The boolean is false when the run stopped first, and it is left for a later run.
func (r *pushRun) deliver(c *pushCollection, name string) (pushed, bool) {
	var waited time.Duration
	tries := retries{}
	for {
		if err := r.waitForPause(); err != nil {
			return pushed{}, false
		}
		// Looked at again for each send, so the last answer is always about the newest copy.
		updated, size, err := fileVersion(c.root, name)
		if err != nil {
			return pushed{unreadable: true}, true
		}
		outcome, step := r.sendOnce(c, name, updated, size, tries, &waited)
		if step != stepAgain {
			return outcome, step == stepFinished
		}
	}
}

// retries counts one document's tries by the kind of answer, so one kind cannot spend what another is owed.
type retries map[migrate.Action]int

// What one send leaves a document's delivery to do next.
type pushStep int

const (
	stepAgain pushStep = iota
	stepFinished
	stepAbandoned
)

func (r *pushRun) sendOnce(c *pushCollection, name string, updated time.Time, size int64, tries retries, waited *time.Duration) (pushed, pushStep) {
	answer, err := r.post(c, name, updated, size)
	if errors.Is(err, errFileUnreadable) {
		return pushed{unreadable: true}, stepFinished
	}
	if err != nil {
		if r.retryAfterLoss(err, tries) {
			return pushed{}, stepAgain
		}
		return pushed{}, stepAbandoned
	}
	if wait := inProgressWait(answer); wait > 0 && *waited < inProgressBudget {
		*waited += wait
		if !r.sleep(wait) {
			r.stopForInterrupt()
			return pushed{}, stepAbandoned
		}
		return pushed{}, stepAgain
	}
	outcome, again := r.judge(c, name, updated, answer, tries)
	outcome.updated, outcome.size = updated, size
	switch {
	case again:
		return outcome, stepAgain
	case outcome.action == migrate.ActionStop:
		return outcome, stepAbandoned
	}
	return outcome, stepFinished
}

func (r *pushRun) post(c *pushCollection, name string, updated time.Time, size int64) (*api.IngestRenderedResponse, error) {
	file, err := c.root.Open(filepath.FromSlash(name))
	if err != nil {
		return nil, errFileUnreadable
	}
	// The transport closes the body. Setting the length is what lets a server turn an oversized document away unread.
	sent := migrate.ServerPath(name)
	return r.session.client.IngestRenderedWithBodyWithResponse(r.ctx, &api.IngestRenderedParams{Collection: &c.job.target, Path: &sent, Updated: &updated}, "text/markdown", file,
		func(_ context.Context, req *http.Request) error {
			req.ContentLength = size
			return nil
		})
}

// retryAfterLoss handles a request with no answer, which may have landed. Sending it again is always safe.
func (r *pushRun) retryAfterLoss(err error, tries retries) bool {
	classified := apperr.FromTransport(err)
	var known *apperr.Error
	if r.ctx.Err() != nil || (errors.As(classified, &known) && known.Kind == apperr.KindInsecure) {
		r.stop(apperr.Wrap(apperr.KindGeneric, classified, "stopped before the end: "+classified.Error()+". Progress is saved, so run this again to continue"))
		return false
	}
	attempt := tries[migrate.ActionResend]
	tries[migrate.ActionResend]++
	if attempt+1 >= resendAttempts {
		r.stop(apperr.New(apperr.KindUnavailable, "the server stopped answering. Progress is saved, so run this again to continue"))
		return false
	}
	return r.sleep(resendWait(attempt))
}

func resendWait(attempt int) time.Duration {
	wait := time.Second << min(attempt, 6)
	return min(wait, longestResend)
}

func (r *pushRun) sleep(wait time.Duration) bool {
	r.a.info("The server is not ready; waiting " + wait.String() + ".")
	return r.a.sleep(r.ctx, wait) == nil
}

// inProgressWait is how long to give a write of this path that is still running, or 0 if this answer is not that.
func inProgressWait(answer *api.IngestRenderedResponse) time.Duration {
	refused := apperr.FromResponse(answer.StatusCode(), answer.HTTPResponse.Header, answer.Body)
	if migrate.Classify(answer.StatusCode(), refused.Code) != migrate.ActionWait {
		return 0
	}
	named := apperr.ParseRetryAfter(answer.HTTPResponse.Header.Get("Retry-After"), time.Now())
	return min(max(named, minServerWait), maxRetryWait)
}

// judge reads one answer. The boolean says to send it again, after any wait.
func (r *pushRun) judge(c *pushCollection, name string, updated time.Time, answer *api.IngestRenderedResponse, tries retries) (pushed, bool) {
	status := answer.StatusCode()
	refused := apperr.FromResponse(status, answer.HTTPResponse.Header, answer.Body)
	action := migrate.Classify(status, refused.Code)
	outcome := pushed{action: action, reason: migrate.Reason(status, refused.Code)}
	switch action {
	case migrate.ActionStored:
		return r.acceptAnswer(c, name, answer)
	case migrate.ActionStop:
		r.stop(stopFor(c.job.target, refused))
		return pushed{action: migrate.ActionStop}, false
	case migrate.ActionReport, migrate.ActionOverCap:
		if migrate.MayBeTakenOver(refused.Code) {
			outcome.takenOver, outcome.edited = r.isTakenOver(c, name, updated)
		}
		return outcome, false
	}
	return r.retryAnswer(outcome, answer.HTTPResponse, tries)
}

// isTakenOver asks the server what a refused path is. Anything short of a clear yes leaves it a reported problem. The second
// answer is whether this local copy differs from the version that was taken over, whose changes then went nowhere.
func (r *pushRun) isTakenOver(c *pushCollection, name string, updated time.Time) (taken, edited bool) {
	answer, err := r.getStatus(c, name)
	if err != nil || answer.JSON200 == nil || answer.JSON200.State != migrate.StateTakenOver {
		return false, false
	}
	return true, answer.JSON200.Updated != nil && !answer.JSON200.Updated.Equal(updated)
}

// acceptAnswer takes a 202 only if it is about this very document. Anything else would record it as stored.
func (r *pushRun) acceptAnswer(c *pushCollection, name string, answer *api.IngestRenderedResponse) (pushed, bool) {
	if answer.JSON202 == nil || answer.JSON202.Path != migrate.ServerPath(name) || answer.JSON202.Collection != c.job.target {
		r.stop(errBadResponse)
		return pushed{action: migrate.ActionStop}, false
	}
	return pushed{action: migrate.ActionStored, result: answer.JSON202}, false
}

func stopFor(target string, refused *apperr.Error) error {
	stopped := *refused
	stopped.Message = "collection " + target + ": " + refused.Message + ". Nothing else was sent. Progress is saved, so run this again once that is fixed"
	return &stopped
}

// retryAnswer waits as the answer's kind asks, up to that kind's limit. Past it, what may be this document's own trouble is
// reported, and a server out of room ends the run, as every document would meet the same.
func (r *pushRun) retryAnswer(outcome pushed, resp *http.Response, tries retries) (pushed, bool) {
	named := apperr.ParseRetryAfter(resp.Header.Get("Retry-After"), time.Now())
	attempt := tries[outcome.action]
	tries[outcome.action]++
	wait, limit := retryPlan(outcome.action, named, attempt)
	if outcome.action != migrate.ActionWait && attempt+1 < limit && wait <= maxRetryWait {
		if outcome.action == migrate.ActionPause || outcome.action == migrate.ActionHold {
			r.pauseFor(wait)
		} else if !r.sleep(wait) {
			r.stopForInterrupt()
			return pushed{action: migrate.ActionStop}, false
		}
		return outcome, true
	}
	// deliver has already waited out its budget for a write in progress.
	if outcome.action == migrate.ActionWait || outcome.action == migrate.ActionResendThenReport {
		outcome.action = migrate.ActionReport
		return outcome, false
	}
	r.stop(apperr.New(apperr.KindUnavailable, "the server stayed busy. Progress is saved, so run this again to continue"))
	return pushed{action: migrate.ActionStop}, false
}

func (r *pushRun) stopForInterrupt() {
	r.stop(apperr.New(apperr.KindGeneric, "stopped before the end. Progress is saved, so run this again to continue"))
}

// retryPlan is how long to wait, and how many tries to allow. A server's named time is honoured, with a floor so a zero does not spin.
func retryPlan(action migrate.Action, named time.Duration, attempt int) (time.Duration, int) {
	switch action {
	case migrate.ActionHold:
		return max(named, pauseDefault), holdAttempts
	case migrate.ActionPause:
		if named > 0 {
			return max(named, minServerWait), pauseAttempts
		}
		return pauseDefault, pauseAttempts
	case migrate.ActionResendThenReport:
		return max(named, resendWait(attempt)), reportAttempts
	}
	return max(named, resendWait(attempt)), resendAttempts
}

// pauseFor holds every worker, not just this one, as the room it ran out of is shared.
func (r *pushRun) pauseFor(wait time.Duration) {
	r.mu.Lock()
	defer r.mu.Unlock()
	until := time.Now().Add(wait)
	if until.After(r.pauseUntil) {
		r.pauseUntil = until
	}
}

func (r *pushRun) waitForPause() error {
	if err := r.ctx.Err(); err != nil {
		return err
	}
	r.mu.Lock()
	wait := time.Until(r.pauseUntil)
	r.mu.Unlock()
	if wait <= 0 {
		return nil
	}
	if !r.sleep(wait) {
		r.stopForInterrupt()
		return r.ctx.Err()
	}
	return nil
}
