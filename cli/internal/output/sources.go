package output

import (
	"fmt"
	"io"
	"sort"
	"strings"

	"github.com/mggarofalo/memoria/cli/internal/sources"
	"github.com/mggarofalo/memoria/cli/internal/termsafe"
)

func tracked(listing *sources.Listing) string {
	if listing.Tracked == nil {
		return "-"
	}
	return fmt.Sprint(*listing.Tracked)
}

func lastSync(listing *sources.Listing) string {
	if listing.LastSync == nil {
		return "never"
	}
	return optionalTime(listing.LastSync)
}

// SourcesTable renders the configured sources as a table.
func SourcesTable(w io.Writer, listings []sources.Listing) error {
	rows := make([]string, 0, len(listings))
	for i := range listings {
		l := &listings[i]
		rows = append(rows, row(l.Name, l.Collection, l.Path, tracked(l), lastSync(l)))
	}
	return table(w, "NAME\tCOLLECTION\tPATH\tTRACKED\tLAST SYNC", rows)
}

// SourcesMarkdown renders the configured sources as a bullet list.
func SourcesMarkdown(w io.Writer, listings []sources.Listing) error {
	var b strings.Builder
	for i := range listings {
		l := &listings[i]
		fmt.Fprintf(&b, "- **%s** to %s: %s, %s tracked, last sync %s\n", inlineText(l.Name), inlineText(l.Collection), codeSpan(termsafe.Line(l.Path)), tracked(l), inlineText(lastSync(l)))
	}
	return writeText(w, &b)
}

func redactionSummary(counts map[string]int) string {
	if len(counts) == 0 {
		return "none"
	}
	names := make([]string, 0, len(counts))
	for name := range counts {
		names = append(names, name)
	}
	sort.Strings(names)
	parts := make([]string, len(names))
	for i, name := range names {
		parts[i] = fmt.Sprintf("%s: %d", name, counts[name])
	}
	return strings.Join(parts, ", ")
}

func mode(report *sources.Report) string {
	if report.DryRun {
		return "dry run"
	}
	return "sync"
}

// SyncTable renders each source's outcome, and for a dry run the files it would send.
func SyncTable(w io.Writer, reports []sources.Report) error {
	rows := make([]string, 0, len(reports))
	for i := range reports {
		r := &reports[i]
		rows = append(rows, row(r.Name, mode(r), r.Collection, len(r.WouldSend), r.Sent, r.Failed, r.Remaining, r.Unchanged, r.Missing, len(r.Skipped), redactionSummary(r.Redactions)))
	}
	if err := table(w, "NAME\tMODE\tCOLLECTION\tWOULD SEND\tSENT\tFAILED\tNOT REACHED\tUNCHANGED\tMISSING LOCALLY\tSKIPPED\tREDACTED", rows); err != nil {
		return err
	}
	var b strings.Builder
	for i := range reports {
		for _, change := range reports[i].WouldSend {
			fmt.Fprintf(&b, "%s: would send %s (%s)\n", termsafe.Line(reports[i].Name), termsafe.Line(change.Path), change.Kind)
		}
	}
	return writeText(w, &b)
}

// SyncMarkdown renders each source's outcome as a bullet list.
func SyncMarkdown(w io.Writer, reports []sources.Report) error {
	var b strings.Builder
	for i := range reports {
		r := &reports[i]
		fmt.Fprintf(&b, "- **%s** (%s) to %s: %d would send, %d sent, %d failed, %d not reached, %d unchanged, %d missing locally, %d skipped; redacted: %s\n",
			inlineText(r.Name), mode(r), inlineText(r.Collection), len(r.WouldSend), r.Sent, r.Failed, r.Remaining, r.Unchanged, r.Missing, len(r.Skipped), inlineText(redactionSummary(r.Redactions)))
		for _, change := range r.WouldSend {
			fmt.Fprintf(&b, "  - would send %s (%s)\n", codeSpan(termsafe.Line(change.Path)), change.Kind)
		}
	}
	return writeText(w, &b)
}
