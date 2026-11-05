package output

import (
	"fmt"
	"io"
	"sort"
	"strings"

	"github.com/mggarofalo/memoria/cli/internal/migrate"
)

// planProblem is what a row shows when a collection could not be counted.
func planProblem(plan *migrate.Plan) string {
	if plan.Problem != "" {
		return plan.Problem
	}
	return "-"
}

// MigratePlanTable renders the plan, with the totals and the raw sessions under it.
func MigratePlanTable(w io.Writer, plans []migrate.Plan, raw *migrate.RawSessions) error {
	rows := make([]string, 0, len(plans))
	files, total := 0, int64(0)
	for i := range plans {
		p := &plans[i]
		files += p.Files
		total += p.Bytes
		rows = append(rows, row(p.Name, p.Target, p.Files, p.Bytes, p.Largest, p.OverLimit, p.Unreadable, planProblem(p), p.Path))
	}
	if err := table(w, "COLLECTION\tTARGET\tFILES\tBYTES\tLARGEST\tOVER 2 MIB\tUNREADABLE\tPROBLEM\tPATH", rows); err != nil {
		return err
	}
	var b strings.Builder
	fmt.Fprintf(&b, "total: %d files, %d bytes\n", files, total)
	if raw != nil {
		b.WriteString(rawLine(raw) + "\n")
	}
	return writeText(w, &b)
}

func rawLine(raw *migrate.RawSessions) string {
	if raw.Problem != "" {
		return "raw Codex sessions: " + row(raw.Problem)
	}
	return row(fmt.Sprintf("raw Codex sessions: %d files, %d bytes, %d unreadable, could go to %s", raw.Files, raw.Bytes, raw.Unreadable, raw.Target))
}

// MigratePlanMarkdown renders the plan as a bullet list.
func MigratePlanMarkdown(w io.Writer, plans []migrate.Plan, raw *migrate.RawSessions) error {
	var b strings.Builder
	for i := range plans {
		p := &plans[i]
		fmt.Fprintf(&b, "- **%s** to %s: %d files, %d bytes, largest %d, %d over 2 MiB, %d unreadable; %s\n", inlineText(p.Name), inlineText(p.Target), p.Files, p.Bytes, p.Largest, p.OverLimit, p.Unreadable, inlineText(planProblem(p)))
	}
	if raw != nil {
		b.WriteString("- " + inlineText(rawLine(raw)) + "\n")
	}
	return writeText(w, &b)
}

// gitleaksCaveat keeps a count of 0 from reading as proof.
const gitleaksCaveat = "Gitleaks is best effort: it can miss a token at a chunk boundary, so 0 findings is not proof that nothing is left."

func anyGitleaks(reports []migrate.Report) bool {
	for i := range reports {
		if reports[i].GitleaksRan {
			return true
		}
	}
	return false
}

func gitleaksSummary(report *migrate.Report) string {
	if !report.GitleaksRan {
		return "NOT RUN"
	}
	count := 0
	for _, findings := range report.GitleaksResults {
		count += findings
	}
	return fmt.Sprint(count)
}

func ruleNames(counts map[string]int) []string {
	names := make([]string, 0, len(counts))
	for name := range counts {
		names = append(names, name)
	}
	sort.Strings(names)
	return names
}

// MigrateReportTable renders the redaction check, with each rule's counts under it.
func MigrateReportTable(w io.Writer, reports []migrate.Report) error {
	rows := make([]string, 0, len(reports))
	for i := range reports {
		r := &reports[i]
		rows = append(rows, row(r.Name, r.Files, r.Changed, r.Refused, r.Skipped, r.Unscanned, gitleaksSummary(r)))
	}
	if err := table(w, "COLLECTION\tFILES\tCHANGED\tREFUSED\tSKIPPED\tUNSCANNED\tGITLEAKS FINDINGS", rows); err != nil {
		return err
	}
	var b strings.Builder
	for i := range reports {
		r := &reports[i]
		for _, rule := range ruleNames(r.Replacements) {
			b.WriteString(row(fmt.Sprintf("%s: %s: %d replacements in %d files", r.Name, rule, r.Replacements[rule], r.FilesByRule[rule])) + "\n")
		}
		for _, rule := range ruleNames(r.GitleaksResults) {
			b.WriteString(row(fmt.Sprintf("%s: gitleaks %s: %d left after redaction", r.Name, rule, r.GitleaksResults[rule])) + "\n")
		}
	}
	if anyGitleaks(reports) {
		b.WriteString(gitleaksCaveat + "\n")
	}
	return writeText(w, &b)
}

// MigrateReportMarkdown renders the redaction check as a bullet list.
func MigrateReportMarkdown(w io.Writer, reports []migrate.Report) error {
	var b strings.Builder
	for i := range reports {
		r := &reports[i]
		fmt.Fprintf(&b, "- **%s**: %d files, %d changed, %d refused, %d skipped, %d not scanned by gitleaks, gitleaks findings: %s\n", inlineText(r.Name), r.Files, r.Changed, r.Refused, r.Skipped, r.Unscanned, gitleaksSummary(r))
		for _, rule := range ruleNames(r.Replacements) {
			fmt.Fprintf(&b, "  - %s: %d replacements in %d files\n", inlineText(rule), r.Replacements[rule], r.FilesByRule[rule])
		}
		for _, rule := range ruleNames(r.GitleaksResults) {
			fmt.Fprintf(&b, "  - gitleaks %s: %d left after redaction\n", inlineText(rule), r.GitleaksResults[rule])
		}
	}
	if anyGitleaks(reports) {
		b.WriteString("\n" + gitleaksCaveat + "\n")
	}
	return writeText(w, &b)
}
