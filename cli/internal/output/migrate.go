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
	for i := range plans {
		for _, line := range unstorableLines(plans[i].Name, plans[i].Unstorable, plans[i].Decomposed, plans[i].Blank) {
			b.WriteString(row(line) + "\n")
		}
	}
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
		for _, line := range unstorableLines(p.Name, p.Unstorable, p.Decomposed, p.Blank) {
			b.WriteString("  - " + inlineText(line) + "\n")
		}
	}
	if raw != nil {
		b.WriteString("- " + inlineText(rawLine(raw)) + "\n")
	}
	return writeText(w, &b)
}

// gitleaksCaveat keeps a count of 0 from reading as proof.
const gitleaksCaveat = "Gitleaks is best effort: it can miss a token at a chunk boundary, so 0 findings is not proof that nothing is left."

// wrappedCaveat says what redaction gives up to keep the text after a secret, and what it still removes more of than the secret.
const wrappedCaveat = "Redaction keeps what follows a secret. So a token wrapped across lines, whose last piece reads as an ordinary word or number, can keep that piece, up to about 12 characters, with the rest removed. A Cookie header's whole line goes, as any cookie in it may be live."

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
		if r.CredentialNames > 0 {
			b.WriteString(row(fmt.Sprintf("%s: %d file name(s) look like a credential, which a server refuses", r.Name, r.CredentialNames)) + "\n")
		}
	}
	if anyGitleaks(reports) {
		b.WriteString(gitleaksCaveat + "\n")
	}
	b.WriteString(wrappedCaveat + "\n")
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
		if r.CredentialNames > 0 {
			fmt.Fprintf(&b, "  - %d file name(s) look like a credential, which a server refuses\n", r.CredentialNames)
		}
	}
	if anyGitleaks(reports) {
		b.WriteString("\n" + gitleaksCaveat + "\n")
	}
	b.WriteString("\n" + wrappedCaveat + "\n")
	return writeText(w, &b)
}

// unstorableLines says what a plan knows will be refused, by reason, and names no file.
func unstorableLines(name string, unstorable map[string]int, decomposed, blank int) []string {
	var lines []string
	for _, reason := range ruleNames(unstorable) {
		lines = append(lines, fmt.Sprintf("%s: cannot be stored at its path, %s: %d", name, reason, unstorable[reason]))
	}
	if blank > 0 {
		lines = append(lines, fmt.Sprintf("%s: %d blank file(s) left out, as QMD does not index them", name, blank))
	}
	if decomposed > 0 {
		lines = append(lines, fmt.Sprintf("%s: %d decomposed name(s) go in composed form, so they are stored under that spelling", name, decomposed))
	}
	return lines
}

const pushFrontmatterNote = "Documents with a frontmatter issue are stored and searchable, but match no date, project or tag filter."

func pushVerdict(result *migrate.PushResult) string {
	if result.DryRun {
		return "Dry run: nothing was sent. Pass --apply to send."
	}
	unstored, selected, edited, taken := 0, 0, 0, 0
	for i := range result.Collections {
		unstored += result.Collections[i].NotStored() + result.Collections[i].OverCap + result.Collections[i].FoldersSkipped
		selected += result.Collections[i].Selected
		edited += result.Collections[i].TakenOverEdited
		taken += result.Collections[i].TakenOver
	}
	switch {
	case unstored > 0:
		return fmt.Sprintf("NOT everything was stored: %d not stored or not read. The local report names them, and these counts do not.", unstored)
	case selected == 0:
		return "Nothing was selected, so nothing was sent."
	case taken > 0:
		return takenOverVerdict(selected-taken, taken, edited)
	}
	return "Every selected document is stored. The server confirmed each one."
}

// takenOverVerdict keeps taken-over documents out of what is called stored: the server records the takeover, and a part of
// that session can since have been deleted.
func takenOverVerdict(stored, taken, edited int) string {
	verdict := fmt.Sprintf("%d stored, which the server confirmed. %d taken over by their session: the server records that, but not that their text is still held.", stored, taken)
	if edited > 0 {
		verdict += fmt.Sprintf(" %d of those have local changes that were not sent.", edited)
	}
	return verdict
}

func sum(counts map[string]int) int {
	total := 0
	for _, count := range counts {
		total += count
	}
	return total
}

