// Package selfupdate replaces the running binary with a GitHub release of this
// repository. Versions come from tags vX.Y.Z; assets are
// memoria_<version>_<os>_<arch>.tar.gz (.zip on Windows) with a checksums.txt on
// the same release.
//
// The checksum comes from the same release as the archive, so alone it only
// catches corruption. Each release also has a build provenance attestation, which
// is verified with Sigstore against this repository's release workflow identity.
// Both are checked before anything on disk changes.
package selfupdate

import (
	"fmt"
	"regexp"
	"strings"

	"github.com/Masterminds/semver/v3"
)

// DevVersion is the version of an unstamped build.
const DevVersion = "dev"

// tagPattern is the shape of a release tag, and of a pinned --version.
var tagPattern = regexp.MustCompile(`^v\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$`)

// ValidTag reports whether tag has the form vX.Y.Z or vX.Y.Z-prerelease.
func ValidTag(tag string) bool { return tagPattern.MatchString(tag) }

// Policy holds what the user has explicitly allowed.
type Policy struct {
	// Force allows updating a dev build, or one with an unreadable version.
	Force bool
	// AllowDowngrade allows moving to an older version.
	AllowDowngrade bool
	// AllowPrerelease allows moving to a prerelease.
	AllowPrerelease bool
	// Pinned is true when the user named the target version.
	Pinned bool
}

// Action is what to do about a candidate release.
type Action int

const (
	// ActionUpdate means install the candidate.
	ActionUpdate Action = iota
	// ActionUpToDate means the current version is already right.
	ActionUpToDate
	// ActionRefuse means the policy forbids it; Decision.Reason says why.
	ActionRefuse
)

// Decision is the outcome of Decide.
type Decision struct {
	Action Action
	Reason string
}

func refuse(format string, args ...any) Decision {
	return Decision{Action: ActionRefuse, Reason: fmt.Sprintf(format, args...)}
}

// Decide compares the running version with a candidate. It is pure, so the rules
// can be tested exhaustively without a network.
func Decide(current, candidate string, policy Policy) Decision {
	target, err := parseVersion(candidate)
	if err != nil {
		return refuse("the release version %q is not a semantic version", candidate)
	}
	if target.Prerelease() != "" && !policy.AllowPrerelease {
		return refuse("%s is a prerelease; pass --prerelease to install it", target.Original())
	}
	running, err := parseVersion(current)
	if err != nil {
		return decideUnversioned(current, policy)
	}
	return compare(running, target, policy)
}

// DecideCandidate applies Decide to a found release, also honoring a release
// marked prerelease on GitHub whatever its tag says.
func DecideCandidate(current string, candidate *Candidate, policy Policy) Decision {
	if candidate.Prerelease && !policy.AllowPrerelease {
		return refuse("%s is a prerelease; pass --prerelease to install it", candidate.Tag)
	}
	return Decide(current, candidate.Tag, policy)
}

func decideUnversioned(current string, policy Policy) Decision {
	if policy.Force {
		return Decision{Action: ActionUpdate}
	}
	return refuse("this is a development build (version %q), which should be rebuilt rather than updated; pass --force to replace it with a release anyway", current)
}

func compare(running, target *semver.Version, policy Policy) Decision {
	switch {
	case target.Equal(running):
		return Decision{Action: ActionUpToDate}
	case target.GreaterThan(running):
		return Decision{Action: ActionUpdate}
	case policy.AllowDowngrade:
		return Decision{Action: ActionUpdate}
	case policy.Pinned:
		return refuse("%s is older than the installed %s; pass --allow-downgrade to install it", target.Original(), running.Original())
	}
	// The newest release is older than this binary, as with a yanked release.
	return Decision{Action: ActionUpToDate}
}

func parseVersion(value string) (*semver.Version, error) {
	return semver.StrictNewVersion(strings.TrimPrefix(value, "v"))
}
