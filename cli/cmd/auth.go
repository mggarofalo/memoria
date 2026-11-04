package cmd

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"

	"github.com/spf13/cobra"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/auth"
	"github.com/mggarofalo/memoria/cli/internal/output"
	"github.com/mggarofalo/memoria/cli/internal/termsafe"
)

const maxStdinKeyBytes = 64 * 1024

func newAuthCmd(a *app) *cobra.Command {
	cmd := &cobra.Command{
		Use:   "auth",
		Short: "Manage API keys",
		Long: `Store and inspect the API key used to reach a Memoria server.

The key is looked up in this order: --api-key, ` + auth.EnvAPIKey + `, the OS keyring,
then the credentials file beside the config (written only by --backend file).
A flag lands in shell history and process listings, and an environment variable
is inherited by child processes. Only the keyring encrypts the key at rest. The
config file never holds a key.`,
		Args: noArgs,
		RunE: func(cmd *cobra.Command, _ []string) error { return cmd.Help() },
	}
	cmd.AddCommand(newAuthLoginCmd(a), newAuthLogoutCmd(a), newAuthStatusCmd(a))
	return cmd
}

type loginOptions struct {
	backend  string
	noVerify bool
}

func newAuthLoginCmd(a *app) *cobra.Command {
	var o loginOptions
	cmd := &cobra.Command{
		Use:   "login",
		Short: "Check an API key against the server and store it",
		Long: `Store an API key for the server at --api-url.

The key is read from a prompt with echo off, or from stdin when stdin is not a
terminal. It is sent to the server to check it, then saved.

--backend file writes the key unencrypted to a credentials file, for hosts with
no keyring such as CI. Nothing falls back to that silently.`,
		Example: `  memoria auth login --api-url https://memoria.example.com
  printf '%s' "$KEY" | memoria auth login --api-url https://memoria.example.com --backend file`,
		Args: noArgs,
		RunE: func(cmd *cobra.Command, _ []string) error { return a.login(cmd.Context(), o) },
	}
	cmd.Flags().StringVar(&o.backend, "backend", auth.BackendKeyring, "Where to store the key: keyring, or file (unencrypted)")
	cmd.Flags().BoolVar(&o.noVerify, "no-verify", false, "Store the key without checking it against the server")
	return cmd
}

func (a *app) login(ctx context.Context, o loginOptions) error {
	store, cfg, profile, err := a.loginTarget(o.backend)
	if err != nil {
		return err
	}
	base, err := a.parseURL(a.loginURL(cfg, profile))
	if err != nil {
		return err
	}
	key, err := a.readKey()
	if err != nil {
		return err
	}
	a.redactor.Add(key)
	session, err := a.newSession(base, key)
	if err != nil {
		return err
	}
	if !o.noVerify {
		if err := a.verifyKey(ctx, session); err != nil {
			return err
		}
	}
	if err := store.Set(auth.KeyName(profile), key); err != nil {
		return err
	}
	a.removeFromOtherBackend(o.backend, profile)

	cfg.ActiveProfile = profile
	cfg.Profiles[profile] = auth.Profile{APIURL: base.String(), Backend: o.backend}
	if err := cfg.Save(); err != nil {
		return err
	}
	a.reportLogin(o.backend, profile, base.String())
	return nil
}

// loginTarget opens the chosen store before the key is read, so a machine with
// no keyring fails before anyone has pasted anything.
func (a *app) loginTarget(backend string) (auth.SecretStore, *auth.Config, string, error) {
	cfg, err := a.loadConfig()
	if err != nil {
		return nil, nil, "", err
	}
	profile, err := a.profileName(cfg)
	if err != nil {
		return nil, nil, "", err
	}
	store, err := a.openBackend(backend)
	if err != nil {
		return nil, nil, "", err
	}
	return store, cfg, profile, nil
}

