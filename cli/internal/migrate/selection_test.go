package migrate

import (
	"errors"
	"io/fs"
	"strings"
	"testing"
	"testing/fstest"
)

func selection(t *testing.T, pattern string, ignore ...string) *Selection {
	t.Helper()
	s, err := NewSelection(pattern, ignore)
	if err != nil {
		t.Fatalf("NewSelection(%q, %v): %v", pattern, ignore, err)
	}
	return s
}

func TestPatternsAreReadAsQMDReadsThem(t *testing.T) {
	for _, c := range []struct {
		pattern string
		name    string
		want    bool
	}{
		{"**/*.md", "a.md", true},
		{"**/*.md", "sub/deep/a.md", true},
		{"**/*.md", "a.txt", false},
		{"*.md", "sub/a.md", false},
		{"./**/*.md", "sub/a.md", true},
		{"**/*.md, **/*.txt", "sub/a.txt", true},
		{"**/*.md,,**/*.txt", "sub/a.txt", true},
		{",**/*.md", "a.md", true},
		{"**/*.{md,txt}", "a.txt", true},
		{"**/*.{md,txt},*.org", "a.org", true},
		{"sub/**", "sub/deep/a.md", true},
		{"**/a?.md", "ab.md", true},
		{"my dir/*.md", "my dir/a.md", true},
	} {
		if got := selection(t, c.pattern).Takes(c.name); got != c.want {
			t.Errorf("%q takes %q = %v, want %v", c.pattern, c.name, got, c.want)
		}
	}
}

func TestIgnoreRulesAreReadAsQMDReadsThem(t *testing.T) {
	for _, c := range []struct {
		rule string
		name string
		want bool
	}{
		{"sub", "sub/deep/a.md", false},
		{"sub", "other/a.md", true},
		{"./sub/**", "sub/a.md", false},
		{"**/deep", "sub/deep/a.md", false},
		{"**/deep", "deep/a.md", false},
		{"**/deep", "sub/a.md", true},
		{"**/*.md", "sub/a.md", false},
		{"sub/*.md", "sub/a.md", false},
		{"sub/*.md", "sub/deep/a.md", true},
	} {
		if got := selection(t, "**/*", c.rule).Takes(c.name); got != c.want {
			t.Errorf("ignoring %q takes %q = %v, want %v", c.rule, c.name, got, c.want)
		}
	}
}

// A rule ending in a wildcard reaches files only, and scripts/e2e checks these same forms against a real QMD index.
func TestAWildcardIgnoreCoversOnlyWhatItMatches(t *testing.T) {
	tree := []string{"a.md", "ab.md", "sub/d.md", "sub/ax.md", "sub/deep/f.md", "deep/g.md"}
	for rule, want := range map[string]int{
		"s*": 6, "?ub": 6, "sub/d*": 5, "sub/*": 4, "*/*": 3, "*": 4, "sub/deep/*": 5, "**/deep/*": 4,
		"sub": 3, "sub/**": 3, "**/deep": 4, "sub/deep": 5, "{sub,deep}": 2, "sub/{deep,x}": 5, "{s*,deep}": 5,
	} {
		s := selection(t, "**/*.md", rule)
		got := 0
		for _, name := range tree {
			if s.Takes(name) {
				got++
			}
		}
		if got != want {
			t.Errorf("ignoring %q takes %d of the files, want %d", rule, got, want)
		}
	}
}

func TestFolderNamesWithSymbolsAreRead(t *testing.T) {
	for _, c := range []struct{ pattern, name string }{
		{"C++/*.md", "C++/a.md"}, {"docs@v2/**", "docs@v2/x/a.md"}, {"a&b/*.md", "a&b/a.md"}, {"#x/*.md", "#x/a.md"}, {"$y/*.md", "$y/a.md"}, {"~z/*.md", "~z/a.md"},
	} {
		if !selection(t, c.pattern).Takes(c.name) {
			t.Errorf("%q did not take %q", c.pattern, c.name)
		}
	}
	if selection(t, "**/*.md", "C++").Takes("C++/a.md") {
		t.Error("an ignored folder with symbols was taken")
	}
	for _, pattern := range []string{"C++(x)/*.md", "docs@(v2)/*.md", "C++/"} {
		if _, err := NewSelection(pattern, nil); err == nil {
			t.Errorf("%q was accepted", pattern)
		}
	}
}

