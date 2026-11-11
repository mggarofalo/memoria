package selfupdate

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"slices"
	"strings"
	"time"
)

const (
	verifyTimeout   = 30 * time.Second
	verifyWaitDelay = 3 * time.Second
)

var (
	// ErrNoRelease means no release has a build for this platform.
	ErrNoRelease = errors.New("no matching release")
	// ErrChecksum means an archive does not match checksums.txt.
	ErrChecksum = errors.New("checksum verification failed")
	// ErrManaged means a package manager, or another user, owns the binary.
	ErrManaged = errors.New("this binary should not be replaced by self-update")
)

// versionEnvAllowlist is all the post-install check passes to the new binary. It
// holds nothing secret: no API key, no GITHUB_TOKEN, no cloud credentials.
var versionEnvAllowlist = []string{"PATH", "SYSTEMROOT", "TMP", "TEMP", "TMPDIR"}

// Options configures an Updater.
type Options struct {
	// OS and Arch override the running platform, for tests.
	OS, Arch string
	// Prerelease lets the search for the newest release consider prereleases.
	Prerelease bool
	// Verify runs the installed binary. Nil runs "<exe> version".
	Verify func(ctx context.Context, exe, expected string) error
	// Attester checks provenance. Nil uses GitHub attestations through Sigstore.
	Attester Attester
	// ChecksumOnly skips provenance and trusts checksums.txt alone.
	ChecksumOnly bool
	// Force replaces a binary that a package manager appears to own.
	Force bool
}

// Candidate is a release chosen for this platform.
type Candidate struct {
	Tag          string
	Version      string
	Asset        string
	Prerelease   bool
	assetURL     string
	checksumsURL string
}

// Updater finds and installs releases.
type Updater struct {
	source *source
	opts   Options
}

// New returns an Updater for the real GitHub API.
func New(opts Options) *Updater { return newUpdater(newSource(), opts) }

func newUpdater(src *source, opts Options) *Updater {
	if opts.OS == "" {
		opts.OS = runtime.GOOS
	}
	if opts.Arch == "" {
		opts.Arch = runtime.GOARCH
	}
	if opts.Verify == nil {
		opts.Verify = RunVersion
	}
	if opts.Attester == nil {
		opts.Attester = newSigstoreAttester(src)
	}
	return &Updater{source: src, opts: opts}
}

// Find returns the newest release, or the one tagged tag when tag is not empty.
// A release with no build for this platform, or no checksums.txt, is an error.
func (u *Updater) Find(ctx context.Context, tag string) (*Candidate, error) {
	releases, err := u.source.list(ctx)
	if err != nil {
		return nil, err
	}
	best := u.choose(releases, tag)
	if best == nil {
		return nil, fmt.Errorf("%w: %s for %s/%s", ErrNoRelease, describeTag(tag), u.opts.OS, u.opts.Arch)
	}
	if best.checksumsURL == "" {
		return nil, fmt.Errorf("release %s has no %s, so it cannot be verified", best.Tag, checksumsAsset)
	}
	return best, nil
}

func describeTag(tag string) string {
	if tag == "" {
		return "latest release"
	}
	return tag
}

func (u *Updater) choose(releases []apiRelease, tag string) *Candidate {
	var best *Candidate
	for i := range releases {
		candidate := u.candidateFrom(&releases[i], tag)
		if candidate != nil && newerThan(candidate, best) {
			best = candidate
		}
	}
	return best
}

// candidateFrom returns the release as a candidate if it is eligible. A pinned tag
// matches itself, and Decide then rules on it. Otherwise drafts and prereleases
// are out, whether GitHub flags them or only the tag says so: an unflagged
// v9.9.9-evil must not outrank the stable releases everyone should update to.
func (u *Updater) candidateFrom(rel *apiRelease, tag string) *Candidate {
	if rel.Draft || !ValidTag(rel.TagName) {
		return nil
	}
	if tag != "" && rel.TagName != tag {
		return nil
	}
	prerelease := rel.Prerelease || hasPrereleaseTag(rel.TagName)
	if tag == "" && prerelease && !u.opts.Prerelease {
		return nil
	}
	version := strings.TrimPrefix(rel.TagName, "v")
	asset := assetName(version, u.opts.OS, u.opts.Arch)
	assetURL := rel.assetURL(asset)
	if assetURL == "" {
		return nil
	}
	return &Candidate{
		Tag:          rel.TagName,
		Version:      version,
		Asset:        asset,
		Prerelease:   prerelease,
		assetURL:     assetURL,
		checksumsURL: rel.assetURL(checksumsAsset),
	}
}