func (a *app) openBackend(backend string) (auth.SecretStore, error) {
	switch backend {
	case auth.BackendFile:
		a.warn(auth.FileWarning)
		return auth.NewFileStore()
	case auth.BackendKeyring:
		store, err := a.openKeyring()
		if err != nil {
			return nil, apperr.Wrap(apperr.KindGeneric, err,
				"cannot use the OS keyring ("+err.Error()+"). On a host without one, --backend file stores the key unencrypted; read its warning before using it")
		}
		return store, nil
	}
	return nil, apperr.New(apperr.KindUsage, "unknown backend %q: use keyring or file", termsafe.Line(backend))
}

// loginURL takes the URL from flag, env or profile, and prompts only at a
// terminal: piped stdin is the key.
func (a *app) loginURL(cfg *auth.Config, profile string) string {
	resolver := auth.Resolver{FlagURL: a.flags.apiURL, Profile: profile, Config: cfg, Getenv: a.getenv}
	if existing := resolver.ResolveURL(); existing != "" || !a.stdinIsTerminal() {
		return existing
	}
	fmt.Fprint(a.stderr, "Memoria server URL: ")
	line, _ := bufio.NewReader(a.stdin).ReadString('\n')
	return strings.TrimSpace(line)
}

func (a *app) readKey() (string, error) {
	raw, err := a.rawKey()
	if err != nil {
		return "", err
	}
	key, err := auth.NormalizeKey(raw)
	if err != nil {
		return "", apperr.Wrap(apperr.KindUsage, err, err.Error())
	}
	return key, nil
}

func (a *app) rawKey() (string, error) {
	if a.flags.apiKey != "" {
		return a.flags.apiKey, nil
	}
	if a.stdinIsTerminal() {
		return a.promptKey()
	}
	return readStdinKey(a.stdin)
}

func (a *app) promptKey() (string, error) {
	fmt.Fprint(a.stderr, "API key (input hidden): ")
	data, err := a.readPassword()
	fmt.Fprintln(a.stderr)
	if err != nil {
		return "", fmt.Errorf("reading the key: %w", err)
	}
	return string(data), nil
}

// readStdinKey reads one line, bounded, so a mistaken pipe cannot fill memory.
func readStdinKey(r io.Reader) (string, error) {
	data, err := io.ReadAll(io.LimitReader(r, maxStdinKeyBytes+1))
	if err != nil {
		return "", fmt.Errorf("reading the key from stdin: %w", err)
	}
	if len(data) > maxStdinKeyBytes {
		return "", apperr.New(apperr.KindUsage, "the key on stdin is too long")
	}
	line, _, _ := strings.Cut(string(data), "\n")
	return line, nil
}

// verifyKey checks the key with a harmless read. A 403 means the server knows
// the key and it simply has no read scope, which is valid for an admin or ingest
// key, so that is stored with a note.
func (a *app) verifyKey(ctx context.Context, s *session) error {
	resp, err := s.client.ListCollectionsWithResponse(ctx)
	if err != nil {
		return apperr.FromTransport(err)
	}
	if resp.StatusCode() == http.StatusForbidden {
		a.warn("the server accepted this key, but it has no read access")
		return nil
	}
	return checkResponse(resp.StatusCode(), resp.HTTPResponse.Header, resp.Body)
}

// removeFromOtherBackend deletes a copy left by an earlier login, so that the
// precedence order can never select a stale key.
func (a *app) removeFromOtherBackend(backend, profile string) {
	var other auth.SecretStore
	var err error
	if backend == auth.BackendFile {
		other, err = a.openKeyring()
	} else {
		other, err = auth.NewFileStore()
	}
	if err == nil {
		_ = other.Delete(auth.KeyName(profile))
	}
}

func (a *app) reportLogin(backend, profile, url string) {
	where := "the OS keyring"
	if backend == auth.BackendFile {
		where = "the credentials file"
	}
	a.info(fmt.Sprintf("Key stored in %s for profile %q (%s).", where, profile, url))
	if a.getenv(auth.EnvAPIKey) != "" {
		a.warn(auth.EnvAPIKey + " is set and takes precedence over the stored key")
	}
}

