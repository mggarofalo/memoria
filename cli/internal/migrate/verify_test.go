package migrate

import (
	"strings"
	"testing"
)

func TestCompareTextAllowsOnlyWhatRedactionExplains(t *testing.T) {
	local := "---\ndate: x\nkey: abc\n---\n\none\nkey sk-real here\nthree\n"
	for name, c := range map[string]struct {
		server string
		want   int
	}{
		"the same":                   {local, TextSame},
		"line endings only":          {strings.ReplaceAll(local, "\n", "\r\n"), TextSame},
		"redacted inside a line":     {strings.Replace(local, "sk-real", "[REDACTED:token]", 1), TextRedacted},
		"redacted whole value":       {strings.Replace(local, "key: abc", "key: \"[REDACTED:token]\"", 1), TextRedacted},
		"a line changed":             {strings.Replace(strings.Replace(local, "sk-real", "[REDACTED:token]", 1), "three", "changed", 1), TextDifferent},
		"a line gone":                {strings.Replace(strings.Replace(local, "sk-real", "[REDACTED:token]", 1), "three\n", "", 1), TextDifferent},
		"a line added":               {local + "\nsomething added\n", TextDifferent},
		"out of order":               {strings.Replace(strings.Replace(local, "one\nkey sk-real here", "key [REDACTED:token] here\none", 1), "", "", 0), TextDifferent},
		"a mark excuses another":     {strings.Replace(local, "three", "[REDACTED:token]\nextra", 1), TextDifferent},
		"text around a mark differs": {strings.Replace(local, "key sk-real here", "lock [REDACTED:token] there", 1), TextDifferent},
		"cut short":                  {"---\ndate: x\n", TextDifferent},
		"empty":                      {"", TextDifferent},
		"a mark hiding everything":   {"[REDACTED:token]\n", TextDifferent},
	} {
		if got := CompareText(local, c.server); got != c.want {
			t.Errorf("%s = %d, want %d", name, got, c.want)
		}
	}
}

func TestOneMarkMayCoverOnlyWhatRedactionFolds(t *testing.T) {
	block := "before\n-----BEGIN PRIVATE KEY-----\nAAAA\nBBBB\n-----END PRIVATE KEY-----\nafter\n"
	if got := CompareText(block, "before\n[REDACTED:private-key]\nafter\n"); got != TextRedacted {
		t.Errorf("a key block = %d", got)
	}
	wrapped := "token: abcd\nefgh\nijkl\nend\n"
	if got := CompareText(wrapped, "token: [REDACTED:token]\nend\n"); got != TextRedacted {
		t.Errorf("a wrapped token = %d", got)
	}
	prose := "first line of prose\nsecond line of prose\nthird line of prose\n"
	if got := CompareText(prose, "[REDACTED:token]\n"); got != TextDifferent {
		t.Errorf("a mark over prose = %d", got)
	}
}

func TestHitsAreComparedByPathWhicheverCollectionHoldsThem(t *testing.T) {
	keys, err := ParseLocalHits([]byte(`[{"file":"qmd://notes/Sub/A.md"},{"file":"notes/b.md"}]`), "notes")
	if err != nil || len(keys) != 2 || keys[0] != "sub/a.md" || keys[1] != "b.md" {
		t.Fatalf("keys = %v, err = %v", keys, err)
	}
	if got := HitKey("archive/sub/a.md", "archive"); got != "sub/a.md" {
		t.Fatalf("server key = %q", got)
	}
}

func TestNoLocalResultsAndGarbageAreToldApart(t *testing.T) {
	if keys, err := ParseLocalHits([]byte("No results found."), "notes"); err != nil || len(keys) != 0 {
		t.Fatalf("keys = %v, err = %v", keys, err)
	}
	if _, err := ParseLocalHits([]byte("[not json"), "notes"); err == nil {
		t.Fatal("garbage was read as results")
	}
}

func TestOverlapIsAShareOfLocalResults(t *testing.T) {
	if got := Share([]string{"a", "b", "c", "d"}, []string{"a", "x", "c"}); got != 0.5 {
		t.Fatalf("share = %v", got)
	}
	overlap := Overlap{}
	overlap.Add(0.5)
	overlap.Add(1)
	if overlap.Queries != 2 || overlap.Mean != 0.75 || overlap.Lowest != 0.5 {
		t.Fatalf("overlap = %+v", overlap)
	}
	overlap.Add(0.75)
	if overlap.Lowest != 0.5 || overlap.Mean != 0.75 {
		t.Fatalf("overlap = %+v", overlap)
	}
}

func TestOverlapIsNotAProblemAndTheRestAre(t *testing.T) {
	clean := VerifySummary{Overlap: Overlap{Queries: 3, Mean: 0.1, Lowest: 0}}
	if clean.Problems() != 0 {
		t.Fatalf("low overlap failed the check: %d", clean.Problems())
	}
	all := VerifySummary{VersionDiffers: 1, NotHeld: 1, SecondSpellings: 1, LocalUnreadable: 1, NotChecked: 1, FoldersSkipped: 1, SampleDifferent: 1, SampleFailed: 1, CountDiffers: true, Overlap: Overlap{Failed: 1}}
	if all.Problems() != 10 {
		t.Fatalf("problems = %d", all.Problems())
	}
}