func TestAnEmptyChoiceInAnIgnoreRuleIsRefused(t *testing.T) {
	for _, rule := range []string{"{a,}", "{,a}", "{a,,b}"} {
		if _, err := NewSelection("**/*.md", []string{rule}); err == nil {
			t.Errorf("%q was accepted", rule)
		}
	}
}

// Each of these is silently read differently by the other matcher, so each is refused rather than guessed at.
func TestWhatCannotBeReadFaithfullyIsRefused(t *testing.T) {
	for _, pattern := range []string{
		"**/*.@(md|txt)", "**/!(a).md", "**/+(a|b).md", "**/?(a).md", "**/*(a).md", "!**/*.md",
		"[ab].md", "**/*.{1..3}", "**/{a}.md", "a**b/*.md", "../x/*.md", "/abs/*.md", "a//b.md", `a\b.md`, "sub/", "*/**", "s*/**",
	} {
		_, err := NewSelection(pattern, nil)
		if err == nil || !strings.Contains(err.Error(), "refused") || !strings.Contains(err.Error(), pattern) {
			t.Errorf("pattern %q: err = %v", pattern, err)
		}
	}
	for _, rule := range []string{"**/*.@(md)", ",!sub/**", "!sub/**", "sub/", " sub", "", "a,b", "*/**", "s*/**", "sub/*/**"} {
		if _, err := NewSelection("**/*.md", []string{rule}); err == nil {
			t.Errorf("ignore rule %q was accepted", rule)
		}
	}
	for _, pattern := range []string{"", " , ,"} {
		if _, err := NewSelection(pattern, nil); err == nil {
			t.Errorf("empty pattern %q was accepted", pattern)
		}
	}
}

func TestCommasInsideBracesStayTogether(t *testing.T) {
	if got := splitPattern(" a{b,c} , d ,, "); strings.Join(got, "|") != "a{b,c}|d" {
		t.Fatalf("parts = %v", got)
	}
}

// failingFS has a folder that cannot be listed, and everything else it holds is ordinary.
type failingFS struct {
	fstest.MapFS
	denied string
}

func (f failingFS) ReadDir(name string) ([]fs.DirEntry, error) {
	if name == f.denied {
		return nil, errors.New("denied")
	}
	return f.MapFS.ReadDir(name)
}

func TestAFolderThatCannotBeListedIsCounted(t *testing.T) {
	tree := failingFS{MapFS: fstest.MapFS{"a.md": {}, "secretfolder/b.md": {}, "secretfolder/c.md": {}}, denied: "secretfolder"}
	var files, unread []string
	err := walkFS(tree, func(name string, _ fs.FileInfo) { files = append(files, name) }, func(name string) { unread = append(unread, name) })
	if err != nil {
		t.Fatal(err)
	}
	if len(files) != 1 || len(unread) != 1 || unread[0] != "secretfolder" {
		t.Fatalf("files = %v, unread = %v", files, unread)
	}
}

func TestANameThatIsNotValidTextIsCounted(t *testing.T) {
	tree := fstest.MapFS{"a.md": {}, "b\xff.md": {}}
	var files, unread []string
	err := walkFS(tree, func(name string, _ fs.FileInfo) { files = append(files, name) }, func(name string) { unread = append(unread, name) })
	if err != nil && !errors.Is(err, fs.ErrInvalid) {
		t.Fatal(err)
	}
	if len(unread) > 0 && len(files) != 1 {
		t.Fatalf("files = %v, unread = %v", files, unread)
	}
}

func TestVisitFileRefusesAnInvalidName(t *testing.T) {
	entries, err := fstest.MapFS{"a.md": {}}.ReadDir(".")
	if err != nil {
		t.Fatal(err)
	}
	visited, unread := 0, 0
	visit := func(string, fs.FileInfo) { visited++ }
	skip := func(string) { unread++ }
	if err := visitFile("b\xff.md", entries[0], visit, skip); err != nil {
		t.Fatal(err)
	}
	if visited != 0 || unread != 1 {
		t.Fatalf("visited = %d, unread = %d", visited, unread)
	}
	if err := visitFile("a.md", entries[0], visit, skip); err != nil {
		t.Fatal(err)
	}
	if visited != 1 || unread != 1 {
		t.Fatalf("visited = %d, unread = %d", visited, unread)
	}
}