func assetName(version, goos, arch string) string {
	extension := "tar.gz"
	if goos == "windows" {
		extension = "zip"
	}
	return fmt.Sprintf("memoria_%s_%s_%s.%s", version, goos, arch, extension)
}

// Install replaces exe with the candidate. In order: download, verify the
// checksum, verify provenance, unpack to a private temp file, swap, run the new
// binary, and on failure swap back. Nothing at exe changes until every check has
// passed. Running the new binary proves only that it works: trust comes from the
// checks before it.
func (u *Updater) Install(ctx context.Context, candidate *Candidate, exe string) error {
	resolved, err := filepath.EvalSymlinks(exe)
	if err != nil {
		return fmt.Errorf("locating the installed binary: %w", err)
	}
	if err := u.checkReplaceable(resolved); err != nil {
		return err
	}
	archive, err := u.verifiedArchive(ctx, candidate)
	if err != nil {
		return err
	}
	staged, err := stageBinary(archive, u.opts.OS, resolved)
	if err != nil {
		return err
	}
	defer func() { _ = os.Remove(staged) }()

	backup := backupPath(resolved)
	if err := swapIn(staged, resolved, backup); err != nil {
		return fmt.Errorf("installing %s: %w", candidate.Tag, err)
	}
	if err := u.opts.Verify(ctx, resolved, candidate.Version); err != nil {
		return rollback(resolved, backup, err)
	}
	removeBackup(backup)
	return nil
}

func (u *Updater) checkReplaceable(exe string) error {
	if u.opts.Force {
		return nil
	}
	if managedPath(exe) {
		return fmt.Errorf("%w: %s looks package-managed, so update it with that package manager, or pass --force", ErrManaged, exe)
	}
	if err := checkOwnedByCurrentUser(exe); err != nil {
		return fmt.Errorf("%w: %w; pass --force to replace it anyway", ErrManaged, err)
	}
	return nil
}

// Paths that mean a package manager installed the binary, and would be left
// confused by replacing it. Unix system paths are anchored, since /usr/local/bin
// also contains /bin/. Windows ones can sit under any profile or drive.
var (
	managedPrefixes = []string{
		"/usr/bin/", "/bin/", "/usr/sbin/", "/sbin/", "/nix/store/", "/snap/", "/opt/homebrew/",
		"/usr/local/Cellar/", "/home/linuxbrew/",
	}
	// Compared in lower case: Windows paths are not case-sensitive.
	managedParts = []string{
		"/winget/packages/", "/scoop/apps/", "/program files/", "/program files (x86)/", "/chocolatey/",
		"/.linuxbrew/", "/.asdf/", "/.local/share/mise/", "/.mise/",
	}
)

