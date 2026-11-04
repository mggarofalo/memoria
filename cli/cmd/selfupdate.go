package cmd

import (
	"context"
	"errors"
	"fmt"
	"os"

	"github.com/spf13/cobra"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/selfupdate"
)

// releaseUpdater is the part of selfupdate.Updater the command needs.
type releaseUpdater interface {
	Find(ctx context.Context, tag string) (*selfupdate.Candidate, error)
	Install(ctx context.Context, candidate *selfupdate.Candidate, exe string) error
}

type selfUpdateDeps struct {
	newUpdater func(selfupdate.Options) releaseUpdater
	executable func() (string, error)
}

func defaultSelfUpdateDeps() selfUpdateDeps {
	return selfUpdateDeps{
		newUpdater: func(o selfupdate.Options) releaseUpdater { return selfupdate.New(o) },
		executable: os.Executable,
	}
}

type selfUpdateOptions struct {
	check         bool
	version       string
	force         bool
	prerelease    bool
	allowDowngrad bool
	checksumOnly  bool
}

func newSelfUpdateCmd(a *app) *cobra.Command {
	var o selfUpdateOptions
	cmd := &cobra.Command{
		Use:   "self-update",
		Short: "Replace this binary with a GitHub release",
		Long: `Download a release over https, verify its SHA-256 against the release's
checksums.txt, verify its build provenance with Sigstore, swap it in, and run the
new binary's 'version' command. If that fails, the previous binary is restored.

Provenance must be signed by this repository's release workflow for that exact
tag. If it cannot be verified, nothing is installed. --checksum-only skips it and
trusts checksums.txt alone, which comes from the same release as the archive and
so cannot catch a compromised release.

Development builds refuse to update unless --force is given.`,
		Example: `  memoria self-update --check
  memoria self-update
  memoria self-update --version v1.2.3
  memoria self-update --version v1.1.0 --allow-downgrade`,
		Args: noArgs,
		RunE: func(cmd *cobra.Command, _ []string) error { return a.runSelfUpdate(cmd.Context(), o) },
	}
	f := cmd.Flags()
	f.BoolVar(&o.check, "check", false, "Report what would happen and change nothing")
	f.StringVar(&o.version, "version", "", "Install this release, vX.Y.Z, instead of the newest")
	f.BoolVar(&o.force, "force", false, "Allow replacing a development build, or a binary a package manager appears to own")
	f.BoolVar(&o.prerelease, "prerelease", false, "Allow installing a prerelease")
	f.BoolVar(&o.allowDowngrad, "allow-downgrade", false, "Allow installing an older version")
	f.BoolVar(&o.checksumOnly, "checksum-only", false, "Skip provenance verification and trust checksums.txt alone. Weaker: it cannot catch a compromised release")
	return cmd
}

func (a *app) runSelfUpdate(ctx context.Context, o selfUpdateOptions) error {
	if o.version != "" && !selfupdate.ValidTag(o.version) {
		return apperr.New(apperr.KindUsage, "--version must look like v1.2.3")
	}
	updater := a.selfUpdate.newUpdater(selfupdate.Options{Prerelease: o.prerelease, ChecksumOnly: o.checksumOnly, Force: o.force})
	candidate, err := updater.Find(ctx, o.version)
	if err != nil {
		return classifyFindError(err)
	}
	decision := selfupdate.DecideCandidate(Version, candidate, selfupdate.Policy{
		Force:           o.force,
		AllowDowngrade:  o.allowDowngrad,
		AllowPrerelease: o.prerelease,
		Pinned:          o.version != "",
	})
	if o.check {
		return a.reportCheck(candidate, decision)
	}
	switch decision.Action {
	case selfupdate.ActionRefuse:
		return apperr.New(apperr.KindGeneric, "%s", decision.Reason)
	case selfupdate.ActionUpToDate:
		a.info("Already up to date (" + Version + ").")
		return nil
	}
	if o.checksumOnly {
		a.warn("--checksum-only: not verifying build provenance. checksums.txt comes from the same release, so a compromised release would not be caught")
	}
	return a.installRelease(ctx, updater, candidate)
}

func classifyFindError(err error) error {
	if errors.Is(err, selfupdate.ErrNoRelease) {
		return apperr.Wrap(apperr.KindNotFound, err, err.Error())
	}
	return apperr.Wrap(apperr.KindUnavailable, err, "checking for a release: "+err.Error())
}

func (a *app) reportCheck(candidate *selfupdate.Candidate, decision selfupdate.Decision) error {
	var message string
	switch decision.Action {
	case selfupdate.ActionUpdate:
		message = "update available: " + Version + " -> " + candidate.Tag
	case selfupdate.ActionUpToDate:
		message = "up to date: " + Version + " (latest release " + candidate.Tag + ")"
	default:
		message = "found " + candidate.Tag + ", but would not install it: " + decision.Reason
	}
	_, err := fmt.Fprintln(a.stdout, message)
	return err
}

func (a *app) installRelease(ctx context.Context, updater releaseUpdater, candidate *selfupdate.Candidate) error {
	exe, err := a.selfUpdate.executable()
	if err != nil {
		return fmt.Errorf("locating this binary: %w", err)
	}
	a.info("Installing " + candidate.Tag + " from " + candidate.Asset + "...")
	if err := updater.Install(ctx, candidate, exe); err != nil {
		return explainInstallFailure(err)
	}
	a.info("Updated to " + candidate.Tag + ".")
	return nil
}

func explainInstallFailure(err error) error {
	if errors.Is(err, selfupdate.ErrAttestation) {
		return fmt.Errorf("%w; nothing was installed. Only if you accept the risk, --checksum-only skips this check", err)
	}
	return err
}

func newVersionCmd(a *app) *cobra.Command {
	return &cobra.Command{
		Use:   "version",
		Short: "Print the version",
		Args:  noArgs,
		RunE: func(*cobra.Command, []string) error {
			_, err := fmt.Fprintln(a.stdout, "memoria", Version)
			return err
		},
	}
}
