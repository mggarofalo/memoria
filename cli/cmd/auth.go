package cmd

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"

	"github.com/spf13/cobra"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/auth"
	"github.com/mggarofalo/memoria/cli/internal/output"
	"github.com/mggarofalo/memoria/cli/internal/termsafe"
	"github.com/mggarofalo/memoria/cli/internal/transport"
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
	raw, err := a.loginURL(cfg, profile)
	if err != nil {
		return err
	}
	base, err := a.parseURL(raw)
	if err != nil {
		return err
	}
	if err := a.checkBackendConflict(o.backend, profile, base); err != nil {
		return err
	}
	// Say where the key is going before asking for it.
	fmt.Fprintln(a.stderr, "Logging in to "+termsafe.Line(transport.Origin(base)))
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
	return a.saveLogin(store, cfg, profile, o.backend, base, key)
}

func (a *app) saveLogin(store auth.SecretStore, cfg *auth.Config, profile, backend string, base *url.URL, key string) error {
	entry, err := auth.EncodeStoredKey(transport.Origin(base), key)
	if err != nil {
		return err
	}
	if err := store.Set(auth.KeyName(profile), entry); err != nil {
		return err
	}
	origin := transport.Origin(base)
	a.removeFromOtherBackend(backend, profile, origin)
	a.updateOriginMarker(backend, profile, origin)

	cfg.ActiveProfile = profile
	cfg.Profiles[profile] = auth.Profile{APIURL: base.String(), Backend: backend}
	if err := cfg.Save(); err != nil {
		return err
	}
	a.reportLogin(backend, profile, base.String())
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
	return nil, apperr.New(apperr.KindUsage, "unknown backend: use keyring or file")
}

// loginURL decides where a key will be sent. --api-url is always explicit, and at a
// terminal the person types the URL. Anything else comes from the environment or
// the config, and whoever sets XDG_CONFIG_HOME, HOME or MEMORIA_URL can write those.
// A piped login cannot even show a prompt to notice with. So a saved URL is used
// only when the keyring already holds a key for that origin. The credentials file
// cannot vouch, as it sits in the same directory as the config.
func (a *app) loginURL(cfg *auth.Config, profile string) (string, error) {
	if a.flags.apiURL != "" {
		return a.flags.apiURL, nil
	}
	if a.getenv(auth.EnvURL) != "" {
		a.warn(auth.EnvURL + " is ignored by 'auth login'; pass --api-url to choose the server")
	}
	saved := cfg.Profile(profile).APIURL
	if saved != "" {
		return saved, a.confirmSavedOrigin(saved, profile)
	}
	if !a.stdinIsTerminal() {
		return "", nil
	}
	fmt.Fprint(a.stderr, "Memoria server URL: ")
	line, _ := bufio.NewReader(a.stdin).ReadString('\n')
	return strings.TrimSpace(line), nil
}

func (a *app) confirmSavedOrigin(saved, profile string) error {
	parsed, err := url.Parse(saved)
	if err == nil {
		if origin, ringErr := a.recordedOrigin(profile); ringErr == nil && origin == transport.Origin(parsed) {
			return nil
		}
	}
	return apperr.New(apperr.KindInsecure,
		"nothing in the OS keyring confirms the server the config names for profile %q; pass --api-url to choose the server explicitly", profile)
}

// keyringOrigin returns the origin of the profile's key in the keyring. No keyring
// means no origin. A refused bus is an error, because a poisoned environment refuses
// its own way to the entry that would expose it.
func (a *app) keyringOrigin(profile string) (string, error) {
	return a.readKeyring(profile, auth.StoredOrigin)
}

// recordedOrigin is keyringOrigin, also counting the marker a file login leaves.
func (a *app) recordedOrigin(profile string) (string, error) {
	return a.readKeyring(profile, auth.RecordedOrigin)
}

func (a *app) readKeyring(profile string, read func(auth.SecretStore, string) string) (string, error) {
	ring, err := a.openKeyring()
	if errors.Is(err, auth.ErrBusRefused) {
		return "", apperr.Wrap(apperr.KindInsecure, err,
			"cannot check the OS keyring: "+err.Error()+". If that bus address is simply stale, unset DBUS_SESSION_BUS_ADDRESS")
	}
	if err != nil {
		return "", nil
	}
	return read(ring, profile), nil
}

// checkBackendConflict refuses a file login beside a keyring key for another server.
// Commands would refuse that pair anyway, since it is also what a poisoned config
// directory looks like, so it is better to say so here.
func (a *app) checkBackendConflict(backend, profile string, base *url.URL) error {
	if backend != auth.BackendFile {
		return nil
	}
	origin, err := a.keyringOrigin(profile)
	if err != nil {
		return err
	}
	if origin == "" || origin == transport.Origin(base) {
		return nil
	}
	return apperr.New(apperr.KindInsecure,
		"profile %q already has a key in the OS keyring for %s; use a separate --profile, or if you mean to replace it, 'memoria auth logout --include-keyring' first", profile, origin)
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
		a.warn("the server accepted this key, but it has no read access. That is normal for an admin key: it manages keys with 'memoria keys', and cannot search")
		return nil
	}
	if err := checkResponse(resp.StatusCode(), resp.HTTPResponse.Header, resp.Body); err != nil {
		return err
	}
	// A proxy's HTML page is a 200 too, and says nothing about the key.
	return requireShape(resp.JSON200 != nil && resp.JSON200.Collections != nil)
}