func managedPath(path string) bool {
	normalized := strings.ReplaceAll(path, `\`, "/")
	lower := strings.ToLower(normalized)
	return slices.ContainsFunc(managedPrefixes, func(prefix string) bool {
		return strings.HasPrefix(normalized, prefix)
	}) || slices.ContainsFunc(managedParts, func(part string) bool {
		return strings.Contains(lower, part)
	})
}

// verifiedArchive downloads the archive and returns it once both checks pass.
func (u *Updater) verifiedArchive(ctx context.Context, candidate *Candidate) ([]byte, error) {
	archive, err := u.source.fetchAll(ctx, candidate.assetURL, maxArchiveBytes)
	if err != nil {
		return nil, fmt.Errorf("downloading %s: %w", candidate.Asset, err)
	}
	checksums, err := u.source.fetchAll(ctx, candidate.checksumsURL, maxListBytes)
	if err != nil {
		return nil, fmt.Errorf("downloading %s: %w", checksumsAsset, err)
	}
	digest := sha256.Sum256(archive)
	if err := verifyChecksum(checksums, candidate.Asset, digest[:]); err != nil {
		return nil, err
	}
	if !u.opts.ChecksumOnly {
		if err := u.opts.Attester.Verify(ctx, Subject{Digest: digest[:], Tag: candidate.Tag, Asset: candidate.Asset}); err != nil {
			return nil, err
		}
	}
	return archive, nil
}

// verifyChecksum looks up name in sha256sum format, by exact name. A malformed
// line cannot match, so it cannot vouch for anything.
func verifyChecksum(checksums []byte, name string, digest []byte) error {
	for _, line := range strings.Split(string(checksums), "\n") {
		fields := strings.Fields(line)
		if len(fields) != 2 || strings.TrimPrefix(fields[1], "*") != name {
			continue
		}
		if !strings.EqualFold(fields[0], hex.EncodeToString(digest)) {
			return fmt.Errorf("%w: %s does not match %s", ErrChecksum, name, checksumsAsset)
		}
		return nil
	}
	return fmt.Errorf("%w: %s is not listed in %s", ErrChecksum, name, checksumsAsset)
}

// stageBinary unpacks to a new, randomly named file beside the target. CreateTemp
// uses O_EXCL, so a planted file or symlink at a predictable name cannot redirect
// the write. It takes the mode of the binary it will replace.
func stageBinary(archive []byte, goos, target string) (path string, err error) {
	info, err := os.Stat(target)
	if err != nil {
		return "", fmt.Errorf("locating the installed binary: %w", err)
	}
	file, err := os.CreateTemp(filepath.Dir(target), "."+filepath.Base(target)+".new-*")
	if err != nil {
		return "", fmt.Errorf("creating a temporary file: %w", err)
	}
	defer func() {
		_ = file.Close()
		if err != nil {
			_ = os.Remove(file.Name())
		}
	}()
	if err = extractBinary(archive, goos, file); err != nil {
		return "", err
	}
	if err = file.Sync(); err != nil {
		return "", err
	}
	if err = file.Chmod(info.Mode().Perm()); err != nil && runtime.GOOS != "windows" {
		return "", err
	}
	return file.Name(), nil
}

func backupPath(exe string) string {
	return filepath.Join(filepath.Dir(exe), "."+filepath.Base(exe)+".old")
}

// swapIn puts staged at exe and leaves the old binary at backup. On Unix a hard
// link keeps the old file and a rename replaces exe atomically, so there is no
// moment with no binary. Windows forbids overwriting a running exe but allows
// renaming it, so there it moves aside first, and a failure moves it back.
func swapIn(staged, exe, backup string) error {
	_ = os.Remove(backup) // A leftover from an update Windows could not clean up.
	if runtime.GOOS != "windows" && os.Link(exe, backup) == nil {
		return os.Rename(staged, exe)
	}
	if err := os.Rename(exe, backup); err != nil {
		return err
	}
	if err := os.Rename(staged, exe); err != nil {
		return errors.Join(err, os.Rename(backup, exe))
	}
	return nil
}

// removeBackup deletes the backup, or hides it if this running process holds it.
func removeBackup(backup string) {
	if os.Remove(backup) != nil {
		hideFile(backup)
	}
}

// rollback restores the backup after the new binary failed to run.
func rollback(exe, backup string, cause error) error {
	if err := os.Rename(backup, exe); err != nil {
		return fmt.Errorf("the new binary failed (%w) and rollback failed (%w); the previous binary is at %s", cause, err, backup)
	}
	return fmt.Errorf("the new binary failed, so the previous version was restored: %w", cause)
}

// RunVersion runs "<exe> version" and checks that it reports expected. It runs
// with a scrubbed environment and proves only that the binary works.
func RunVersion(ctx context.Context, exe, expected string) error {
	ctx, cancel := context.WithTimeout(ctx, verifyTimeout)
	defer cancel()
	cmd := exec.CommandContext(ctx, exe, "version")
	cmd.Env = scrubbedEnviron(os.Environ())
	// A binary that leaves a child holding stdout must not block the rollback.
	cmd.WaitDelay = verifyWaitDelay
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

func scrubbedEnviron(environ []string) []string {
	return slices.DeleteFunc(slices.Clone(environ), func(entry string) bool {
		name, _, _ := strings.Cut(entry, "=")
		return !slices.Contains(versionEnvAllowlist, strings.ToUpper(name))
	})
}
