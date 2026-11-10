package output

import (
	"fmt"
	"io"
	"strings"

	"github.com/mggarofalo/memoria/cli/internal/api"
	"github.com/mggarofalo/memoria/cli/internal/termsafe"
)

func readinessLines(report *api.ReadinessReport, traceID string) []string {
	lines := []string{"Status: " + termsafe.Line(report.Status) + "."}
	if report.Reason != nil {
		lines = append(lines, "Reason: "+termsafe.Line(*report.Reason))
	}
	if s := report.Sidecar; s != nil {
		lines = append(lines, sidecarLines(s)...)
	} else {
		lines = append(lines, "No backend detail: that takes a key with the admin scope.")
	}
	if traceID != "" {
		lines = append(lines, "Trace: "+termsafe.Line(traceID))
	}
	return lines
}

func sidecarLines(s *api.SidecarReadiness) []string {
	lines := []string{
		fmt.Sprintf("Sidecar %s, up %ds. Index: %d documents, %d waiting to embed.", termsafe.Line(s.Version), s.UptimeSeconds, s.Index.TotalDocuments, s.Index.NeedsEmbedding),
		fmt.Sprintf("Jobs: %d pending, persistent: %s.", s.Jobs.Pending, yesNo(s.Jobs.Persistent == nil || *s.Jobs.Persistent)),
	}
	if s.Jobs.Stuck != nil {
		lines = append(lines, fmt.Sprintf("Stuck job, after %d attempts: %s", s.Jobs.Stuck.Attempts, termsafe.Line(s.Jobs.Stuck.Reason)))
	}
	return append(lines, llmLines(s.Llm)...)
}

func llmLines(l *api.SidecarLlmInfo) []string {
	if l == nil {
		return nil
	}
	lines := []string{fmt.Sprintf("Model work: %s mode, %d of %d workers up, unavailable: %s.", termsafe.Line(l.Mode), l.WorkersUp, l.WorkersTotal, noneIfEmpty(l.Unavailable))}
	if l.DocumentsSetAside != nil && *l.DocumentsSetAside > 0 {
		lines = append(lines, fmt.Sprintf("Documents set aside: %d.", *l.DocumentsSetAside))
	}
	return lines
}

func noneIfEmpty(items []string) string {
	if len(items) == 0 {
		return "none"
	}
	return termsafe.Line(strings.Join(items, ","))
}

// ReadinessText renders a readiness report as plain lines, for table and markdown alike.
func ReadinessText(w io.Writer, report *api.ReadinessReport, traceID string) error {
	var b strings.Builder
	for _, line := range readinessLines(report, traceID) {
		b.WriteString(line + "\n")
	}
	return writeText(w, &b)
}
