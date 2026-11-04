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
}

func newSelfUpdateCmd(a *app) *cobra.Command {
	var o selfUpdateOptions
	cmd := &cobra.Command{
		Use:   "self-update",
		Short: "Replace this binary with a GitHub release",
		Long: `Download a release over https, verify its SHA-256 against the release's
checksums.txt, swap it in, and run the new binary's 'version' command. If that
fails, the previous binary is restored.

The checksum comes from the same release as the archive, so it catches
corruption and a tampered asset, not a compromised release. For an independent
check, each release has a build provenance attestation:
  gh attestation verify <archive> --repo mggarofalo/memoria

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
	f.BoolVar(&o.force, "force", false, "Allow replacing a development build")
	f.BoolVar(&o.prerelease, "prerelease", false, "Allow installing a prerelease")
	f.BoolVar(&o.allowDowngrad, "allow-downgrade", false, "Allow installing an older version")
	return cmd
}

func (a *app) runSelfUpdate(ctx context.Context, o selfUpdateOptions) error {
	if o.version != "" && !selfupdate.ValidTag(o.version) {
		return apperr.New(apperr.KindUsage, "--version must look like v1.2.3")
	}
	updater := a.selfUpdate.newUpdater(selfupdate.Options{Prerelease: o.prerelease})
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
		return err
	}
	a.info("Updated to " + candidate.Tag + ".")
	return nil
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