func pushDetails(summary *migrate.PushSummary) []string {
	var lines []string
	lines = append(lines, unstorableLines(summary.Name, summary.Unstorable, summary.DecomposedNames, summary.Blank)...)
	for _, reason := range ruleNames(summary.Refused) {
		lines = append(lines, fmt.Sprintf("%s: refused, %s: %d", summary.Name, reason, summary.Refused[reason]))
	}
	for _, reason := range ruleNames(summary.Frontmatter) {
		lines = append(lines, fmt.Sprintf("%s: frontmatter %s: %d", summary.Name, reason, summary.Frontmatter[reason]))
	}
	for _, rule := range ruleNames(summary.Redactions) {
		lines = append(lines, fmt.Sprintf("%s: %s: %d replacements this run", summary.Name, rule, summary.Redactions[rule]))
	}
	if summary.StoredUnredacted > 0 {
		lines = append(lines, fmt.Sprintf("%s: %d stored unredacted, as the server's setting for this collection says", summary.Name, summary.StoredUnredacted))
	}
	if summary.TakenOverEdited > 0 {
		lines = append(lines, fmt.Sprintf("%s: %d taken over by their session, with local changes that were not sent", summary.Name, summary.TakenOverEdited))
	}
	if summary.NoLongerSelected > 0 {
		lines = append(lines, fmt.Sprintf("%s: %d sent earlier and no longer selected, and left on the server", summary.Name, summary.NoLongerSelected))
	}
	return lines
}

func pushNotes(result *migrate.PushResult) []string {
	notes := []string{}
	for i := range result.Collections {
		if sum(result.Collections[i].Frontmatter) > 0 {
			notes = append(notes, pushFrontmatterNote)
			break
		}
	}
	notes = append(notes, pushVerdict(result))
	if result.ReportFile != "" {
		notes = append(notes, "Local report: "+result.ReportFile)
	}
	return notes
}

// MigratePushTable renders a push. It names no file: only the local report does.
func MigratePushTable(w io.Writer, result *migrate.PushResult) error {
	rows := make([]string, 0, len(result.Collections))
	var b strings.Builder
	for i := range result.Collections {
		s := &result.Collections[i]
		if result.DryRun {
			rows = append(rows, row(s.Name, s.Target, s.Selected, s.Bytes, s.OverDefaultLimit, sum(s.Unstorable), s.FoldersSkipped))
			for _, line := range unstorableLines(s.Name, s.Unstorable, s.DecomposedNames, s.Blank) {
				b.WriteString(row(line) + "\n")
			}
			continue
		}
		rows = append(rows, row(s.Name, s.Target, s.Selected, s.Sent, s.AlreadySent, s.TakenOver, s.RefusedTotal(), s.OverCap, s.Unreadable, s.FoldersSkipped, s.NotReached, sum(s.Frontmatter)))
		for _, line := range pushDetails(s) {
			b.WriteString(row(line) + "\n")
		}
	}
	header := "COLLECTION\tTARGET\tSELECTED\tSENT\tALREADY SENT\tTAKEN OVER\tREFUSED\tOVER CAP\tUNREADABLE\tSKIPPED BEFORE SENDING\tNOT REACHED\tFRONTMATTER ISSUES"
	if result.DryRun {
		header = "COLLECTION\tTARGET\tWOULD SEND\tBYTES\tOVER 2 MIB\tCANNOT BE STORED\tFOLDERS SKIPPED"
	}
	if err := table(w, header, rows); err != nil {
		return err
	}
	for _, note := range pushNotes(result) {
		b.WriteString(row(note) + "\n")
	}
	return writeText(w, &b)
}

// MigratePushMarkdown renders a push as a bullet list, naming no file.
func MigratePushMarkdown(w io.Writer, result *migrate.PushResult) error {
	var b strings.Builder
	for i := range result.Collections {
		s := &result.Collections[i]
		fmt.Fprintf(&b, "- **%s** to %s: %d selected, %d sent, %d already sent, %d taken over, %d refused, %d over cap, %d unreadable, %d folders skipped, %d not reached, %d frontmatter issues\n",
			inlineText(s.Name), inlineText(s.Target), s.Selected, s.Sent, s.AlreadySent, s.TakenOver, s.RefusedTotal(), s.OverCap, s.Unreadable, s.FoldersSkipped, s.NotReached, sum(s.Frontmatter))
		for _, line := range pushDetails(s) {
			b.WriteString("  - " + inlineText(line) + "\n")
		}
	}
	for _, note := range pushNotes(result) {
		b.WriteString("\n" + inlineText(note) + "\n")
	}
	return writeText(w, &b)
}

