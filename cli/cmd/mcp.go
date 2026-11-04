package cmd

import (
	"strings"

	"github.com/spf13/cobra"

	"github.com/mggarofalo/memoria/cli/internal/mcpbridge"
)

type mcpOptions struct {
	path             string
	maxMessageBytes  int
	maxResponseBytes int
}

func newMCPCmd(a *app) *cobra.Command {
	var o mcpOptions
	cmd := &cobra.Command{
		Use:   "mcp",
		Short: "Serve MCP on stdio, forwarding to the server's /mcp endpoint",
		Long: `Bridge for MCP clients that can only launch a local process. It reads JSON-RPC
from stdin, one message per line, forwards each to the server with the stored
key, and writes replies to stdout.

Stdout carries protocol frames only; logs go to stderr. Missing credentials and
upstream failures are answered as JSON-RPC errors rather than by exiting, so
the client can show why. Set --timeout to limit each upstream exchange (default
5m here).`,
		Args: noArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			return a.mcp(cmd, o)
		},
	}
	f := cmd.Flags()
	f.StringVar(&o.path, "path", "/mcp", "MCP endpoint path on the server")
	f.IntVar(&o.maxMessageBytes, "max-message-bytes", mcpbridge.DefaultMaxInboundBytes, "Largest message accepted from the client")
	f.IntVar(&o.maxResponseBytes, "max-response-bytes", mcpbridge.DefaultMaxOutboundBytes, "Largest frame accepted from the server")
	return cmd
}

func (a *app) mcp(cmd *cobra.Command, o mcpOptions) error {
	options := mcpbridge.Options{
		Redactor:         a.redactor,
		Log:              a.stderr,
		MaxInboundBytes:  o.maxMessageBytes,
		MaxOutboundBytes: o.maxResponseBytes,
		RequestTimeout:   mcpbridge.DefaultRequestTimeout,
	}
	if cmd.Flags().Changed("timeout") {
		options.RequestTimeout = a.flags.timeout
	}

	// The bridge sets a deadline per exchange, so the client gets none of its
	// own; a long SSE stream must not be cut by a whole-request timeout.
	a.flags.timeout = 0
	s, err := a.connect()
	if err != nil {
		options.StartupErr = err
	} else {
		options.Client = s.http
		options.Endpoint = s.endpoint + "/" + strings.TrimPrefix(o.path, "/")
	}
	return mcpbridge.New(a.stdout, options).Run(cmd.Context(), a.stdin)
}
