package sources

import (
	"os"
	"path/filepath"
	"reflect"
	"runtime"
	"strings"
	"testing"
	"time"
)

const origin = "https://memoria.example.com"

func privateConfig(t *testing.T) string {
	t.Helper()
	dir := t.TempDir()
	t.Setenv("XDG_CONFIG_HOME", dir)
	return filepath.Join(dir, "memoria")
}

func write(t *testing.T, root, name, text string) string {
	t.Helper()
	path := filepath.Join(root, filepath.FromSlash(name))
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte(text), 0o600); err != nil {
		t.Fatal(err)
	}
	return path
}

func openTree(t *testing.T, dir string) *Tree {
	t.Helper()
	tree, err := OpenTree(dir)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = tree.Close() })
	return tree
}

func TestALoadWithNoFileMeansNoSources(t *testing.T) {
	privateConfig(t)
	empty, err := Load()
	if err != nil || len(empty.Sources) != 0 {
		t.Fatalf("empty = %v, err = %v", empty, err)
	}
}

func TestConfigRoundTrip(t *testing.T) {
	privateConfig(t)
	source := Source{Name: "notes", Path: "/srv/notes", Collection: "safe"}
	config := &Config{}
	if err := config.Add(source); err != nil {
		t.Fatal(err)
	}
	if err := config.Save(); err != nil {
		t.Fatal(err)
	}
	loaded, err := Load()
	if err != nil {
		t.Fatal(err)
	}
	if found, ok := loaded.Find("notes"); !ok || found != source {
		t.Fatalf("found = %+v, %v", found, ok)
	}
}

func TestANameIsAddedOnceAndRemovedOnce(t *testing.T) {
	config := &Config{}
	source := Source{Name: "notes", Path: "/srv/notes", Collection: "safe"}
	if err := config.Add(source); err != nil {
		t.Fatal(err)
	}
	if err := config.Add(source); err == nil {
		t.Fatal("a second source with that name was accepted")
	}
	if !config.Remove("notes") || config.Remove("notes") {
		t.Fatal("remove should succeed once")
	}
}