// verifyLimits is said on every run, so a clean result does not read as more than it is.
var verifyLimits = []string{
	"This shows each path's state and version, and that the sampled documents match apart from redaction.",
	"It does not show every document's text is right: only the sample was fetched. A file gone from the server's disk behind its ledger shows only if sampled, and taken-over documents are counted on their state alone.",
	"Overlap is a number to read, not a pass or fail. Local QMD ranks differently and may use another embedder, so 100% is not expected.",
}

func overlapText(o migrate.Overlap, mode string) string {
	if o.Queries == 0 {
		return "not run, as no --queries were given"
	}
	counted := o.Queries - o.NoLocalResults - o.Failed
	if counted == 0 {
		return fmt.Sprintf("no query could be compared, of %d (%d with no local results, %d failed)", o.Queries, o.NoLocalResults, o.Failed)
	}
	return fmt.Sprintf("mean %.0f%%, lowest %.0f%%, over %d of %d queries, local %s (%d with no local results, %d failed)", o.Mean*100, o.Lowest*100, counted, o.Queries, mode, o.NoLocalResults, o.Failed)
}

func verifyNotes(result *migrate.VerifyResult) []string {
	problems := 0
	for i := range result.Collections {
		problems += result.Collections[i].Problems()
	}
	verdict := "Nothing here needs a look, within the limits below."
	if problems > 0 {
		verdict = fmt.Sprintf("%d thing(s) need a look. The local report names them, and these counts do not.", problems)
	}
	seed := fmt.Sprintf("Sample seed: %d. Pass it as --seed to fetch the same sample again.", result.Seed)
	return append([]string{verdict}, append(verifyLimits, seed, "Local report: "+result.ReportFile)...)
}

// MigrateVerifyTable renders a check. It names no file or query: only the local report does.
func MigrateVerifyTable(w io.Writer, result *migrate.VerifyResult) error {
	rows := make([]string, 0, len(result.Collections))
	var b strings.Builder
	for i := range result.Collections {
		s := &result.Collections[i]
		rows = append(rows, row(s.Name, s.Target, s.LocalFiles, s.ServerDocuments, s.Confirmed, s.TakenOver, s.VersionDiffers, s.NotHeld, s.SecondSpellings, s.LocalUnreadable+s.FoldersSkipped, s.NotChecked))
		if s.CountDiffers {
			b.WriteString(row(fmt.Sprintf("%s: the counts differ: %d local, %d on the server", s.Name, s.LocalFiles, s.ServerDocuments)) + "\n")
		}
		b.WriteString(row(s.Name+": sample: "+sampleText(s)) + "\n")
		b.WriteString(row(s.Name+": overlap: "+overlapText(s.Overlap, result.LocalMode)) + "\n")
	}
	header := "COLLECTION\tTARGET\tLOCAL FILES\tSERVER DOCUMENTS\tCONFIRMED\tTAKEN OVER\tVERSION DIFFERS\tNOT HELD\tSECOND SPELLING\tNOT READ HERE\tNOT CHECKED"
	if err := table(w, header, rows); err != nil {
		return err
	}
	for _, note := range verifyNotes(result) {
		b.WriteString(row(note) + "\n")
	}
	return writeText(w, &b)
}

func sampleText(s *migrate.VerifySummary) string {
	return fmt.Sprintf("%d fetched: %d the same, %d the same apart from redaction, %d different, %d not fetched", s.Sampled, s.SampleSame, s.SampleRedacted, s.SampleDifferent, s.SampleFailed)
}

// MigrateVerifyMarkdown renders a check as a bullet list, naming no file or query.
func MigrateVerifyMarkdown(w io.Writer, result *migrate.VerifyResult) error {
	var b strings.Builder
	for i := range result.Collections {
		s := &result.Collections[i]
		fmt.Fprintf(&b, "- **%s** to %s: %d local files, %d server documents, %d confirmed, %d taken over, %d at another version, %d not held, %d second spellings, %d not read here, %d not checked\n",
			inlineText(s.Name), inlineText(s.Target), s.LocalFiles, s.ServerDocuments, s.Confirmed, s.TakenOver, s.VersionDiffers, s.NotHeld, s.SecondSpellings, s.LocalUnreadable+s.FoldersSkipped, s.NotChecked)
		if s.CountDiffers {
			b.WriteString("  - the counts differ\n")
		}
		b.WriteString("  - sample: " + sampleText(s) + "\n")
		b.WriteString("  - overlap: " + inlineText(overlapText(s.Overlap, result.LocalMode)) + "\n")
	}
	for _, note := range verifyNotes(result) {
		b.WriteString("\n" + inlineText(note) + "\n")
	}
	return writeText(w, &b)
}
