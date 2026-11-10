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
	"regexp"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/spf13/cobra"
	"golang.org/x/term"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/auth"
	"github.com/mggarofalo/memoria/cli/internal/harden"
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
	// rawStdout is for the MCP bridge alone. It scrubs each frame after decoding, and
	// a byte-level pass here would undo the client's own id if it held the key.
	rawStdout io.Writer
	stderr    io.Writer // Scrubbed of the key and anything credential-shaped.

	redactor        *redact.Redactor
	getenv          func(string) string
	stdinIsTerminal func() bool
	readPassword    func() ([]byte, error)
	openKeyring     func() (auth.SecretStore, error)
	selfUpdate      selfUpdateDeps
	sleep           func(context.Context, time.Duration) error
	redactionCheck  func(context.Context, []string, []byte) ([]byte, error)
	localQMD        func(context.Context, string, []string) ([]byte, error)

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
	newKeysCmd,
	newWorkersCmd,
	newQueueCmd,
	newReindexCmd,
	newReadyCmd,
	newSourcesCmd,
	newMigrateCmd,
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
		return safeFlagError(err)
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
		return apperr.Wrap(apperr.KindUsage, err, err.Error())
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

// flagNamePattern finds a flag's name in pflag's errors, which also quote the
// value given. A value can be a key pasted in the wrong place, so only the name
// goes on.
var flagNamePattern = regexp.MustCompile(`(--[a-z][a-z-]*)\b`)

func safeFlagError(err error) error {
	message := err.Error()
	if strings.HasPrefix(message, "unknown") || strings.HasPrefix(message, "bad flag") {
		return apperr.New(apperr.KindUsage, "unknown flag; see --help")
	}
	if name := flagNamePattern.FindString(lastClause(message)); name != "" {
		return apperr.New(apperr.KindUsage, "invalid value for %s; see --help", name)
	}
	return apperr.New(apperr.KindUsage, "invalid flag or value; see --help")
}

// lastClause skips the quoted value at the start of pflag's "invalid argument"
// errors, so that a value shaped like a flag is not taken for the flag's name.
func lastClause(message string) string {
	if i := strings.LastIndex(message, "\" for "); i >= 0 {
		return message[i:]
	}
	return message
}

// registerArgSecrets finds an --api-key value before cobra parses anything, in
// either dash form, so that nothing a parse error quotes can repeat it.
func registerArgSecrets(r *redact.Redactor, args []string) {
	for i, arg := range args {
		name := strings.TrimLeft(arg, "-")
		if value, ok := strings.CutPrefix(name, "api-key="); ok && name != arg {
			r.Add(value)
		} else if name == "api-key" && arg != name && i+1 < len(args) {
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
	err = classifyCobraError(err)
	fmt.Fprintln(a.stderr, "error: "+termsafe.Line(err.Error()))
	return apperr.ExitCode(err)
}

// classifyCobraError replaces cobra's unknown-command error, which quotes the
// argument: a bare key given as a command must not come back on stderr.
func classifyCobraError(err error) error {
	var classified *apperr.Error
	if !errors.As(err, &classified) && strings.HasPrefix(err.Error(), "unknown command") {
		return apperr.New(apperr.KindUsage, "unknown command; see 'memoria --help'")
	}
	return err
}

// lockedWriter keeps diagnostics whole when several workers write at once.
type lockedWriter struct {
	mu sync.Mutex
	to io.Writer
}

func (l *lockedWriter) Write(p []byte) (int, error) {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.to.Write(p)
}

func newApp(stdin io.Reader, stdout, stderr io.Writer) *app {
	r := redact.New()
	return &app{
		stdin:           stdin,
		stdout:          r.Writer(stdout),
		rawStdout:       stdout,
		stderr:          &lockedWriter{to: r.DiagnosticWriter(stderr)},
		redactor:        r,
		getenv:          os.Getenv,
		stdinIsTerminal: func() bool { return term.IsTerminal(int(os.Stdin.Fd())) },
		readPassword:    func() ([]byte, error) { return term.ReadPassword(int(os.Stdin.Fd())) },
		openKeyring:     auth.NewKeyringStore,
		selfUpdate:      defaultSelfUpdateDeps(),
		sleep:           sleepUntilDone,
		redactionCheck:  runDotnet,
		localQMD:        runLocalQMD,
	}
}

func sleepUntilDone(ctx context.Context, wait time.Duration) error {
	timer := time.NewTimer(wait)
	defer timer.Stop()
	select {
	case <-timer.C:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

// Execute runs the command tree and returns the process exit code.
func Execute() int {
	_ = harden.Process() // Best effort: it only removes a defence in depth.
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	return newApp(os.Stdin, os.Stdout, os.Stderr).run(ctx, os.Args[1:])
}