func TestLoadRefusesWhatItCannotTrustOrRead(t *testing.T) {
	config := privateConfig(t)
	c := &Config{}
	if err := c.Save(); err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(config, configFileName)
	if err := os.WriteFile(path, []byte("{nope"), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := Load(); err == nil || !strings.Contains(err.Error(), "not valid JSON") {
		t.Fatalf("err = %v", err)
	}
	if runtime.GOOS != "windows" {
		if err := os.Chmod(path, 0o644); err != nil {
			t.Fatal(err)
		}
		if _, err := Load(); err == nil {
			t.Fatal("a file others can read was accepted")
		}
	}
}

func TestNamesAndCollections(t *testing.T) {
	for name, want := range map[string]bool{"notes": true, "a.b_c-d": true, "": false, ".hidden": false, "has space": false, strings.Repeat("a", 65): false} {
		if ValidName(name) != want {
			t.Errorf("ValidName(%q) != %v", name, want)
		}
	}
	for name, want := range map[string]bool{"notes": true, "": false, " notes": false, "a/b": false, "a\x00b": false, strings.Repeat("a", 201): false} {
		if ValidCollection(name) != want {
			t.Errorf("ValidCollection(%q) != %v", name, want)
		}
	}
}

func TestListTakesMarkdownAndLeavesOutTheRest(t *testing.T) {
	dir := t.TempDir()
	write(t, dir, "b.md", "b")
	write(t, dir, "A.MD", "a")
	write(t, dir, "sub/c.md", "c")
	write(t, dir, "notes.txt", "t")
	write(t, dir, ".hidden.md", "h")
	write(t, dir, ".git/config.md", "g")
	paths, skipped, err := openTree(t, dir).List()
	if err != nil {
		t.Fatal(err)
	}
	if want := []string{"A.MD", "b.md", "sub/c.md"}; !reflect.DeepEqual(paths, want) {
		t.Fatalf("paths = %v, want %v", paths, want)
	}
	if len(skipped) != 0 {
		t.Fatalf("skipped = %v", skipped)
	}
}

func TestLinksAreNotFollowed(t *testing.T) {
	dir, outside := t.TempDir(), t.TempDir()
	secret := write(t, outside, "secret.md", "the private key")
	if err := os.Symlink(secret, filepath.Join(dir, "link.md")); err != nil {
		t.Skipf("this host cannot make links: %v", err)
	}
	if err := os.Symlink(outside, filepath.Join(dir, "linked")); err != nil {
		t.Skipf("this host cannot make links: %v", err)
	}
	tree := openTree(t, dir)
	paths, skipped, err := tree.List()
	if err != nil {
		t.Fatal(err)
	}
	if len(paths) != 0 {
		t.Fatalf("a link was listed: %v", paths)
	}
	if len(skipped) != 1 || skipped[0].Path != "link.md" || skipped[0].Reason != errNotRegular.Error() {
		t.Fatalf("skipped = %v", skipped)
	}
	if _, err := tree.Read("link.md"); err == nil {
		t.Fatal("a link out of the folder was read")
	}
}

func TestReadRefusesWhatCannotBeSent(t *testing.T) {
	dir := t.TempDir()
	write(t, dir, "binary.md", "\xff\xfe")
	write(t, dir, "huge.md", strings.Repeat("x", MaxFileBytes+1))
	write(t, dir, "fits.md", strings.Repeat("x", MaxFileBytes))
	if err := os.Mkdir(filepath.Join(dir, "folder.md"), 0o700); err != nil {
		t.Fatal(err)
	}
	tree := openTree(t, dir)
	for name, want := range map[string]error{"binary.md": errNotText, "huge.md": errTooLarge, "folder.md": errNotRegular} {
		if _, err := tree.Read(name); err != want {
			t.Errorf("Read(%s) = %v, want %v", name, err, want)
		}
	}
	if _, err := tree.Read("fits.md"); err != nil {
		t.Errorf("a file at the limit was refused: %v", err)
	}
	if _, err := tree.Read("absent.md"); err == nil {
		t.Error("an absent file was read")
	}
	if _, err := tree.Read("../outside.md"); err == nil {
		t.Error("a path out of the folder was read")
	}
}

func TestOpenTreeRefusesAMissingFolder(t *testing.T) {
	if _, err := OpenTree(filepath.Join(t.TempDir(), "absent")); err == nil {
		t.Fatal("a missing folder was opened")
	}
}

func TestStateIsPerServerAndSurvivesASave(t *testing.T) {
	privateConfig(t)
	source := Source{Name: "notes", Collection: "safe"}
	state, exists, err := LoadState(origin, source)
	if err != nil || exists {
		t.Fatalf("exists = %v, err = %v", exists, err)
	}
	when := time.Date(2026, 1, 2, 3, 4, 5, 0, time.UTC)
	state.Record("a.md", "abc", when)
	if err := state.Save(); err != nil {
		t.Fatal(err)
	}
	loaded, exists, err := LoadState(origin, source)
	if err != nil || !exists || loaded.Files["a.md"].SHA256 != "abc" || !loaded.Files["a.md"].Updated.Equal(when) {
		t.Fatalf("loaded = %+v, exists = %v, err = %v", loaded, exists, err)
	}
	for name, other := range map[string]struct {
		origin string
		source Source
	}{
		"server":     {"https://other.example.com", source},
		"source":     {origin, Source{Name: "other", Collection: "safe"}},
		"collection": {origin, Source{Name: "notes", Collection: "other"}},
	} {
		if _, exists, _ := LoadState(other.origin, other.source); exists {
			t.Errorf("another %s saw this cursor", name)
		}
	}
}

func TestADamagedCursorIsAnErrorAndNotAFirstRun(t *testing.T) {
	privateConfig(t)
	source := Source{Name: "notes", Collection: "safe"}
	state, _, err := LoadState(origin, source)
	if err != nil {
		t.Fatal(err)
	}
	if err := state.Save(); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(state.path, []byte("{nope"), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, _, err := LoadState(origin, source); err == nil || !strings.Contains(err.Error(), "damaged") {
		t.Fatalf("err = %v", err)
	}
}

func TestACursorWithNoFilesLoadsAsEmpty(t *testing.T) {
	privateConfig(t)
	source := Source{Name: "notes", Collection: "safe"}
	state, _, _ := LoadState(origin, source)
	if err := state.Save(); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(state.path, []byte("{}"), 0o600); err != nil {
		t.Fatal(err)
	}
	loaded, exists, err := LoadState(origin, source)
	if err != nil || !exists || loaded.Files == nil {
		t.Fatalf("loaded = %+v, exists = %v, err = %v", loaded, exists, err)
	}
}

func TestPlanComparesContentAndNeverDeletes(t *testing.T) {
	privateConfig(t)
	dir := t.TempDir()
	unchanged := write(t, dir, "same.md", "same")
	write(t, dir, "edited.md", "new text")
	write(t, dir, "fresh.md", "fresh")
	write(t, dir, "binary.md", "\xff")
	tree := openTree(t, dir)
	state, _, _ := LoadState(origin, Source{Name: "notes", Collection: "safe"})
	same, err := tree.Read("same.md")
	if err != nil {
		t.Fatal(err)
	}
	state.Record("same.md", same.SHA256, same.ModTime)
	state.Record("edited.md", "old hash", same.ModTime)
	state.Record("gone.md", "any", same.ModTime)

	// Touching a file is not a change.
	if err := os.Chtimes(unchanged, time.Now().Add(time.Hour), time.Now().Add(time.Hour)); err != nil {
		t.Fatal(err)
	}
	paths, _, err := tree.List()
	if err != nil {
		t.Fatal(err)
	}
	plan := state.Plan(tree, paths)
	want := []Change{{Path: "edited.md", Kind: KindChanged}, {Path: "fresh.md", Kind: KindNew}}
	if !reflect.DeepEqual(plan.Changes, want) {
		t.Fatalf("changes = %v, want %v", plan.Changes, want)
	}
	if plan.Unchanged != 1 || plan.Missing != 1 {
		t.Fatalf("plan = %+v", plan)
	}
	if len(plan.Skipped) != 1 || plan.Skipped[0].Path != "binary.md" || plan.Skipped[0].Reason != errNotText.Error() {
		t.Fatalf("skipped = %v", plan.Skipped)
	}
}

func TestSkipReasonDoesNotRepeatAnUnknownError(t *testing.T) {
	if got := skipReason(os.ErrPermission); got != "it could not be read" {
		t.Fatalf("reason = %q", got)
	}
}

func TestVersionMovesForwardForAChangeWithAnOldTime(t *testing.T) {
	privateConfig(t)
	state, _, _ := LoadState(origin, Source{Name: "notes", Collection: "safe"})
	sent := time.Date(2026, 3, 1, 12, 0, 0, 0, time.UTC)
	if got := state.Version("new.md", sent.Add(123456*time.Nanosecond)); !got.Equal(sent) {
		t.Fatalf("a new file's version = %v", got)
	}
	state.Record("a.md", "abc", sent)
	if got := state.Version("a.md", sent.Add(time.Hour)); !got.Equal(sent.Add(time.Hour)) {
		t.Fatalf("a later edit's version = %v", got)
	}
	for name, modTime := range map[string]time.Time{"older": sent.Add(-time.Hour), "same": sent} {
		if got := state.Version("a.md", modTime); !got.Equal(sent.Add(time.Second)) {
			t.Fatalf("%s time gives version %v", name, got)
		}
	}
}
