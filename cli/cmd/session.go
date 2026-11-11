package cmd

import (
	"bytes"
	"context"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"

	"github.com/mggarofalo/memoria/cli/internal/api"
	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/auth"
	"github.com/mggarofalo/memoria/cli/internal/output"
	"github.com/mggarofalo/memoria/cli/internal/transport"
)

// session is a resolved server, key and guarded client.
type session struct {
	base     *url.URL
	keyID    string // The public id of the key in use, when it has the usual form.
	profile  string
	source   string
	http     *http.Client
	client   *api.ClientWithResponses
	endpoint string
}

func (a *app) loadConfig() (*auth.Config, error) {
	cfg, err := auth.LoadConfig()
	if err != nil {
		return nil, err
	}
	if cfg.HasStraySecret {
		a.warn("config.json contains a field that looks like a key. It is ignored; remove it, and rotate that key if the file was ever shared")
	}
	return cfg, nil
}

func (a *app) profileName(cfg *auth.Config) (string, error) {
	name := a.flags.profile
	if name == "" {
		name = a.getenv(auth.EnvProfile)
	}
	if name == "" {
		name = cfg.ActiveProfile
	}
	if err := auth.ValidateProfileName(name); err != nil {
		return "", apperr.New(apperr.KindUsage, "invalid profile: %s", err.Error())
	}
	return name, nil
}

func (a *app) resolver(cfg *auth.Config, profile string) (*auth.Resolver, error) {
	file, err := auth.NewFileStore()
	if err != nil {
		return nil, err
	}
	return &auth.Resolver{
		FlagKey: a.flags.apiKey,
		FlagURL: a.flags.apiURL,
		Profile: profile,
		Config:  cfg,
		Getenv:  a.getenv,
		Keyring: a.openKeyring,
		File:    file,
	}, nil
}

func (a *app) allowInsecure() bool {
	return a.flags.allowInsecure || a.getenv(auth.EnvAllowInsecureHTTP) == "1"
}

func (a *app) allowPrivate() bool {
	return a.flags.allowPrivate || a.getenv(auth.EnvAllowPrivateHTTP) == "1"
}

// plainHTTP is how far this invocation has opted in to plain http.
func (a *app) plainHTTP() transport.Allowance {
	return transport.AllowanceOf(a.allowInsecure(), a.allowPrivate())
}

// parseURL validates the server URL and applies the plain-http rules, showing
// any warning. It runs before the key is read from any store.
func (a *app) parseURL(raw string) (*url.URL, error) {
	if raw == "" {
		return nil, apperr.New(apperr.KindUsage, "no server URL: run 'memoria auth login --api-url <url>', set %s, or pass --api-url", auth.EnvURL)
	}
	base, err := transport.ParseBaseURL(raw)
	if err != nil {
		return nil, err
	}
	warning, err := transport.Evaluate(base, a.plainHTTP())
	if err != nil {
		return nil, err
	}
	if warning != "" {
		a.warn(warning)
	}
	return base, nil
}

// connect resolves the URL and key and builds the client.
func (a *app) connect() (*session, error) {
	cfg, err := a.loadConfig()
	if err != nil {
		return nil, err
	}
	profile, err := a.profileName(cfg)
	if err != nil {
		return nil, err
	}
	resolver, err := a.resolver(cfg, profile)
	if err != nil {
		return nil, err
	}
	// Before the URL is even parsed, so that the config's server cannot shape the
	// answer, such as by asking for a plain-http opt-in for a server nobody chose.
	if err := checkExplicitKeyOrigin(resolver); err != nil {
		return nil, err
	}
	base, err := a.parseURL(resolver.ResolveURL())
	if err != nil {
		return nil, err
	}
	resolved, err := a.resolveKey(resolver, profile, base)
	if err != nil {
		return nil, err
	}
	s, err := a.newSession(base, resolved.Credential.Reveal())
	if err != nil {
		return nil, err
	}
	s.profile, s.source = profile, resolved.Source
	return s, nil
}

func (a *app) resolveKey(resolver *auth.Resolver, profile string, base *url.URL) (auth.Resolved, error) {
	resolved, err := resolver.Resolve()
	if err != nil {
		return auth.Resolved{}, err
	}
	a.redactor.Add(resolved.Credential.Reveal())
	if err := checkStoredKeyOrigin(resolved, profile, base); err != nil {
		return auth.Resolved{}, err
	}
	if resolved.Source == auth.SourceFile {
		if err := a.checkFileBackend(resolved, profile); err != nil {
			return auth.Resolved{}, err
		}
	}
	if resolved.Source == auth.SourceEnv && a.stdinIsTerminal() && !a.flags.quiet {
		a.warn("using " + auth.EnvAPIKey + " from the environment; everything your shell starts inherits it. Consider 'memoria auth login'")
	}
	return resolved, nil
}

// checkFileBackend covers a poisoned config directory holding its own credentials
// file: the attacker's key, for the attacker's server, would receive search text.
// A keyring entry for another server gives that away. With no keyring entry nothing
// can tell, so the origin in use is always stated.
func (a *app) checkFileBackend(resolved auth.Resolved, profile string) error {
	origin, err := a.recordedOrigin(profile)
	if err != nil {
		return err
	}
	if origin != "" && origin != resolved.Origin {
		return apperr.New(apperr.KindInsecure,
			"the credentials file for profile %q names %s, but the OS keyring knows this profile by %s; check XDG_CONFIG_HOME and HOME, as they may point at someone else's files",
			profile, resolved.Origin, origin)
	}
	// Not a.info: with nothing to compare against, this line is the only signal, so
	// --quiet must not hide it.
	a.warn("using the key from the credentials file, for " + resolved.Origin)
	return nil
}