// removeFromOtherBackend deletes a copy left by an earlier login for the same
// server, so that the precedence order can never select a stale key. An entry for
// another server is not this login's to delete.
func (a *app) removeFromOtherBackend(backend, profile, origin string) {
	var other auth.SecretStore
	var err error
	if backend == auth.BackendFile {
		other, err = a.openKeyring()
	} else {
		other, err = auth.NewFileStore()
	}
	if err == nil && auth.StoredOrigin(other, profile) == origin {
		_ = other.Delete(auth.KeyName(profile))
	}
}

// updateOriginMarker leaves the keyring a record of a file login's server, so that a
// later poisoned config directory has something to conflict with. It is best effort:
// a machine with no keyring has nothing to protect this way.
func (a *app) updateOriginMarker(backend, profile, origin string) {
	ring, err := a.openKeyring()
	if err != nil {
		return
	}
	if backend == auth.BackendFile {
		_ = ring.Set(auth.OriginMarkerName(profile), origin)
		return
	}
	_ = ring.Delete(auth.OriginMarkerName(profile))
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
	var includeKeyring bool
	cmd := &cobra.Command{
		Use:   "logout",
		Short: "Remove the stored API key",
		Long: `Remove the profile's key from the credentials file and the OS keyring. This does
not revoke it on the server.

A keyring key for a different server than the config names is kept unless you pass
--include-keyring. A config can be replaced by whoever sets XDG_CONFIG_HOME or HOME,
and logging out under one must not destroy the real key.`,
		Args: noArgs,
		RunE: func(*cobra.Command, []string) error { return a.logout(includeKeyring) },
	}
	cmd.Flags().BoolVar(&includeKeyring, "include-keyring", false, "Also remove a keyring key for a different server than the config names")
	return cmd
}

func (a *app) logout(includeKeyring bool) error {
	cfg, err := a.loadConfig()
	if err != nil {
		return err
	}
	profile, err := a.profileName(cfg)
	if err != nil {
		return err
	}
	removed, failure := a.deleteStoredKeys(cfg, profile, includeKeyring)
	if failure != nil {
		return failure
	}
	if removed {
		a.info(fmt.Sprintf("Removed the stored key for profile %q. It stays valid on the server until revoked there.", profile))
	} else {
		a.info(fmt.Sprintf("No key removed for profile %q.", profile))
	}
	if a.getenv(auth.EnvAPIKey) != "" {
		a.warn(auth.EnvAPIKey + " is still set in this environment")
	}
	return nil
}

// deleteStoredKeys tries both stores. An absent keyring is not a failure, since the
// key may never have been there.
func (a *app) deleteStoredKeys(cfg *auth.Config, profile string, includeKeyring bool) (removed bool, failure error) {
	file, err := auth.NewFileStore()
	if err != nil {
		return false, err
	}
	removed, failure = a.deleteFrom(file, "the credentials file", auth.StoredOrigin(file, profile), profile)
	ring, err := a.openKeyring()
	if errors.Is(err, auth.ErrBusRefused) {
		a.warn("the OS keyring was not touched: " + err.Error())
	}
	if err != nil {
		return removed, failure
	}
	ringRemoved, ringFailure := a.deleteFromKeyring(ring, cfg, profile, includeKeyring)
	return removed || ringRemoved, errors.Join(failure, ringFailure)
}

func (a *app) deleteFromKeyring(ring auth.SecretStore, cfg *auth.Config, profile string, includeKeyring bool) (bool, error) {
	origin := auth.RecordedOrigin(ring, profile)
	if !includeKeyring && origin != "" && origin != configOrigin(cfg, profile) {
		a.warn(fmt.Sprintf("kept the OS keyring's key for %s, because the config names a different server; pass --include-keyring to remove it", origin))
		return false, nil
	}
	_ = ring.Delete(auth.OriginMarkerName(profile))
	return a.deleteFrom(ring, "the OS keyring", auth.StoredOrigin(ring, profile), profile)
}

func (a *app) deleteFrom(store auth.SecretStore, where, origin, profile string) (bool, error) {
	err := store.Delete(auth.KeyName(profile))
	if errors.Is(err, auth.ErrNotFound) {
		return false, nil
	}
	if err != nil {
		return false, err
	}
	a.info(fmt.Sprintf("Removed the key for %s from %s.", describeOrigin(origin), where))
	return true, nil
}

func describeOrigin(origin string) string {
	if origin == "" {
		return "an unreadable entry"
	}
	return origin
}

func configOrigin(cfg *auth.Config, profile string) string {
	saved := cfg.Profile(profile).APIURL
	parsed, err := url.Parse(saved)
	if saved == "" || err != nil {
		return ""
	}
	return transport.Origin(parsed)
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
			status.Message = a.redactor.Diagnostic(termsafe.Line(err.Error()))
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
