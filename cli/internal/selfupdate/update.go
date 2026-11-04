package selfupdate

import (
	"context"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"runtime"
	"slices"
	"strings"
	"time"

	lib "github.com/creativeprojects/go-selfupdate"
)

const verifyTimeout = 30 * time.Second

// ErrNoRelease means no release has a build for this platform.
var ErrNoRelease = errors.New("no matching release")

// assetPattern matches an archive under the release contract.
var assetPattern = regexp.MustCompile(`^memoria_(.+)_(windows|linux|darwin)_(amd64|arm64)\.(tar\.gz|zip)$`)

func isContractAsset(name, version string) bool {
	if name == checksumsAsset {
		return true
	}
	match := assetPattern.FindStringSubmatch(name)
	if match == nil || match[1] != version {
		return false
	}
	// Windows ships zip; everything else ships tar.gz.
	return (match[2] == "windows") == (match[4] == "zip")
}

// Options configures an Updater.
type Options struct {
	// OS and Arch override the running platform, for tests.
	OS, Arch string
	// Prerelease lets the search for the newest release consider prereleases.
	Prerelease bool
	// Verify runs the installed binary. Nil runs "<exe> version".
	Verify func(ctx context.Context, exe, expected string) error
}

// Candidate is a release chosen for this platform.
type Candidate struct {
	Tag        string
	Version    string
	Asset      string
	Prerelease bool
	release    *lib.Release
}

// Updater finds and installs releases.
type Updater struct {
	source *source
	opts   Options
}

// New returns an Updater for the real GitHub API.
func New(opts Options) *Updater { return newUpdater(newSource(), opts) }

func newUpdater(src *source, opts Options) *Updater {
	if opts.Verify == nil {
		opts.Verify = RunVersion
	}
	return &Updater{source: src, opts: opts}
}

func (u *Updater) library(oldSavePath string) (*lib.Updater, error) {
	return lib.NewUpdater(lib.Config{
		Source:      u.source,
		Validator:   &lib.ChecksumValidator{UniqueFilename: checksumsAsset},
		OS:          u.opts.OS,
		Arch:        u.opts.Arch,
		Prerelease:  u.opts.Prerelease,
		OldSavePath: oldSavePath,
	})
}

// Find returns the newest release, or the one tagged tag when tag is not empty.
// A release with no build for this platform, or no checksums.txt, is an error.
func (u *Updater) Find(ctx context.Context, tag string) (*Candidate, error) {
	library, err := u.library("")
	if err != nil {
		return nil, fmt.Errorf("creating the updater: %w", err)
	}
	slug := lib.ParseSlug(repoSlug)
	rel, found, err := library.DetectVersion(ctx, slug, tag)
	if err != nil {
		return nil, err
	}
	if !found {
		return nil, fmt.Errorf("%w: %s for %s/%s", ErrNoRelease, describeTag(tag), u.platformOS(), u.platformArch())
	}
	return &Candidate{
		Tag:        "v" + rel.Version(),
		Version:    rel.Version(),
		Asset:      rel.AssetName,
		Prerelease: rel.Prerelease,
		release:    rel,
	}, nil
}

func describeTag(tag string) string {
	if tag == "" {
		return "latest release"
	}
	return tag
}

func (u *Updater) platformOS() string {
	if u.opts.OS != "" {
		return u.opts.OS
	}
	return runtime.GOOS
}

func (u *Updater) platformArch() string {
	if u.opts.Arch != "" {
		return u.opts.Arch
	}
	return runtime.GOARCH
}

// Install replaces exe with the candidate. In order: download, verify the
// checksum, unpack, swap the files, run the new binary, and on failure swap back.
// Nothing on disk changes until the checksum and the archive are both good.
//
// The running binary's file can be renamed on every supported platform, Windows
// included, which only forbids overwriting or deleting it. So the swap is two
// renames in the same directory. The backup of a running Windows binary cannot
// be deleted until the process exits, so it is hidden and removed next time.
func (u *Updater) Install(ctx context.Context, candidate *Candidate, exe string) error {
	resolved, err := filepath.EvalSymlinks(exe)
	if err != nil {
		return fmt.Errorf("locating the installed binary: %w", err)
	}
	backup := backupPath(resolved)
	removeStale(backup)

	library, err := u.library(backup)
	if err != nil {
		return fmt.Errorf("creating the updater: %w", err)
	}
	if err := library.UpdateTo(ctx, candidate.release, resolved); err != nil {
		return fmt.Errorf("installing %s: %w", candidate.Tag, err)
	}

	if err := u.opts.Verify(ctx, resolved, candidate.Version); err != nil {
		return rollback(resolved, backup, err)
	}
	removeBackup(backup)
	return nil
}

func backupPath(exe string) string {
	return filepath.Join(filepath.Dir(exe), "."+filepath.Base(exe)+".old")
}

func removeStale(backup string) {
	_ = os.Remove(backup)
}

// removeBackup deletes the backup, or hides it if this running process holds it.
func removeBackup(backup string) {
	if os.Remove(backup) != nil {
		hideFile(backup)
	}
}

// rollback restores the backup after the new binary failed to run.
func rollback(exe, backup string, cause error) error {
	if err := restore(exe, backup); err != nil {
		return fmt.Errorf("the new binary failed (%v) and rollback failed (%w); the previous binary is at %s", cause, err, backup)
	}
	return fmt.Errorf("the new binary failed, so the previous version was restored: %w", cause)
}

func restore(exe, backup string) error {
	failed := exe + ".failed"
	if err := os.Rename(exe, failed); err != nil {
		return err
	}
	if err := os.Rename(backup, exe); err != nil {
		return errors.Join(err, os.Rename(failed, exe))
	}
	_ = os.Remove(failed)
	return nil
}

// RunVersion runs "<exe> version" and checks that it reports expected. The API
// key is removed from the child's environment; version has no use for it.
func RunVersion(ctx context.Context, exe, expected string) error {
	ctx, cancel := context.WithTimeout(ctx, verifyTimeout)
	defer cancel()
	cmd := exec.CommandContext(ctx, exe, "version")
	cmd.Env = environWithout(os.Environ(), "MEMORIA_API_KEY")
	out, err := cmd.Output()
	if err != nil {
		return fmt.Errorf("running version: %w", err)
	}
	if !reportsVersion(string(out), expected) {
		return fmt.Errorf("version reported something other than %s", expected)
	}
	return nil
}

// reportsVersion looks for the version as a word, with or without a leading v,
// since either form may be what the build stamped.
func reportsVersion(output, expected string) bool {
	return slices.ContainsFunc(strings.Fields(output), func(word string) bool {
		return strings.TrimPrefix(word, "v") == expected
	})
}

func environWithout(environ []string, name string) []string {
	return slices.DeleteFunc(slices.Clone(environ), func(entry string) bool {
		return strings.HasPrefix(strings.ToUpper(entry), name+"=")
	})
}
