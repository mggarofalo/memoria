package output

import (
	"io"
	"strings"
	"testing"

	"github.com/mggarofalo/memoria/cli/internal/migrate"
)

func TestARerenderSaysWhatIsNotHeldAndWhy(t *testing.T) {
	result := &migrate.RerenderResult{ReportFile: "r.json", Collections: []migrate.RerenderSummary{{Name: "codex", Target: "codex", OverCap: 4, Sent: 2, NoTranscript: 1, Refused: map[string]int{"session_shrank": 1}}}}
	checkHolds(t, renderedBy(t, func(w io.Writer) error { return MigrateRerenderTable(w, result) }), RerenderNoTranscriptNote, RerenderTakeoverNote, "2 of 4 over-cap document(s) are not held", "session_shrank", "r.json")
	// Markdown escapes its text, so only what needs no escaping is looked for here.
	checkHolds(t, renderedBy(t, func(w io.Writer) error { return MigrateRerenderMarkdown(w, result) }), "4 over cap", "1 with no transcript", "2 of 4 over", "only by its export")
	lost := &migrate.RerenderResult{Collections: []migrate.RerenderSummary{{Name: "codex", OverCap: 3, Shorter: 1, Uncounted: 1, Refused: map[string]int{"session_shrank": 1}}}}
	checkHolds(t, renderedBy(t, func(w io.Writer) error { return MigrateRerenderTable(w, lost) }), RerenderShorterNote, RerenderShrankNote, RerenderUncountedNote, "TRANSCRIPT SHORTER")
	accepted := &migrate.RerenderResult{Collections: []migrate.RerenderSummary{{Name: "codex", OverCap: 1, Sent: 1, AcceptedUncounted: 1}}}
	checkHolds(t, renderedBy(t, func(w io.Writer) error { return MigrateRerenderTable(w, accepted) }), RerenderAcceptedNote, "1 sent with --accept-uncounted")
	clean := &migrate.RerenderResult{Collections: []migrate.RerenderSummary{{Name: "codex", OverCap: 1, Sent: 1}}}
	text := renderedBy(t, func(w io.Writer) error { return MigrateRerenderTable(w, clean) })
	if !strings.Contains(text, "All 1 over-cap document(s) are held") || strings.Contains(text, RerenderNoTranscriptNote) || strings.Contains(text, RerenderShrankNote) || strings.Contains(text, RerenderShorterNote) {
		t.Fatalf("text = %s", text)
	}
	dry := &migrate.RerenderResult{DryRun: true, Collections: []migrate.RerenderSummary{{Name: "codex", OverCap: 1, WouldSend: 1}}}
	checkHolds(t, renderedBy(t, func(w io.Writer) error { return MigrateRerenderTable(w, dry) }), "WOULD SEND", "Dry run: nothing was sent.")
}

func TestVerifySaysWhenASessionHoldsItsDocuments(t *testing.T) {
	held := &migrate.VerifyResult{Collections: []migrate.VerifySummary{{Name: "a", HeldBySession: 2, TakenOver: 1}}}
	checkHolds(t, renderedBy(t, func(w io.Writer) error { return MigrateVerifyTable(w, held) }), "2 document(s) are held through their session's parts", "were not compared with them or sampled")
}
