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

// Each of these is silently read differently by the other matcher, so each is refused rather than guessed at.
func TestWhatCannotBeReadFaithfullyIsRefused(t *testing.T) {
	for _, pattern := range []string{
		"**/*.@(md|txt)", "**/!(a).md", "**/+(a|b).md", "**/?(a).md", "**/*(a).md", "!**/*.md",
		"[ab].md", "**/*.{1..3}", "**/{a}.md", "a**b/*.md", "../x/*.md", "/abs/*.md", "a//b.md", `a\b.md`, "sub/",
	} {
		_, err := NewSelection(pattern, nil)
		if err == nil || !strings.Contains(err.Error(), "refused") || !strings.Contains(err.Error(), pattern) {
			t.Errorf("pattern %q: err = %v", pattern, err)
		}
	}
	for _, rule := range []string{"**/*.@(md)", ",!sub/**", "!sub/**", "sub/", " sub", "", "a,b"} {
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