func newAuthLogoutCmd(a *app) *cobra.Command {
	return &cobra.Command{
		Use:   "logout",
		Short: "Remove the stored API key",
		Long:  "Remove the profile's key from the OS keyring and the credentials file. This does not revoke it on the server.",
		Args:  noArgs,
		RunE:  func(*cobra.Command, []string) error { return a.logout() },
	}
}

func (a *app) logout() error {
	cfg, err := a.loadConfig()
	if err != nil {
		return err
	}
	profile, err := a.profileName(cfg)
	if err != nil {
		return err
	}
	removed, failure := a.deleteStoredKeys(profile)
	if failure != nil {
		return failure
	}
	if removed {
		a.info(fmt.Sprintf("Removed the stored key for profile %q. It stays valid on the server until revoked there.", profile))
	} else {
		a.info(fmt.Sprintf("No stored key for profile %q.", profile))
	}
	if a.getenv(auth.EnvAPIKey) != "" {
		a.warn(auth.EnvAPIKey + " is still set in this environment")
	}
	return nil
}

// deleteStoredKeys tries both stores. An absent keyring is not a failure, since
// the key may never have been there.
func (a *app) deleteStoredKeys(profile string) (removed bool, failure error) {
	stores := make([]auth.SecretStore, 0, 2)
	if ring, err := a.openKeyring(); err == nil {
		stores = append(stores, ring)
	}
	file, err := auth.NewFileStore()
	if err != nil {
		return false, err
	}
	stores = append(stores, file)
	for _, store := range stores {
		err := store.Delete(auth.KeyName(profile))
		removed = removed || err == nil
		if err != nil && !errors.Is(err, auth.ErrNotFound) {
			failure = errors.Join(failure, err)
		}
	}
	return removed, failure
}

// authStatus is the output of auth status. It says a key is present and where
// it came from. It has no field that could hold any part of the key.
type authStatus struct {
	Profile       string `json:"profile"`
	URL           string `json:"url"`
	Authenticated bool   `json:"authenticated"`
	Source        string `json:"source"`
	Verified      *bool  `json:"verified,omitempty"`
	Message       string `json:"message,omitempty"`
}

func newAuthStatusCmd(a *app) *cobra.Command {
	var offline bool
	cmd := &cobra.Command{
		Use:   "status",
		Short: "Show where the API key comes from and whether the server accepts it",
		Long:  "Show the active profile, server and key source. The key itself is never shown, in whole or in part. Exits 3 when no usable key is found.",
		Args:  noArgs,
		RunE: func(cmd *cobra.Command, _ []string) error {
			return a.authStatus(cmd.Context(), offline)
		},
	}
	cmd.Flags().BoolVar(&offline, "offline", false, "Do not contact the server")
	return cmd
}

func (a *app) authStatus(ctx context.Context, offline bool) error {
	s, err := a.connect()
	if err != nil {
		return err
	}
	status := authStatus{Profile: s.profile, URL: s.base.String(), Authenticated: true, Source: s.source}
	if s.source == auth.SourceFile {
		a.warn(auth.FileWarning)
	}
	if !offline {
		verified := true
		err = a.verifyKey(ctx, s)
		if err != nil {
			verified = false
			status.Authenticated = false
			status.Message = err.Error()
		}
		status.Verified = &verified
	}
	if renderErr := a.renderAuthStatus(status); renderErr != nil {
		return renderErr
	}
	return err
}

func (a *app) renderAuthStatus(status authStatus) error {
	if a.format(output.Table) == output.JSON {
		data, err := json.Marshal(status)
		if err != nil {
			return err
		}
		_, err = fmt.Fprintf(a.stdout, "%s\n", data)
		return err
	}
	lines := fmt.Sprintf("profile: %s\nserver: %s\nkey: present (from %s)\n", status.Profile, termsafe.Line(status.URL), status.Source)
	if status.Verified != nil {
		lines += fmt.Sprintf("accepted by server: %t\n", *status.Verified)
	}
	_, err := io.WriteString(a.stdout, lines)
	return err
}
