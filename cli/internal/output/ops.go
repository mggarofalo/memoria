package output

import (
	"fmt"
	"io"
	"strings"

	"github.com/mggarofalo/memoria/cli/internal/api"
	"github.com/mggarofalo/memoria/cli/internal/termsafe"
)

// Escape makes server text safe for one line of a format. A one-line result takes
// Plain for a table and Inline for markdown.
type Escape func(string) string

var (
	// Plain strips what a terminal would act on.
	Plain Escape = termsafe.Line
	// Inline also escapes what markdown would act on.
	Inline Escape = inlineText
)

func yesNo(b bool) string {
	if b {
		return "yes"
	}
	return "no"
}

// workerState puts a drain first, since it is the operator's own doing and what they look for.
func workerState(w *api.WorkerInfo) string {
	if w.Drained {
		return "drained (" + w.State + ")"
	}
	return w.State
}

func errorRate(rate float64) string {
	return fmt.Sprintf("%.0f%%", rate*100)
}

// WorkersTable renders the worker pool as a table.
func WorkersTable(w io.Writer, list *api.WorkerList) error {
	if len(list.Workers) == 0 {
		_, err := fmt.Fprintf(w, "No workers: the sidecar runs in %s LLM mode.\n", termsafe.Line(list.Mode))
		return err
	}
	rows := make([]string, 0, len(list.Workers))
	for i := range list.Workers {
		wk := &list.Workers[i]
		rows = append(rows, row(wk.Name, workerState(wk), wk.Circuit, wk.InFlight, wk.Calls, errorRate(wk.ErrorRate),
			strings.Join(wk.Capabilities, ","), wk.Priority, deref(wk.LastHeartbeat), wk.Host))
	}
	return table(w, "NAME\tSTATE\tCIRCUIT\tIN FLIGHT\tCALLS\tERRORS\tROLES\tPRIORITY\tLAST HEARTBEAT\tHOST", rows)
}

// WorkersMarkdown renders the worker pool as a bullet list.
func WorkersMarkdown(w io.Writer, list *api.WorkerList) error {
	var b strings.Builder
	if len(list.Workers) == 0 {
		fmt.Fprintf(&b, "No workers: the sidecar runs in %s LLM mode.\n", inlineText(list.Mode))
	}
	for i := range list.Workers {
		wk := &list.Workers[i]
		fmt.Fprintf(&b, "- %s: %s, circuit %s, %d in flight, %s errors, roles %s\n", codeSpan(termsafe.Line(wk.Name)), inlineText(workerState(wk)),
			inlineText(wk.Circuit), wk.InFlight, errorRate(wk.ErrorRate), inlineText(strings.Join(wk.Capabilities, ",")))
	}
	return writeText(w, &b)
}

// WorkerLine is the one-line summary a drain or enable reports.
func WorkerLine(wk *api.WorkerInfo, esc Escape) string {
	return fmt.Sprintf("Worker %s is %s, with %d calls in flight.", esc(wk.Name), esc(workerState(wk)), wk.InFlight)
}

func countsLine(c api.JobCounts) string {
	return fmt.Sprintf("%d queued (%d held), %d running, %d succeeded, %d failed", c.Queued, c.Held, c.Running, c.Succeeded, c.Failed)
}

// QueueLine is the one-line summary a pause or resume reports.
// It takes an Escape like the other lines, though it holds only counts.
func QueueLine(state *api.QueueState, _ Escape) string {
	verb := "running"
	if state.Paused {
		verb = "paused"
	}
	return "The queue is " + verb + ": " + countsLine(state.Counts) + "."
}

func jobCollections(job *api.JobInfo) string {
	if job.Collections == nil {
		return "all"
	}
	return strings.Join(*job.Collections, ",")
}

func attempts(job *api.JobInfo) string {
	if job.Attempts == nil {
		return "-"
	}
	return fmt.Sprint(*job.Attempts)
}

func queueSummary(status *api.QueueStatus) []string {
	return []string{
		fmt.Sprintf("Paused: %s. Persistent: %s. Documents set aside: %d.", yesNo(status.Paused), yesNo(status.Persistent), status.DeadLetters),
		"Jobs: " + countsLine(status.Counts) + ".",
		fmt.Sprintf("Model calls: interactive %d running, %d waiting; background %d running, %d waiting.",
			status.Interactive.InFlight, status.Interactive.Waiting, status.Background.InFlight, status.Background.Waiting),
	}
}

func shownJobs(status *api.QueueStatus, limit int) []api.JobInfo {
	if limit > 0 && len(status.Jobs) > limit {
		return status.Jobs[:limit]
	}
	return status.Jobs
}

// QueueTable renders the queue's state and its most recent jobs, up to limit (0 for all).
func QueueTable(w io.Writer, status *api.QueueStatus, limit int) error {
	var b strings.Builder
	for _, line := range queueSummary(status) {
		b.WriteString(line + "\n")
	}
	jobs := shownJobs(status, limit)
	if len(jobs) == 0 {
		b.WriteString("No jobs.\n")
		return writeText(w, &b)
	}
	b.WriteString("\n")
	if err := writeText(w, &b); err != nil {
		return err
	}
	rows := make([]string, 0, len(jobs))
	for i := range jobs {
		job := &jobs[i]
		rows = append(rows, row(job.Id, job.State, jobCollections(job), yesNo(job.Embed), yesNo(job.Force), attempts(job), job.CreatedAt, deref(job.Error)))
	}
	return table(w, "ID\tSTATE\tCOLLECTIONS\tEMBED\tFORCE\tATTEMPTS\tCREATED\tERROR", rows)
}

// QueueMarkdown renders the queue's state and its most recent jobs as markdown.
func QueueMarkdown(w io.Writer, status *api.QueueStatus, limit int) error {
	var b strings.Builder
	for _, line := range queueSummary(status) {
		b.WriteString(line + "\n\n")
	}
	jobs := shownJobs(status, limit)
	for i := range jobs {
		job := &jobs[i]
		fmt.Fprintf(&b, "- %s %s: %s", codeSpan(termsafe.Line(job.Id)), inlineText(job.State), inlineText(jobCollections(job)))
		if job.Error != nil {
			fmt.Fprintf(&b, " (%s)", inlineText(*job.Error))
		}
		b.WriteString("\n")
	}
	return writeText(w, &b)
}

// JobLine is the one-line summary a retry, cancel or reindex reports.
func JobLine(verb string, job *api.JobInfo, esc Escape) string {
	return fmt.Sprintf("%s job %s (%s, collections: %s).", verb, esc(job.Id), esc(job.State), esc(jobCollections(job)))
}
