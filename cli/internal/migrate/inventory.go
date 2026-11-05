package migrate

import (
	"io"
	"io/fs"
	"os"
	"path"
	"path/filepath"
	"strings"
	"time"
	"unicode/utf8"
)

// DefaultMaxDocumentBytes is a server's default limit on one stored document. The server
// can be set differently, so a plan only flags files past it.
const DefaultMaxDocumentBytes = 2 * 1024 * 1024

const (
	maxCheckedBytes = 64 * 1024 * 1024
	// A server refuses a version more than this ahead of its clock.
	maxFutureSkew = time.Hour
)

// QMD never indexes these, whatever the pattern says.
var excludedDirectories = map[string]bool{"node_modules": true, ".git": true, ".cache": true, "vendor": true, "dist": true, "build": true}

// Plan is what a collection holds, and where it would go. It holds counts and sizes, and
// no text. BlankNames is for a push's private report, and never reaches any output.
type Plan struct {
	Name       string `json:"name"`
	Target     string `json:"target"`
	Path       string `json:"path"`
	Pattern    string `json:"pattern"`
	Files      int    `json:"files"`
	Bytes      int64  `json:"bytes"`
	Largest    int64  `json:"largestBytes"`
	OverLimit  int    `json:"overDefaultLimit"`
	Unreadable int    `json:"unreadable"`
	// Unstorable is the selected files a server would always refuse at their path, by reason.
	Unstorable map[string]int `json:"unstorable,omitempty"`
	// Blank is the files the pattern takes that hold nothing but space. QMD does not index them, so they are not selected.
	Blank      int      `json:"blank,omitempty"`
	BlankNames []string `json:"-"`
	// Decomposed is the selected files stored under a composed spelling of their name, as a server refuses any other.
	Decomposed int    `json:"decomposedNames,omitempty"`
	Problem    string `json:"problem,omitempty"`
}

// RawSessions is a folder of raw transcripts that could be rendered and sent.
type RawSessions struct {
	Path       string `json:"path"`
	Target     string `json:"target"`
	Files      int    `json:"files"`
	Bytes      int64  `json:"bytes"`
	Unreadable int    `json:"unreadable"`
	Problem    string `json:"problem,omitempty"`
}

// Inventory counts what QMD would index in a collection, and returns those files' paths.
// It selects as QMD does: the pattern and ignore rules, with no links followed, no hidden
// file or folder, and no blank file.
func Inventory(collection Collection, target string) (Plan, []string) {
	plan := Plan{Name: collection.Name, Target: target, Path: collection.Path, Pattern: collection.Pattern}
	if !filepath.IsAbs(collection.Path) {
		plan.Problem = "its path is not absolute"
		return plan, nil
	}
	root, err := os.OpenRoot(collection.Path)
	if err != nil {
		plan.Problem = "its folder cannot be opened"
		return plan, nil
	}
	defer func() { _ = root.Close() }()

	selection, err := NewSelection(collection.Pattern, collection.Ignore)
	if err != nil {
		plan.Problem = err.Error()
		return plan, nil
	}
	var files, names []string
	local := map[string]string{}
	err = walk(root, func(name string, info fs.FileInfo) {
		if selection.Takes(name) && plan.keep(root, name, info, local) {
			plan.add(info.Size())
			names = append(names, name)
			files = append(files, filepath.Join(collection.Path, filepath.FromSlash(name)))
		}
	}, func(name string) {
		// A folder that cannot be listed may hold anything, so it counts whatever its name says.
		plan.Unreadable++
	})
	if err != nil {
		plan.Problem = "its folder cannot be read"
	}
	plan.noteNames(names, local)
	return plan, files
}

