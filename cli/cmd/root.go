// Package cmd wires the memoria command tree.
//
// Each command lives in its own file as a newXCmd(*app) factory, listed once in
// commandFactories below. Later commands (key administration, sources, worker,
// queue, migrate) slot in the same way and get credentials, the guarded HTTP
// client, output formats and exit codes from app without further plumbing.
package cmd

import (
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"os/signal"
	"strings"
	"syscall"
	"time"

	"github.com/spf13/cobra"
	"golang.org/x/term"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/auth"
	"github.com/mggarofalo/memoria/cli/internal/output"
	"github.com/mggarofalo/memoria/cli/internal/redact"
	"github.com/mggarofalo/memoria/cli/internal/termsafe"
)

// Version is stamped at build time via -ldflags.
var Version = "dev"

const defaultTimeout = 2 * time.Minute

// app holds a command's dependencies, so tests can run the real tree against a
// fake server, a fake keyring and captured output.
type app struct {
	stdin  io.Reader
	stdout io.Writer // Scrubbed of the key.
	stderr io.Writer // Scrubbed of the key and anything credential-shaped.

	redactor        *redact.Redactor
	getenv          func(string) string
	stdinIsTerminal func() bool
	readPassword    func() ([]byte, error)
	openKeyring     func() (auth.SecretStore, error)
	selfUpdate      selfUpdateDeps

	flags rootFlags
}

type rootFlags struct {
	apiURL        string
	apiKey        string
	profile       string
	format        string
	verbose       bool
	quiet         bool
	allowInsecure bool
	timeout       time.Duration
}

var commandFactories = []func(*app) *cobra.Command{
	newAuthCmd,
	newQueryCmd,
	newGetCmd,
	newMultiGetCmd,
	newCollectionsCmd,
	newStatusCmd,
	newMCPCmd,
	newSelfUpdateCmd,
	newVersionCmd,
}

// newRootCmd builds the tree against the real process streams.
func newRootCmd() *cobra.Command {
	return newApp(os.Stdin, os.Stdout, os.Stderr).rootCmd()
}

func (a *app) rootCmd() *cobra.Command {
	root := &cobra.Command{
		Use:           "memoria",
		Short:         "Search your own accumulated record",
		Long:          "memoria queries a Memoria server: agent transcripts, issues, PRs, notes, and docs, indexed together.",
		SilenceUsage:  true,
		SilenceErrors: true,
		Version:       Version,
		PersistentPreRunE: func(*cobra.Command, []string) error {
			return a.prepare()
		},
	}
	root.SetIn(a.stdin)
	root.SetOut(a.stdout)
	root.SetErr(a.stderr)
	root.SetFlagErrorFunc(func(_ *cobra.Command, err error) error {
		return apperr.New(apperr.KindUsage, "%s", err.Error())
	})

	pf := root.PersistentFlags()
	pf.StringVar(&a.flags.apiURL, "api-url", "", "Memoria server URL (or "+auth.EnvURL+", or the profile from 'auth login')")
	pf.StringVar(&a.flags.apiKey, "api-key", "", "API key. Avoid this: flags land in shell history and process listings. Prefer 'auth login' or "+auth.EnvAPIKey)
	pf.StringVar(&a.flags.profile, "profile", "", "Configuration profile (or "+auth.EnvProfile+")")
	pf.StringVar(&a.flags.format, "format", "", "Output format: json, table, or markdown. json is the server's response, byte for byte")
	pf.BoolVarP(&a.flags.verbose, "verbose", "v", false, "Log requests to stderr (never headers or the key)")
	pf.BoolVarP(&a.flags.quiet, "quiet", "q", false, "Suppress informational messages")
	pf.BoolVar(&a.flags.allowInsecure, "allow-insecure-http", false, "Allow sending the key over plain http to a public address (or "+auth.EnvAllowInsecureHTTP+"=1). The key can be read in transit")
	pf.DurationVar(&a.flags.timeout, "timeout", defaultTimeout, "Time limit for each request")

	for _, factory := range commandFactories {
		root.AddCommand(factory(a))
	}
	return root
}

// prepare validates global flags and warns about a key on the command line.
func (a *app) prepare() error {
	if _, err := output.ParseFormat(a.flags.format, output.Table); err != nil {
		return apperr.New(apperr.KindUsage, "%s", err.Error())
	}
	if a.flags.apiKey != "" {
		a.warn("--api-key puts the key in your shell history and in process listings; prefer 'memoria auth login' or " + auth.EnvAPIKey)
	}
	return nil
}

func (a *app) warn(message string) {
	fmt.Fprintln(a.stderr, "warning: "+termsafe.Line(message))
}

func (a *app) info(message string) {
	if !a.flags.quiet {
		fmt.Fprintln(a.stderr, termsafe.Line(message))
	}
}

// noArgs rejects positional arguments without repeating them, unlike cobra's
// own validators: a key pasted as an argument must not come back in an error.
func noArgs(cmd *cobra.Command, args []string) error {
	if len(args) > 0 {
		return apperr.New(apperr.KindUsage, "%q takes no positional arguments", cmd.CommandPath())
	}
	return nil
}

// registerArgSecrets finds the --api-key value before cobra parses anything, so
// a parse error that quotes an argument is scrubbed too.
func registerArgSecrets(r *redact.Redactor, args []string) {
	for i, arg := range args {
		if value, ok := strings.CutPrefix(arg, "--api-key="); ok {
			r.Add(value)
		} else if arg == "--api-key" && i+1 < len(args) {
			r.Add(args[i+1])
		}
	}
}

// run executes the tree and returns the exit code.
func (a *app) run(ctx context.Context, args []string) int {
	registerArgSecrets(a.redactor, args)
	a.redactor.Add(a.getenv(auth.EnvAPIKey))

	root := a.rootCmd()
	root.SetArgs(args)
	err := root.ExecuteContext(ctx)
	if err == nil {
		return apperr.ExitOK
	}
	fmt.Fprintln(a.stderr, "error: "+termsafe.Line(err.Error()))
	return exitCodeFor(err)
}

// exitCodeFor maps errors, treating cobra's unclassified unknown-command error
// as a usage error.
func exitCodeFor(err error) int {
	var classified *apperr.Error
	if !errors.As(err, &classified) && strings.HasPrefix(err.Error(), "unknown command") {
		return apperr.ExitUsage
	}
	return apperr.ExitCode(err)
}

func newApp(stdin io.Reader, stdout, stderr io.Writer) *app {
	r := redact.New()
	return &app{
		stdin:           stdin,
		stdout:          r.Writer(stdout),
		stderr:          r.DiagnosticWriter(stderr),
		redactor:        r,
		getenv:          os.Getenv,
		stdinIsTerminal: func() bool { return term.IsTerminal(int(os.Stdin.Fd())) },
		readPassword:    func() ([]byte, error) { return term.ReadPassword(int(os.Stdin.Fd())) },
		openKeyring:     func() (auth.SecretStore, error) { return auth.NewKeyringStore() },
		selfUpdate:      defaultSelfUpdateDeps(),
	}
}

// Execute runs the command tree and returns the process exit code.
func Execute() int {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	return newApp(os.Stdin, os.Stdout, os.Stderr).run(ctx, os.Args[1:])
}
