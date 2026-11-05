package migrate

import "testing"

func TestCompareTextAllowsOnlyWhatRedactionExplains(t *testing.T) {
	local := "---\ndate: x\n---\n\none\nkey sk-real\nthree\n"
	for name, c := range map[string]struct {
		server string
		want   int
	}{
		"the same":              {local, TextSame},
		"line endings only":     {"---\r\ndate: x\r\n---\r\n\r\none\r\nkey sk-real\r\nthree\r\n", TextSame},
		"redacted":              {"---\ndate: x\n---\n\none\nkey [REDACTED:token]\nthree\n", TextRedacted},
		"a line changed":        {"---\ndate: x\n---\n\none\nkey [REDACTED:token]\nchanged\n", TextDifferent},
		"a line added":          {local + "\nsomething added\n", TextDifferent},
		"a line gone":           {"---\ndate: x\n---\n\none\nkey [REDACTED:token]\n", TextDifferent},
		"cut short":             {"---\ndate: x\n", TextDifferent},
		"empty":                 {"", TextDifferent},
		"redaction hides a cut": {"[REDACTED:token]\n", TextDifferent},
	} {
		if got := CompareText(local, c.server); got != c.want {
			t.Errorf("%s = %d, want %d", name, got, c.want)
		}
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
	all := VerifySummary{VersionDiffers: 1, NotHeld: 1, SecondSpellings: 1, LocalUnreadable: 1, NotChecked: 1, FoldersSkipped: 1, SampleDifferent: 1, SampleFailed: 1, Overlap: Overlap{Failed: 1}}
	if all.Problems() != 9 {
		t.Fatalf("problems = %d", all.Problems())
	}
}
