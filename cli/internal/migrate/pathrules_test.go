package migrate

import (
	"encoding/json"
	"os"
	"strings"
	"testing"
)

// The same cases go to a real API in scripts/e2e, so these rules and the server's cannot drift apart unnoticed.
func TestPathRulesMatchTheSharedCases(t *testing.T) {
	data, err := os.ReadFile("testdata/path-rules.json")
	if err != nil {
		t.Fatal(err)
	}
	var cases []struct {
		Path   string `json:"path"`
		Reason string `json:"reason"`
	}
	if err := json.Unmarshal(data, &cases); err != nil {
		t.Fatal(err)
	}
	if len(cases) < 30 {
		t.Fatalf("only %d cases", len(cases))
	}
	for _, c := range cases {
		if got := PathProblem(c.Path); got != c.Reason {
			t.Errorf("%q = %q, want %q", c.Path, got, c.Reason)
		}
	}
}

func TestPathRulesTooLongToFit(t *testing.T) {
	for name, want := range map[string]string{
		strings.Repeat("a", 251) + ".md":     "",
		strings.Repeat("a", 253) + ".md":     ReasonSegmentLong,
		strings.Repeat("é", 127) + ".md":     ReasonSegmentLong,
		strings.Repeat("a/", 31) + "b.md":    "",
		strings.Repeat("a/", 32) + "b.md":    ReasonTooDeep,
		strings.Repeat("abc/", 256) + "b.md": ReasonTooLong,
		`back\slash.md`:                      ReasonBackslash,
	} {
		if got := PathProblem(name); got != want {
			t.Errorf("%.20q... = %q, want %q", name, got, want)
		}
	}
	long := strings.Repeat(strings.Repeat("b", 100)+"/", 11) + "c.md"
	if got := PathProblem(long); got != ReasonTooLong {
		t.Errorf("a long path = %q", got)
	}
}

func TestADecomposedNameIsSentComposedTheSameEveryTime(t *testing.T) {
	decomposed, composed := "café/a.md", "café/a.md"
	if ServerPath(decomposed) != composed || ServerPath(ServerPath(decomposed)) != composed || ServerPath(composed) != composed {
		t.Fatalf("ServerPath = %q", ServerPath(decomposed))
	}
	if PathProblem(decomposed) != "" {
		t.Fatalf("problem = %q", PathProblem(decomposed))
	}
}

func TestNamesThatDifferOnlyInCaseAreCounted(t *testing.T) {
	counts := Unstorable([]string{"Note.md", "note.md", "other.md", "Dir/a.md", "dir/b.md", "Dir/c.md", "café.md", "café.md", "x~1.md", "straße.md", "STRASSE.md"})
	want := map[string]int{ReasonCaseConflict: 4, ReasonShortName: 1}
	if len(counts) != len(want) || counts[ReasonCaseConflict] != want[ReasonCaseConflict] || counts[ReasonShortName] != want[ReasonShortName] {
		t.Fatalf("counts = %v, want %v", counts, want)
	}
}
