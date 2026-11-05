package migrate

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"time"

	"github.com/mggarofalo/memoria/cli/internal/auth"
)

const pushStateDir = "migrate-state"

// FrontmatterRead is the one answer that means a stored document matches date, project and tag filters.
const FrontmatterRead = "read"

// Action is what a push does about one answer from the server.
type Action int

// What a push can do about an answer.
const (
	// ActionStored means the document is stored.
	ActionStored Action = iota
	// ActionStop means nothing else will get through either, so the run ends and can resume.
	ActionStop
	// ActionReport refuses this document alone, and sending it again would change nothing.
	ActionReport
	// ActionOverCap is a refusal for size, which is kept apart as it can be acknowledged.
	ActionOverCap
	// ActionWait comes back after the time the server names.
	ActionWait
	// ActionPause holds every worker, as the server is out of room for now.
	ActionPause
	// ActionHold holds every worker for a key's rate limit, which says nothing about the document, so it has its own long budget.
	ActionHold
	// ActionResend is a lost answer or a server away. Sending again is always safe, and if it never answers the run ends.
	ActionResend
	// ActionResendThenReport is a failure that may be this document's own, so it cannot end the run forever.
	ActionResendThenReport
)

// Classify is the route's table of answers. Anything it does not name is reported for that document and not retried, as a
// refusal is the one outcome that cannot store something wrongly.
func Classify(status int, code string) Action {
	switch status {
	case http.StatusAccepted:
		return ActionStored
	case http.StatusUnauthorized, http.StatusForbidden, http.StatusNotFound:
		return ActionStop
	case http.StatusConflict:
		return classifyConflict(code)
	case http.StatusRequestEntityTooLarge:
		return ActionOverCap
	case http.StatusTooManyRequests:
		return ActionHold
	case http.StatusServiceUnavailable:
		if code == "ingest_busy" {
			return ActionPause
		}
		return ActionResend
	case http.StatusBadGateway, http.StatusGatewayTimeout:
		return ActionResend
	case http.StatusRequestTimeout, http.StatusInternalServerError:
		return ActionResendThenReport
	}
	return ActionReport
}

func classifyConflict(code string) Action {
	switch code {
	case "ingest_not_enabled", "collection_not_empty":
		return ActionStop
	case "ingest_in_progress":
		return ActionWait
	}
	return ActionReport
}

var safeCode = regexp.MustCompile(`^[a-z][a-z0-9_]{0,63}$`)

// The states of a path's ledger entry that mean the server holds it. Complete means a write of it landed, and taken over
// means its session now owns it.
const (
	StateComplete  = "complete"
	StateTakenOver = "taken_over"
)

// MayBeTakenOver is whether a refusal is what a re-push of a taken-over path gets. Only the path's own state can say that it was.
func MayBeTakenOver(code string) bool {
	return code == "path_owned" || code == "stale_version"
}

// ReasonOlderThanSent is a local copy older than one already stored, which this does not send.
const ReasonOlderThanSent = "older_than_sent"

// ReasonSecondSpelling is a file with the same composed name as one before it. Sent, it would replace that one, and one document
// would be lost with no refusal from anyone, so it stays here until the owner renames it.
const ReasonSecondSpelling = "second_spelling"

// SecondSpellings is the paths whose composed name an earlier path already has.
func SecondSpellings(names []string) map[string]bool {
	seen := make(map[string]bool, len(names))
	second := map[string]bool{}
	for _, name := range names {
		sent := ServerPath(name)
		if seen[sent] {
			second[name] = true
		}
		seen[sent] = true
	}
	return second
}

// Reason names a refusal for the summary. A server's code is only repeated if it looks like one, as a summary goes to logs.
func Reason(status int, code string) string {
	if safeCode.MatchString(code) {
		return code
	}
	return fmt.Sprintf("http_%d", status)
}

// PushEntry is what was stored for one path. Paths and progress only: no text, and no value from it.
type PushEntry struct {
	Updated     time.Time `json:"updated"`
	Bytes       int64     `json:"bytes"`
	Frontmatter string    `json:"frontmatter"`
	Redaction   string    `json:"redaction"`
}

// PushState is the progress of one collection to one server. Sending again is safe, so losing the last few entries in a
// kill costs only those documents being sent again.
type PushState struct {
	Origin     string               `json:"origin"`
	Collection string               `json:"collection"`
	Target     string               `json:"target"`
	Files      map[string]PushEntry `json:"files"`

	path string
}

func pushHash(parts ...string) (string, string, error) {
	dir, err := auth.ConfigDir()
	if err != nil {
		return "", "", err
	}
	digest := sha256.New()
	for _, part := range parts {
		_, _ = fmt.Fprintf(digest, "%d:%s", len(part), part)
	}
	return filepath.Join(dir, pushStateDir), hex.EncodeToString(digest.Sum(nil)[:16]), nil
}

