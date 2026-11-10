package output

import (
	"fmt"
	"io"
	"strings"

	"github.com/mggarofalo/memoria/cli/internal/migrate"
)

// RerenderNoTranscriptNote is said whenever a transcript is missing, as that document then has no copy on the server.
const RerenderNoTranscriptNote = "An over-cap document with no raw transcript here is held only by its export, which the server cannot store. Find its transcript, or keep the export."

// RerenderTakeoverNote says what a Codex session does to an export, which proves nothing about which copy it is.
const RerenderTakeoverNote = "A Codex session takes over documents in the collection root whose frontmatter names it. One that would lose messages is refused with session_shrank, and nothing changes."

// RerenderShorterNote is said when a transcript holds fewer messages than its exports, which were then not sent.
const RerenderShorterNote = "A transcript with fewer messages than its exports was not sent: the export is the only copy of the messages it lacks. The local report says how many."

// RerenderShrankNote is said for session_shrank, as a refusal by code alone does not say what it leaves.
const RerenderShrankNote = "session_shrank means the server kept what it had: the export is still the only copy of the messages the transcript lacks."

// RerenderUncountedNote is said for an export that does not say how far its messages reach, and whose body does not show it.
const RerenderUncountedNote = "An export that does not count its messages, and whose body is not its transcript's, was not sent: nothing shows the transcript has them all, so its export is its only copy. After looking, --accept-uncounted sends it anyway."

// RerenderAcceptedNote is said whenever --accept-uncounted let an export through, so it is never silent.
const RerenderAcceptedNote = "Sent with --accept-uncounted, on your word: for these, nothing showed the transcript holds every message the export does. The local report names them."

func rerenderLines(s *migrate.RerenderSummary) []string {
	var lines []string
	for _, reason := range ruleNames(s.Refused) {
		lines = append(lines, fmt.Sprintf("%s: refused, %s: %d", s.Name, reason, s.Refused[reason]))
	}
	if s.AcceptedUncounted > 0 {
		lines = append(lines, fmt.Sprintf("%s: %d sent with --accept-uncounted", s.Name, s.AcceptedUncounted))
	}
	if s.Threads > 0 {
		lines = append(lines, fmt.Sprintf("%s: %d raw transcript(s) for these, as one export can hold its session's subagent threads too", s.Name, s.Threads))
	}
	return lines
}

func rerenderNotes(result *migrate.RerenderResult) []string {
	var notes []string
	missing, notHeld, overCap := 0, 0, 0
	for i := range result.Collections {
		missing += result.Collections[i].NoTranscript
		notHeld += result.Collections[i].NotHeld()
		overCap += result.Collections[i].OverCap
	}
	if missing > 0 {
		notes = append(notes, RerenderNoTranscriptNote)
	}
	notes = append(notes, lossNotes(result)...)
	notes = append(notes, RerenderTakeoverNote)
	switch {
	case result.DryRun:
		notes = append(notes, "Dry run: nothing was sent.")
	case notHeld == 0:
		notes = append(notes, fmt.Sprintf("All %d over-cap document(s) are held through their session.", overCap))
	default:
		notes = append(notes, fmt.Sprintf("%d of %d over-cap document(s) are not held. The local report names them.", notHeld, overCap))
	}
	if result.ReportFile != "" {
		notes = append(notes, "Local report: "+result.ReportFile)
	}
	return notes
}

func lossNotes(result *migrate.RerenderResult) []string {
	shorter, shrank, uncounted, accepted := 0, 0, 0, 0
	for i := range result.Collections {
		shorter += result.Collections[i].Shorter
		shrank += result.Collections[i].Refused["session_shrank"]
		uncounted += result.Collections[i].Uncounted
		accepted += result.Collections[i].AcceptedUncounted
	}
	var notes []string
	for _, said := range []struct {
		count int
		note  string
	}{{shorter, RerenderShorterNote}, {shrank, RerenderShrankNote}, {uncounted, RerenderUncountedNote}, {accepted, RerenderAcceptedNote}} {
		if said.count > 0 {
			notes = append(notes, said.note)
		}
	}
	return notes
}

// MigrateRerenderTable renders a re-render. It names no file: only the local report does.
func MigrateRerenderTable(w io.Writer, result *migrate.RerenderResult) error {
	rows := make([]string, 0, len(result.Collections))
	var b strings.Builder
	for i := range result.Collections {
		s := &result.Collections[i]
		sent := []any{s.Sent, s.Already, s.Partial, s.RefusedTotal()}
		if result.DryRun {
			sent = []any{s.WouldSend}
		}
		rows = append(rows, row(append(append([]any{s.Name, s.Target, s.OverCap}, sent...), s.NoSessionID, s.NoTranscript, s.Shorter, s.Uncounted, s.Several, s.Unreadable, s.NotReached)...))
		for _, line := range rerenderLines(s) {
			b.WriteString(row(line) + "\n")
		}
	}
	header := "COLLECTION\tTARGET\tOVER CAP\tSENT\tALREADY SENT\tPARTIAL\tREFUSED\tNO SESSION ID\tNO TRANSCRIPT\tTRANSCRIPT SHORTER\tNOT COUNTED\tSEVERAL TRANSCRIPTS\tUNREADABLE\tNOT REACHED"
	if result.DryRun {
		header = "COLLECTION\tTARGET\tOVER CAP\tWOULD SEND\tNO SESSION ID\tNO TRANSCRIPT\tTRANSCRIPT SHORTER\tNOT COUNTED\tSEVERAL TRANSCRIPTS\tUNREADABLE\tNOT REACHED"
	}
	if err := table(w, header, rows); err != nil {
		return err
	}
	for _, note := range rerenderNotes(result) {
		b.WriteString(row(note) + "\n")
	}
	return writeText(w, &b)
}

// MigrateRerenderMarkdown renders a re-render as a bullet list, naming no file.
func MigrateRerenderMarkdown(w io.Writer, result *migrate.RerenderResult) error {
	var b strings.Builder
	for i := range result.Collections {
		s := &result.Collections[i]
		fmt.Fprintf(&b, "- **%s** to %s: %d over cap, %d would send, %d sent, %d already sent, %d partial, %d refused, %d with no session id, %d with no transcript, %d with a shorter transcript, %d not counted, %d with several, %d unreadable, %d not reached\n",
			inlineText(s.Name), inlineText(s.Target), s.OverCap, s.WouldSend, s.Sent, s.Already, s.Partial, s.RefusedTotal(), s.NoSessionID, s.NoTranscript, s.Shorter, s.Uncounted, s.Several, s.Unreadable, s.NotReached)
		for _, line := range rerenderLines(s) {
			b.WriteString("  - " + inlineText(line) + "\n")
		}
	}
	for _, note := range rerenderNotes(result) {
		b.WriteString("\n" + inlineText(note) + "\n")
	}
	return writeText(w, &b)
}