// checkExplicitKeyOrigin holds a key from --api-key or MEMORIA_API_KEY to a server
// named the same way, by --api-url or MEMORIA_URL. Such a key is tied to no server,
// so taking the server from config.json would send it wherever that file says, and
// that file is the one a poisoned XDG_CONFIG_HOME or HOME supplies. The server the
// config names is not repeated: it is not one the user chose here.
func checkExplicitKeyOrigin(resolver *auth.Resolver) error {
	label := resolver.ExplicitKeySource()
	// With no URL anywhere, parseURL gives the plain usage error that says how to name one.
	if label == "" || resolver.ExplicitURL() || resolver.ResolveURL() == "" {
		return nil
	}
	return apperr.New(apperr.KindInsecure,
		"a key from %s is sent only to a server named the same way: pass --api-url or set %s. It is never sent to the server in config.json, which is not tied to that key",
		label, auth.EnvURL)
}

// checkStoredKeyOrigin holds a stored key to the origin recorded with it in the
// store. The config file cannot be the authority: anyone who can set
// XDG_CONFIG_HOME or HOME can supply one naming their own server.
func checkStoredKeyOrigin(resolved auth.Resolved, name string, base *url.URL) error {
	if !resolved.Stored() || resolved.Origin == transport.Origin(base) {
		return nil
	}
	return apperr.New(apperr.KindInsecure,
		"refusing to send the key stored for profile %q to %s, which is not the server it was saved for; run 'memoria auth login --api-url <url>' for that server, or use a separate --profile",
		name, transport.Origin(base))
}

func (a *app) newSession(base *url.URL, key string) (*session, error) {
	httpClient, err := a.httpClient(base, key, a.flags.timeout)
	if err != nil {
		return nil, err
	}
	client, err := api.NewClientWithResponses(base.String()+"/", api.WithHTTPClient(httpClient))
	if err != nil {
		return nil, fmt.Errorf("creating the API client: %w", err)
	}
	return &session{base: base, keyID: publicKeyID(key), http: httpClient, client: client, endpoint: base.String()}, nil
}

// publicKeyID reads the id from mem_<id>_<secret>. The id is public by design. Any
// other form has none, and the key stays opaque.
func publicKeyID(key string) string {
	parts := strings.Split(key, "_")
	if len(parts) == 3 && parts[0] == "mem" {
		return parts[1]
	}
	return ""
}

func (a *app) httpClient(base *url.URL, key string, timeout time.Duration) (*http.Client, error) {
	options := transport.Options{
		BaseURL:           base,
		Key:               key,
		AllowInsecureHTTP: a.allowInsecure(),
		AllowPrivateHTTP:  a.allowPrivate(),
		Timeout:           timeout,
		UserAgent:         "memoria-cli/" + Version,
	}
	if a.flags.verbose {
		options.Verbose = a.stderr
	}
	return transport.NewClient(options)
}

// present writes a result in the chosen format. Rendering a huge hostile document
// happens off to the side, so that the context still ends the command on time.
// JSON is the response itself and needs no rendering.
func (a *app) present(ctx context.Context, fallback output.Format, raw []byte, renderers map[output.Format]func(io.Writer) error) error {
	format := a.format(fallback)
	if format == output.JSON {
		return a.writeRaw(raw)
	}
	rendered, err := a.renderWithin(ctx, renderers[format])
	if err != nil {
		return err
	}
	_, err = a.stdout.Write(rendered)
	return err
}

func (a *app) renderWithin(ctx context.Context, render func(io.Writer) error) ([]byte, error) {
	ctx, cancel := a.renderContext(ctx)
	defer cancel()
	type outcome struct {
		data []byte
		err  error
	}
	done := make(chan outcome, 1)
	go func() {
		var buf bytes.Buffer
		err := render(&buf)
		done <- outcome{buf.Bytes(), err}
	}()
	select {
	case result := <-done:
		return result.data, result.err
	case <-ctx.Done():
		return nil, apperr.Wrap(apperr.KindGeneric, ctx.Err(), "stopped while rendering the result")
	}
}

func (a *app) renderContext(ctx context.Context) (context.Context, context.CancelFunc) {
	if a.flags.timeout <= 0 {
		return context.WithCancel(ctx)
	}
	return context.WithTimeout(ctx, a.flags.timeout)
}

// writeRaw emits the response body byte for byte. The one exception is a response
// holding the key in use, even in an escaped form, which is scrubbed.
func (a *app) writeRaw(body []byte) error {
	scrubbed, err := a.redactor.JSON(body, "")
	if err != nil {
		scrubbed = body
	}
	_, err = a.stdout.Write(scrubbed)
	return err
}

// format returns the chosen output format, or the command's default.
func (a *app) format(fallback output.Format) output.Format {
	// prepare has already validated the flag.
	format, _ := output.ParseFormat(a.flags.format, fallback)
	return format
}

// checkResponse turns a non-200 response into a classified error.
func checkResponse(status int, header http.Header, body []byte) error {
	if status == http.StatusOK {
		return nil
	}
	return apperr.FromResponse(status, header, body)
}

var errBadResponse = apperr.New(apperr.KindGeneric, "the server returned a success response this client cannot read")

// requireShape refuses a 200 whose body did not parse into what the endpoint
// promises. An HTML page from a proxy, null, and {} all parse to nothing useful,
// and treating them as success would report a key as accepted.
func requireShape(present bool) error {
	if !present {
		return errBadResponse
	}
	return nil
}
