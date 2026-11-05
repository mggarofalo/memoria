package migrate

import (
	"net/http"
	"testing"
	"time"

	"github.com/mggarofalo/memoria/cli/internal/auth"
)

func TestClassifyFollowsTheRoutesTable(t *testing.T) {
	for _, c := range []struct {
		status int
		code   string
		want   Action
	}{
		{http.StatusAccepted, "", ActionStored},
		{http.StatusForbidden, "", ActionStop},
		{http.StatusUnauthorized, "", ActionStop},
		{http.StatusNotFound, "", ActionStop},
		{http.StatusConflict, "ingest_not_enabled", ActionStop},
		{http.StatusConflict, "collection_not_empty", ActionStop},
		{http.StatusBadRequest, "", ActionReport},
		{http.StatusConflict, "stale_version", ActionReport},
		{http.StatusConflict, "path_owned", ActionReport},
		{http.StatusConflict, "case_conflict", ActionReport},
		{http.StatusConflict, "deleted_during_upload", ActionReport},
		{http.StatusConflict, "", ActionReport},
		{http.StatusUnprocessableEntity, "", ActionReport},
		{http.StatusRequestEntityTooLarge, "", ActionOverCap},
		{http.StatusConflict, "ingest_in_progress", ActionWait},
		{http.StatusTooManyRequests, "", ActionHold},
		{http.StatusServiceUnavailable, "ingest_busy", ActionPause},
		{http.StatusBadGateway, "", ActionResend},
		{http.StatusServiceUnavailable, "", ActionResend},
		{http.StatusServiceUnavailable, "sidecar_unreachable", ActionResend},
		{http.StatusGatewayTimeout, "", ActionResend},
		{http.StatusInternalServerError, "", ActionResendThenReport},
		{http.StatusRequestTimeout, "", ActionResendThenReport},
		{http.StatusTeapot, "", ActionReport},
	} {
		if got := Classify(c.status, c.code); got != c.want {
			t.Errorf("%d %q = %d, want %d", c.status, c.code, got, c.want)
		}
	}
}

func TestReasonOnlyRepeatsWhatLooksLikeACode(t *testing.T) {
	for code, want := range map[string]string{
		"stale_version":                 "stale_version",
		"":                              "http_409",
		"C:\\Users\\someone\\secret.md": "http_409",
		"a b":                           "http_409",
	} {
		if got := Reason(http.StatusConflict, code); got != want {
			t.Errorf("Reason(%q) = %q, want %q", code, got, want)
		}
	}
}

func TestAChangedFileIsNotCountedAsSent(t *testing.T) {
	now := time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)
	state := PushState{Files: map[string]PushEntry{"a.md": {Updated: now, Bytes: 3}}}
	if !state.IsSent("a.md", now, 3) {
		t.Fatal("the same version was not recognized")
	}
	if state.IsSent("a.md", now.Add(time.Second), 3) || state.IsSent("a.md", now, 4) || state.IsSent("b.md", now, 3) {
		t.Fatal("a different version counted as sent")
	}
}

func TestEveryDocumentIsInOneCount(t *testing.T) {
	summary := PushSummary{Selected: 6, Sent: 1, AlreadySent: 1, Refused: map[string]int{"x": 1}, OverCap: 1, Unreadable: 1, NotReached: 1}
	if !summary.Accounted() || summary.NotStored() != 3 || summary.RefusedTotal() != 1 {
		t.Fatalf("summary = %+v", summary)
	}
	summary.Sent = 2
	if summary.Accounted() {
		t.Fatal("too many counted")
	}
}

func TestProgressIsKeptPerServerAndCollection(t *testing.T) {
	t.Setenv("XDG_CONFIG_HOME", t.TempDir())
	when := time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)
	state, err := LoadPushState("https://a", "notes", "archive", false)
	if err != nil || len(state.Files) != 0 {
		t.Fatalf("state = %+v, err = %v", state, err)
	}
	state.Files["a.md"] = PushEntry{Updated: when, Bytes: 3, Frontmatter: FrontmatterRead}
	if err := state.Save(); err != nil {
		t.Fatal(err)
	}
	again, err := LoadPushState("https://a", "notes", "archive", false)
	if err != nil || !again.IsSent("a.md", when, 3) {
		t.Fatalf("state = %+v, err = %v", again, err)
	}
	for _, other := range [][3]string{{"https://b", "notes", "archive"}, {"https://a", "other", "archive"}, {"https://a", "notes", "other"}} {
		checkEmptyProgress(t, other[0], other[1], other[2], false)
	}
	checkEmptyProgress(t, "https://a", "notes", "archive", true)
}

func checkEmptyProgress(t *testing.T, origin, collection, target string, ignore bool) {
	t.Helper()
	fresh, err := LoadPushState(origin, collection, target, ignore)
	if err != nil || len(fresh.Files) != 0 {
		t.Errorf("%s %s %s saw progress it should not: %+v, %v", origin, collection, target, fresh, err)
	}
}

func TestEachServerHasItsOwnReport(t *testing.T) {
	t.Setenv("XDG_CONFIG_HOME", t.TempDir())
	first, err := PushReportPath("https://a")
	if err != nil {
		t.Fatal(err)
	}
	second, err := PushReportPath("https://b")
	if err != nil || first == second {
		t.Fatalf("reports %q and %q: %v", first, second, err)
	}
}

func TestDamagedOrEmptyProgress(t *testing.T) {
	t.Setenv("XDG_CONFIG_HOME", t.TempDir())
	state, err := LoadPushState("o", "c", "t", false)
	if err != nil {
		t.Fatal(err)
	}
	state.Files = nil
	if err := state.Save(); err != nil {
		t.Fatal(err)
	}
	empty, err := LoadPushState("o", "c", "t", false)
	if err != nil || empty.Files == nil {
		t.Fatalf("state = %+v, err = %v", empty, err)
	}
	if err := auth.WritePrivateFile(state.path, []byte("not json")); err != nil {
		t.Fatal(err)
	}
	if _, err := LoadPushState("o", "c", "t", false); err == nil {
		t.Fatal("damaged progress read as a fresh start")
	}
}