// keep looks at what only this machine can see. It reads the file to judge it, and keeps none of it.
func (p *Plan) keep(root *os.Root, name string, info fs.FileInfo, local map[string]string) bool {
	switch textOf(root, name, info.Size()) {
	case textBlank:
		p.Blank++
		p.BlankNames = append(p.BlankNames, name)
		return false
	case textUnreadable:
		p.Unreadable++
		return false
	case textInvalid:
		local[name] = ReasonNotUTF8
	}
	if info.ModTime().After(time.Now().Add(maxFutureSkew)) {
		local[name] = ReasonFutureTime
	}
	return true
}

func (p *Plan) noteNames(names []string, local map[string]string) {
	p.Unstorable = Unstorable(names)
	for name, reason := range local {
		if PathProblem(name) == "" {
			p.Unstorable[reason]++
		}
	}
	for _, name := range names {
		if ServerPath(name) != name {
			p.Decomposed++
		}
	}
}

type textKind int

const (
	textFine textKind = iota
	textBlank
	textInvalid
	textUnreadable
)

// textOf judges a file's text. A file past any server's cap is not read, as it will be refused for its size anyway.
func textOf(root *os.Root, name string, size int64) textKind {
	if size > maxCheckedBytes {
		return textFine
	}
	file, err := root.Open(filepath.FromSlash(name))
	if err != nil {
		return textUnreadable
	}
	defer func() { _ = file.Close() }()
	data, err := io.ReadAll(file)
	switch {
	case err != nil:
		return textUnreadable
	case !utf8.Valid(data):
		return textInvalid
	case strings.TrimFunc(string(data), isJSSpace) == "":
		return textBlank
	}
	return textFine
}

func (p *Plan) add(size int64) {
	p.Files++
	p.Bytes += size
	p.Largest = max(p.Largest, size)
	if size > DefaultMaxDocumentBytes {
		p.OverLimit++
	}
}

// walk visits the regular files QMD could see. A link is neither followed nor visited.
func walk(root *os.Root, visit func(string, fs.FileInfo), unreadable func(string)) error {
	return walkFS(root.FS(), visit, unreadable)
}

func walkFS(tree fs.FS, visit func(string, fs.FileInfo), unreadable func(string)) error {
	return fs.WalkDir(tree, ".", func(name string, entry fs.DirEntry, err error) error {
		if name == "." {
			return err
		}
		if err != nil {
			unreadable(name)
			return skipIfDirectory(entry)
		}
		if isExcluded(name, entry) {
			return skipIfDirectory(entry)
		}
		if !entry.Type().IsRegular() {
			return nil
		}
		return visitFile(name, entry, visit, unreadable)
	})
}

// visitFile counts a name that is not valid text as unreadable: it cannot go in a list for the tool, or on the wire.
func visitFile(name string, entry fs.DirEntry, visit func(string, fs.FileInfo), unreadable func(string)) error {
	info, err := entry.Info()
	if err != nil || !utf8.ValidString(name) {
		unreadable(name)
		return nil
	}
	visit(name, info)
	return nil
}

func isExcluded(name string, entry fs.DirEntry) bool {
	base := path.Base(name)
	return strings.HasPrefix(base, ".") || (entry.IsDir() && excludedDirectories[base])
}

func skipIfDirectory(entry fs.DirEntry) error {
	if entry != nil && entry.IsDir() {
		return fs.SkipDir
	}
	return nil
}

// InventoryRaw counts the raw Codex transcripts in a folder, in any subfolder.
func InventoryRaw(dir, target string) RawSessions {
	raw := RawSessions{Path: dir, Target: target}
	root, err := os.OpenRoot(dir)
	if err != nil {
		raw.Problem = "its folder cannot be opened"
		return raw
	}
	defer func() { _ = root.Close() }()
	err = walk(root, func(name string, info fs.FileInfo) {
		if strings.EqualFold(path.Ext(name), ".jsonl") {
			raw.Files++
			raw.Bytes += info.Size()
		}
	}, func(string) { raw.Unreadable++ })
	if err != nil {
		raw.Problem = "its folder cannot be read"
	}
	return raw
}
