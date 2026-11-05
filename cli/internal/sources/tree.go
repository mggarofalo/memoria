package sources

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"io"
	"io/fs"
	"os"
	"path"
	"path/filepath"
	"strings"
	"time"
	"unicode/utf8"
)

// MaxFileBytes bounds what one file can hold in memory. The server's own limit is
// lower by default and is the one that decides; this only stops a huge file being read.
const MaxFileBytes = 16 * 1024 * 1024

var (
	errTooLarge   = errors.New("it is larger than this client will read")
	errNotText    = errors.New("it is not UTF-8 text")
	errNotRegular = errors.New("it is not a regular file")
)

// Tree is a source's folder. Reading goes through os.Root, so a link swapped in
// after the listing still cannot reach anything outside it.
type Tree struct {
	root *os.Root
}

// Skipped is a file left out, and why.
type Skipped struct {
	Path   string `json:"path"`
	Reason string `json:"reason"`
}

// Content is one read of a file. Body and SHA256 always describe the same bytes.
type Content struct {
	Body    string
	SHA256  string
	ModTime time.Time
}

// OpenTree opens a source's folder.
func OpenTree(dir string) (*Tree, error) {
	root, err := os.OpenRoot(dir)
	if err != nil {
		return nil, err
	}
	return &Tree{root: root}, nil
}

// Close releases the folder.
func (t *Tree) Close() error {
	return t.root.Close()
}

// List returns the Markdown files in the folder, in order. A link is never followed,
// since one in a notes folder could point at a private key. Hidden files and folders
// are left out, as QMD leaves them out and they are things like .git.
func (t *Tree) List() ([]string, []Skipped, error) {
	var (
		paths   []string
		skipped []Skipped
	)
	err := fs.WalkDir(t.root.FS(), ".", func(name string, entry fs.DirEntry, err error) error {
		if name == "." {
			return err
		}
		if err != nil {
			skipped = append(skipped, Skipped{Path: name, Reason: "it could not be read"})
			return skipUnreadable(entry)
		}
		if strings.HasPrefix(path.Base(name), ".") {
			return skipHidden(entry)
		}
		if !strings.EqualFold(path.Ext(name), ".md") {
			return nil
		}
		if !entry.Type().IsRegular() {
			skipped = append(skipped, Skipped{Path: name, Reason: errNotRegular.Error()})
			return nil
		}
		paths = append(paths, name)
		return nil
	})
	return paths, skipped, err
}

func skipUnreadable(entry fs.DirEntry) error {
	if entry != nil && entry.IsDir() {
		return fs.SkipDir
	}
	return nil
}

func skipHidden(entry fs.DirEntry) error {
	if entry.IsDir() {
		return fs.SkipDir
	}
	return nil
}

// Read returns a file's text. The file is read once, so what is hashed is what is sent.
func (t *Tree) Read(name string) (Content, error) {
	file, err := t.root.Open(filepath.FromSlash(name))
	if err != nil {
		return Content{}, err
	}
	defer func() { _ = file.Close() }()
	info, err := file.Stat()
	if err != nil {
		return Content{}, err
	}
	if !info.Mode().IsRegular() {
		return Content{}, errNotRegular
	}
	data, err := io.ReadAll(io.LimitReader(file, MaxFileBytes+1))
	if err != nil {
		return Content{}, err
	}
	if len(data) > MaxFileBytes {
		return Content{}, errTooLarge
	}
	if !utf8.Valid(data) {
		return Content{}, errNotText
	}
	sum := sha256.Sum256(data)
	return Content{Body: string(data), SHA256: hex.EncodeToString(sum[:]), ModTime: info.ModTime()}, nil
}
