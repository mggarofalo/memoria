package cmd

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"

	"github.com/spf13/cobra"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/auth"
	"github.com/mggarofalo/memoria/cli/internal/output"
	"github.com/mggarofalo/memoria/cli/internal/sources"
	"github.com/mggarofalo/memoria/cli/internal/transport"
)

func newSourcesCmd(a *app) *cobra.Command {
	cmd := &cobra.Command{
		Use:   "sources",
		Short: "Keep local folders of Markdown in the corpus",
		Long: `Send the Markdown files in local folders to the server, each to the collection you
choose. The server redacts everything it stores.

A sync sends only what changed, and never deletes anything on the server.`,
		Args: noArgs,
		RunE: func(cmd *cobra.Command, _ []string) error { return cmd.Help() },
	}
	cmd.AddCommand(newSourcesListCmd(a), newSourcesAddCmd(a), newSourcesRemoveCmd(a), newSourcesSyncCmd(a))
	return cmd
}

func newSourcesAddCmd(a *app) *cobra.Command {
	var source sources.Source
	cmd := &cobra.Command{
		Use:   "add <name> --path <folder> --collection <name>",
		Short: "Configure a folder to sync. Sends nothing",
		Long: `Configure a folder to sync. This sends nothing and needs no server.

The name is part of each document's identity on the server, so keep it: the same
folder under a new name would store every document again.`,
		Example: `  memoria sources add notes --path ~/notes --collection notes`,
		Args:    oneSourceName,
		RunE: func(_ *cobra.Command, args []string) error {
			source.Name = args[0]
			return a.sourcesAdd(source)
		},
	}
	f := cmd.Flags()
	f.StringVar(&source.Path, "path", "", "The folder to send (required)")
	f.StringVar(&source.Collection, "collection", "", "The collection its documents go to (required)")
	return cmd
}

// oneSourceName does not repeat what it refuses: a key pasted here must not come back.
func oneSourceName(cmd *cobra.Command, args []string) error {
	if len(args) != 1 {
		return apperr.New(apperr.KindUsage, "%q takes exactly one source name", cmd.CommandPath())
	}
	// A name is written to the config and sent to the server, so nothing shaped like a key can be one.
	if !sources.ValidName(args[0]) || publicKeyID(args[0]) != "" {
		return apperr.New(apperr.KindUsage, "a source name may hold letters, digits, '.', '_' and '-', and must start with a letter or digit")
	}
	return nil
}

func (a *app) sourcesAdd(source sources.Source) error {
	if !sources.ValidCollection(source.Collection) {
		return apperr.New(apperr.KindUsage, "--collection is required: a name with no slash or surrounding space")
	}
	dir, err := existingFolder(source.Path)
	if err != nil {
		return err
	}
	source.Path = dir
	config, err := sources.Load()
	if err != nil {
		return err
	}
	if err := config.Add(source); err != nil {
		return apperr.Wrap(apperr.KindUsage, err, err.Error())
	}
	if err := config.Save(); err != nil {
		return err
	}
	a.info(fmt.Sprintf("Added source %s. Nothing has been sent: 'memoria sources sync %s' shows what would be.", source.Name, source.Name))
	return nil
}

func existingFolder(path string) (string, error) {
	if path == "" {
		return "", apperr.New(apperr.KindUsage, "--path is required")
	}
	dir, err := filepath.Abs(path)
	if err != nil {
		return "", apperr.New(apperr.KindUsage, "--path could not be resolved")
	}
	info, err := os.Stat(dir)
	if err != nil || !info.IsDir() {
		return "", apperr.New(apperr.KindUsage, "--path is not a folder this user can read")
	}
	return dir, nil
}

func newSourcesRemoveCmd(a *app) *cobra.Command {
	return &cobra.Command{
		Use:   "remove <name>",
		Short: "Stop syncing a folder. Deletes nothing on the server",
		Args:  oneSourceName,
		RunE:  func(_ *cobra.Command, args []string) error { return a.sourcesRemove(args[0]) },
	}
}

func (a *app) sourcesRemove(name string) error {
	config, err := sources.Load()
	if err != nil {
		return err
	}
	if !config.Remove(name) {
		return apperr.New(apperr.KindNotFound, "no source has that name; see 'memoria sources list'")
	}
	if err := config.Save(); err != nil {
		return err
	}
	a.info("Removed source " + name + ". Its documents stay on the server.")
	return nil
}

func newSourcesListCmd(a *app) *cobra.Command {
	return &cobra.Command{
		Use:   "list",
		Short: "List configured folders and their collections. Needs no server",
		Args:  noArgs,
		RunE:  func(cmd *cobra.Command, _ []string) error { return a.sourcesList(cmd.Context()) },
	}
}

func (a *app) sourcesList(ctx context.Context) error {
	config, err := sources.Load()
	if err != nil {
		return err
	}
	listings := make([]sources.Listing, 0, len(config.Sources))
	origin := a.originIfKnown()
	for _, source := range config.Sources {
		listing, err := listingOf(source, origin)
		if err != nil {
			return err
		}
		listings = append(listings, listing)
	}
	raw, err := json.Marshal(listings)
	if err != nil {
		return err
	}
	return a.present(ctx, output.Table, raw, map[output.Format]func(io.Writer) error{
		output.Markdown: func(w io.Writer) error { return output.SourcesMarkdown(w, listings) },
		output.Table:    func(w io.Writer) error { return output.SourcesTable(w, listings) },
	})
}

// listingOf reads the cursor for the current server. With no server chosen, what is
// tracked is unknown, which is not the same as nothing.
func listingOf(source sources.Source, origin string) (sources.Listing, error) {
	listing := sources.Listing{Source: source}
	if origin == "" {
		return listing, nil
	}
	state, _, err := sources.LoadState(origin, source)
	if err != nil {
		return listing, err
	}
	count := len(state.Files)
	listing.Tracked, listing.LastSync = &count, state.LastSync
	return listing, nil
}

// originIfKnown is for showing state, so it stays quiet about a missing server.
func (a *app) originIfKnown() string {
	origin, err := a.localOrigin()
	if err != nil {
		return ""
	}
	return origin
}

// localOrigin names the server a cursor belongs to, without reading any key or
// touching the network.
func (a *app) localOrigin() (string, error) {
	cfg, err := a.loadConfig()
	if err != nil {
		return "", err
	}
	profile, err := a.profileName(cfg)
	if err != nil {
		return "", err
	}
	resolver := &auth.Resolver{FlagURL: a.flags.apiURL, Profile: profile, Config: cfg, Getenv: a.getenv}
	base, err := a.parseURL(resolver.ResolveURL())
	if err != nil {
		return "", err
	}
	return transport.Origin(base), nil
}

// failedError is the summary of a run that finished, but left files unsent.
func failedError(failed int) error {
	if failed == 0 {
		return nil
	}
	return apperr.New(apperr.KindGeneric, "%d file(s) were not sent; each is named above, and the next sync tries them again", failed)
}

// firstOf returns the first error, the one that stopped the run.
func firstOf(errs ...error) error {
	for _, err := range errs {
		if err != nil {
			return err
		}
	}
	return nil
}
