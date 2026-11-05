package output

import (
	"bytes"
	"io"
	"strings"
	"testing"

	"github.com/mggarofalo/memoria/cli/internal/migrate"
)

func renderedBy(t *testing.T, render func(io.Writer) error) string {
	t.Helper()
	var out bytes.Buffer
	if err := render(&out); err != nil {
		t.Fatal(err)
	}
	return out.String()
}

func checkHolds(t *testing.T, text string, wants ...string) {
	t.Helper()
	for _, want := range wants {
		if !strings.Contains(text, want) {
			t.Errorf("%q is missing from %q", want, text)
		}
	}
}

func pushWithEverything() *migrate.PushResult {
	return &migrate.PushResult{
		ReportFile: "report.json",
		Collections: []migrate.PushSummary{{
			Name: "notes", Target: "archive", Selected: 7, Sent: 1, AlreadySent: 1, OverCap: 1, Unreadable: 1, NotReached: 1,
			Refused: map[string]int{"stale": 2}, Frontmatter: map[string]int{"missing": 1}, Redactions: map[string]int{"token": 3},
			Unstorable: map[string]int{"short": 2}, DecomposedNames: 1, StoredUnredacted: 1, NoLongerSelected: 1,
		}},
	}
}

func TestAPushWithEverythingWrongSaysSo(t *testing.T) {
	result := pushWithEverything()
	wants := []string{"NOT everything was stored", "refused, stale: 2", "frontmatter missing: 1", "token: 3 replacements", "1 stored unredacted", "1 sent earlier", "report.json", "match no date", "composed form"}
	table := renderedBy(t, func(w io.Writer) error { return MigratePushTable(w, result) })
	checkHolds(t, table, append(wants, "OVER CAP")...)
	markdown := renderedBy(t, func(w io.Writer) error { return MigratePushMarkdown(w, result) })
	checkHolds(t, markdown, "1 over cap", "NOT everything was stored", "2 refused")
}

func TestACleanPushAndADryRunSaySo(t *testing.T) {
	clean := &migrate.PushResult{Collections: []migrate.PushSummary{{Name: "notes", Target: "notes", Selected: 1, Sent: 1}}}
	checkHolds(t, renderedBy(t, func(w io.Writer) error { return MigratePushTable(w, clean) }), "Every selected document is stored")
	edited := &migrate.PushResult{Collections: []migrate.PushSummary{{Name: "notes", Target: "notes", Selected: 1, TakenOver: 1, TakenOverEdited: 1, Blank: 2}}}
	checkHolds(t, renderedBy(t, func(w io.Writer) error { return MigratePushTable(w, edited) }), "1 of those have local changes", "2 blank file(s)", "0 stored", "1 taken over", "not that their text is still held")
	dry := &migrate.PushResult{DryRun: true, Collections: []migrate.PushSummary{{Name: "notes", Target: "notes", Selected: 2, Unstorable: map[string]int{"short": 1}}}}
	checkHolds(t, renderedBy(t, func(w io.Writer) error { return MigratePushTable(w, dry) }), "Dry run", "WOULD SEND", "CANNOT BE STORED", "cannot be stored at its path, short: 1")
}

func TestAPlanSaysWhatCannotBeStored(t *testing.T) {
	plans := []migrate.Plan{{Name: "notes", Target: "notes", Files: 2, Unstorable: map[string]int{"short": 2}, Decomposed: 1}}
	raw := &migrate.RawSessions{Target: "codex", Files: 1}
	checkHolds(t, renderedBy(t, func(w io.Writer) error { return MigratePlanTable(w, plans, raw) }), "cannot be stored at its path, short: 2", "composed form", "raw Codex sessions: 1 files")
	checkHolds(t, renderedBy(t, func(w io.Writer) error { return MigratePlanMarkdown(w, plans, raw) }), "short: 2", "raw Codex")
	failed := []migrate.Plan{{Name: "notes", Problem: "its folder cannot be opened"}}
	checkHolds(t, renderedBy(t, func(w io.Writer) error { return MigratePlanTable(w, failed, &migrate.RawSessions{Problem: "gone"}) }), "cannot be opened", "gone")
}

func TestAReportCountsCredentialNamesAndCaveatsGitleaks(t *testing.T) {
	reports := []migrate.Report{{Name: "notes", CheckResult: migrate.CheckResult{
		Files: 1, CredentialNames: 1, Unscanned: 1, GitleaksRan: true, GitleaksResults: map[string]int{"rule": 2}, Replacements: map[string]int{"r": 1}, FilesByRule: map[string]int{"r": 1},
	}}}
	checkHolds(t, renderedBy(t, func(w io.Writer) error { return MigrateReportTable(w, reports) }), "1 file name(s) look like a credential", "best effort", "UNSCANNED", "rule: 2 left", "Redaction keeps what follows a secret")
	checkHolds(t, renderedBy(t, func(w io.Writer) error { return MigrateReportMarkdown(w, reports) }), "1 file name(s) look like a credential", "best effort", "1 not scanned", "Redaction keeps what follows a secret")
	unrun := []migrate.Report{{Name: "notes"}}
	checkHolds(t, renderedBy(t, func(w io.Writer) error { return MigrateReportTable(w, unrun) }), "NOT RUN")
}

func TestAVerifySaysWhatItFoundAndWhatItDoesNotShow(t *testing.T) {
	result := &migrate.VerifyResult{LocalMode: "search", ReportFile: "report.json", Collections: []migrate.VerifySummary{
		{Name: "a", Target: "a", LocalFiles: 2, Confirmed: 1, NotHeld: 1, Sampled: 1, SampleRedacted: 1, Overlap: migrate.Overlap{Queries: 3, NoLocalResults: 1, Mean: 0.5, Lowest: 0.25}},
		{Name: "b", Target: "b", Overlap: migrate.Overlap{Queries: 1, Failed: 1}},
		{Name: "c", Target: "c"},
	}}
	wants := []string{"need a look", "report.json", "does not show every document", "mean 50%, lowest 25%", "no query could be compared", "not run, as no --queries", "the same apart from redaction", "local search"}
	checkHolds(t, renderedBy(t, func(w io.Writer) error { return MigrateVerifyTable(w, result) }), append(wants, "SERVER DOCUMENTS")...)
	checkHolds(t, renderedBy(t, func(w io.Writer) error { return MigrateVerifyMarkdown(w, result) }), "need a look", "does not show every document", "mean 50%, lowest 25%", "1 not held")
	clean := &migrate.VerifyResult{LocalMode: "search", Collections: []migrate.VerifySummary{{Name: "a", Target: "a", Confirmed: 1}}}
	checkHolds(t, renderedBy(t, func(w io.Writer) error { return MigrateVerifyTable(w, clean) }), "Nothing here needs a look, within the limits below")
}
