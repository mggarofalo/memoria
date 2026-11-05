package migrate

import (
	"io/fs"
	"os"
	"path"
	"path/filepath"
	"strings"
	"unicode/utf8"
)

// DefaultMaxDocumentBytes is a server's default limit on one stored document. The server
// can be set differently, so a plan only flags files past it.
const DefaultMaxDocumentBytes = 2 * 1024 * 1024

// QMD never indexes these, whatever the pattern says.
var excludedDirectories = map[string]bool{"node_modules": true, ".git": true, ".cache": true, "vendor": true, "dist": true, "build": true}

// Plan is what a collection holds, and where it would go. It holds counts and sizes, and
// reads no file's text.
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
// It selects as QMD does: the pattern and ignore rules, with no links followed, and no
// hidden file or folder.
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
	var files []string
	err = walk(root, func(name string, info fs.FileInfo) {
		if selection.Takes(name) {
			plan.add(info.Size())
			files = append(files, filepath.Join(collection.Path, filepath.FromSlash(name)))
		}
	}, func(name string) {
		// A folder that cannot be listed may hold anything, so it counts whatever its name says.
		plan.Unreadable++
	})
	if err != nil {
		plan.Problem = "its folder cannot be read"
	}
	return plan, files
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