// PushReportPath is where a run names the files it could not store, which is the only place they are named.
func PushReportPath(origin string) (string, error) {
	dir, name, err := pushHash(origin)
	if err != nil {
		return "", err
	}
	return filepath.Join(dir, name+"-report.json"), nil
}

// LoadPushState reads a collection's progress, or starts it. A damaged file is an error rather than a fresh start, which
// would read as nothing having been sent.
func LoadPushState(origin, collection, target string, ignoreSaved bool) (*PushState, error) {
	dir, name, err := pushHash(origin, collection, target)
	if err != nil {
		return nil, err
	}
	state := &PushState{Origin: origin, Collection: collection, Target: target, Files: map[string]PushEntry{}, path: filepath.Join(dir, name+".json")}
	if ignoreSaved {
		return state, nil
	}
	data, err := auth.ReadPrivateFile(state.path)
	if errors.Is(err, os.ErrNotExist) {
		return state, nil
	}
	if err != nil {
		return nil, err
	}
	if err := json.Unmarshal(data, state); err != nil {
		return nil, errors.New("the progress file at " + state.path + " is damaged. Pass --ignore-state to send everything again, which is safe")
	}
	if state.Files == nil {
		state.Files = map[string]PushEntry{}
	}
	return state, nil
}

// Save replaces the file atomically and privately, so an interruption leaves the last good one.
func (s *PushState) Save() error {
	data, err := json.MarshalIndent(s, "", "  ")
	if err != nil {
		return fmt.Errorf("failed to encode the progress: %w", err)
	}
	if err := auth.WritePrivateFile(s.path, append(data, '\n')); err != nil {
		return fmt.Errorf("failed to write the progress: %w", err)
	}
	return nil
}

// IsSent says whether this very version was stored. A file changed since is not.
func (s *PushState) IsSent(path string, updated time.Time, size int64) bool {
	entry, known := s.Files[path]
	return known && entry.Updated.Equal(updated) && entry.Bytes == size
}

// PushProblem is one document in the local report. It is the only record that names a file.
type PushProblem struct {
	Collection string `json:"collection"`
	Target     string `json:"target"`
	Path       string `json:"path"`
	Status     string `json:"status"`
	Reason     string `json:"reason,omitempty"`
}

// The statuses of a PushProblem.
const (
	ProblemRefused     = "refused"
	ProblemOverCap     = "over_cap"
	ProblemUnreadable  = "unreadable"
	ProblemNotReached  = "not_reached"
	ProblemFrontmatter = "frontmatter"
	ProblemBlank       = "blank"
	ProblemLocalEdits  = "taken_over_local_changes_not_sent"
)

// PushSummary is one collection's run. It holds counts, and reasons a server gave as codes, and names no file.
type PushSummary struct {
	Name             string `json:"name"`
	Target           string `json:"target"`
	Selected         int    `json:"selected"`
	Bytes            int64  `json:"bytes"`
	OverDefaultLimit int    `json:"overDefaultLimit"`
	Sent             int    `json:"sent"`
	AlreadySent      int    `json:"alreadySent"`
	// TakenOver is documents whose session has since replaced them, which the server confirmed. They are migrated, not failed.
	TakenOver int `json:"takenOver"`
	// TakenOverEdited is those of TakenOver with a local copy different from what was migrated. Those changes were not sent.
	TakenOverEdited int `json:"takenOverWithLocalChanges"`
	// Blank is files left out of Selected, as QMD does not index a blank file.
	Blank            int            `json:"blank"`
	Refused          map[string]int `json:"refused"`
	OverCap          int            `json:"overCap"`
	Unreadable       int            `json:"unreadable"`
	NotReached       int            `json:"notReached"`
	FoldersSkipped   int            `json:"foldersSkipped"`
	Unstorable       map[string]int `json:"unstorable,omitempty"`
	DecomposedNames  int            `json:"decomposedNames,omitempty"`
	Frontmatter      map[string]int `json:"frontmatterIssues"`
	StoredUnredacted int            `json:"storedUnredacted"`
	Redactions       map[string]int `json:"redactionsThisRun"`
	NoLongerSelected int            `json:"noLongerSelected"`
}

// RefusedTotal is every document refused, apart from those over the cap.
func (s *PushSummary) RefusedTotal() int {
	total := 0
	for _, count := range s.Refused {
		total += count
	}
	return total
}

// NotStored is every selected document the server does not hold, leaving out what the caller has acknowledged.
func (s *PushSummary) NotStored() int {
	return s.RefusedTotal() + s.Unreadable + s.NotReached
}

// Accounted reports whether every selected document is in exactly one count, which is what makes the summary worth trusting.
func (s *PushSummary) Accounted() bool {
	return s.Sent+s.AlreadySent+s.TakenOver+s.NotStored()+s.OverCap == s.Selected
}

// PushResult is what a run says on stdout.
type PushResult struct {
	DryRun      bool          `json:"dryRun"`
	Collections []PushSummary `json:"collections"`
	ReportFile  string        `json:"reportFile,omitempty"`
}
