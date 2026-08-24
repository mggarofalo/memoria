// Package cmd wires the memoria command tree.
package cmd

import (
	"github.com/spf13/cobra"
)

// Version is stamped at build time via -ldflags.
var Version = "dev"

var (
	flagAPIURL string
	flagAPIKey string
)

func newRootCmd() *cobra.Command {
	root := &cobra.Command{
		Use:           "memoria",
		Short:         "Search your own accumulated record",
		Long:          "memoria queries a Memoria server: agent transcripts, issues, PRs, notes, and docs, indexed together.",
		SilenceUsage:  true,
		SilenceErrors: true,
		Version:       Version,
	}

	pf := root.PersistentFlags()
	pf.StringVar(&flagAPIURL, "api-url", "", "Memoria server URL (prefer keyring or env var)")
	pf.StringVar(&flagAPIKey, "api-key", "", "API key (prefer keyring or env var)")

	return root
}

// Execute runs the command tree.
func Execute() error {
	return newRootCmd().Execute()
}
