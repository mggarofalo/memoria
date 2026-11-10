package output

import (
	"bytes"
	"strings"
	"testing"

	"github.com/mggarofalo/memoria/cli/internal/api"
)

const hostileOp = "x\x1b]52;c;ZXZpbA==\x07 ![i](http://evil/x)"

func hostileOps() (*api.WorkerList, *api.QueueStatus) {
	errText := hostileOp
	collections := []string{"c\x1b[2J"}
	workers := &api.WorkerList{Mode: "remote", Workers: []api.WorkerInfo{{Name: "w\x1b[2J", State: hostileOp, Capabilities: []string{hostileOp}, Host: hostileOp, Circuit: hostileOp}}}
	queue := &api.QueueStatus{Jobs: []api.JobInfo{{Id: "j\x1b[2J", State: hostileOp, Collections: &collections, Error: &errText, CreatedAt: hostileOp}}}
	return workers, queue
}

// Worker and job fields come from the sidecar, through the API: none may drive the terminal or a markdown renderer.
func TestOpsOutputCannotCarryEscapesOrMarkup(t *testing.T) {
	workers, queue := hostileOps()
	worker := &workers.Workers[0]
	state := &api.QueueState{}
	for name, render := range map[string]func(*bytes.Buffer) error{
		"workers table":    func(w *bytes.Buffer) error { return WorkersTable(w, workers) },
		"workers markdown": func(w *bytes.Buffer) error { return WorkersMarkdown(w, workers) },
		"queue table":      func(w *bytes.Buffer) error { return QueueTable(w, queue, 0) },
		"queue markdown":   func(w *bytes.Buffer) error { return QueueMarkdown(w, queue, 0) },
		"lines": func(w *bytes.Buffer) error {
			w.WriteString(WorkerLine(worker) + JobLine("Queued", &queue.Jobs[0]) + QueueLine(state))
			return nil
		},
	} {
		var out bytes.Buffer
		if err := render(&out); err != nil {
			t.Fatal(err)
		}
		if strings.ContainsAny(out.String(), "\x1b\x07") {
			t.Errorf("%s carries a control character: %q", name, out.String())
		}
		if strings.Contains(name, "markdown") && strings.Contains(out.String(), "![i](") {
			t.Errorf("%s carries live markup: %q", name, out.String())
		}
	}
}

func TestJobsWithoutAttemptsOrCollectionsReadPlainly(t *testing.T) {
	queue := &api.QueueStatus{Jobs: []api.JobInfo{{Id: "j1", State: "queued", CreatedAt: "2026-11-01T10:00:00Z"}}}
	var out bytes.Buffer
	if err := QueueTable(&out, queue, 0); err != nil {
		t.Fatal(err)
	}
	if line := strings.Split(strings.TrimSpace(out.String()), "\n"); !strings.Contains(line[len(line)-1], "all") || !strings.Contains(line[len(line)-1], "-") {
		t.Fatalf("out = %q", out.String())
	}
}
