package output

import (
	"fmt"
	"io"
	"strings"

	"github.com/mggarofalo/memoria/cli/internal/api"
)

func readinessLines(report *api.ReadinessReport, traceID string, esc Escape) []string {
	lines := []string{"Status: " + esc(report.Status) + "."}
	if report.Reason != nil {
		lines = append(lines, "Reason: "+esc(*report.Reason))
	}
	if s := report.Sidecar; s != nil {
		lines = append(lines, sidecarLines(s, esc)...)
	} else {
		lines = append(lines, "No backend detail: that takes a key with the admin scope.")
	}
	if traceID != "" {
		lines = append(lines, "Trace: "+esc(traceID))
	}
	return lines
}

func sidecarLines(s *api.SidecarReadiness, esc Escape) []string {
	lines := []string{
		fmt.Sprintf("Sidecar %s, up %ds. Index: %d documents, %d waiting to embed.", esc(s.Version), s.UptimeSeconds, s.Index.TotalDocuments, s.Index.NeedsEmbedding),
		fmt.Sprintf("Jobs: %d pending, persistent: %s.", s.Jobs.Pending, yesNo(s.Jobs.Persistent == nil || *s.Jobs.Persistent)),
	}
	if s.Jobs.Stuck != nil {
		lines = append(lines, fmt.Sprintf("Stuck job, after %d attempts: %s", s.Jobs.Stuck.Attempts, esc(s.Jobs.Stuck.Reason)))
	}
	return append(lines, llmLines(s.Llm, esc)...)
}

func llmLines(l *api.SidecarLlmInfo, esc Escape) []string {
	if l == nil {
		return nil
	}
	lines := []string{fmt.Sprintf("Model work: %s mode, %d of %d workers up, unavailable: %s.", esc(l.Mode), l.WorkersUp, l.WorkersTotal, noneIfEmpty(l.Unavailable, esc))}
	if l.DocumentsSetAside != nil && *l.DocumentsSetAside > 0 {
		lines = append(lines, fmt.Sprintf("Documents set aside: %d.", *l.DocumentsSetAside))
	}
	return lines
}

func noneIfEmpty(items []string, esc Escape) string {
	if len(items) == 0 {
		return "none"
	}
	return esc(strings.Join(items, ","))
}

// ReadinessText renders a readiness report as lines, with esc Plain for a table and Inline for markdown.
func ReadinessText(w io.Writer, report *api.ReadinessReport, traceID string, esc Escape) error {
	var b strings.Builder
	for _, line := range readinessLines(report, traceID, esc) {
		b.WriteString(line + "\n")
	}
	return writeText(w, &b)
}
