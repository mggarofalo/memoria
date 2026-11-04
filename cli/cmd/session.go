package cmd

import (
	"fmt"
	"net/http"
	"net/url"
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
	warning, err := transport.Evaluate(base, a.allowInsecure())
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
	base, err := a.parseURL(resolver.ResolveURL())
	if err != nil {
		return nil, err
	}
	resolved, err := a.resolveKey(resolver, cfg, profile, base)
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

func (a *app) resolveKey(resolver *auth.Resolver, cfg *auth.Config, profile string, base *url.URL) (auth.Resolved, error) {
	resolved, err := resolver.Resolve()
	if err != nil {
		return auth.Resolved{}, err
	}
	a.redactor.Add(resolved.Credential.Reveal())
	if err := checkStoredKeyOrigin(resolved, cfg.Profile(profile), profile, base); err != nil {
		return auth.Resolved{}, err
	}
	if resolved.Source == auth.SourceEnv && a.stdinIsTerminal() && !a.flags.quiet {
		a.warn("using " + auth.EnvAPIKey + " from the environment; child processes inherit it. Consider 'memoria auth login'")
	}
	return resolved, nil
}

// checkStoredKeyOrigin binds a stored key to the server it was saved for.
// Otherwise a stray --api-url, or a MEMORIA_URL set by a project's .env, would
// quietly hand the stored key to whatever server it named.
func checkStoredKeyOrigin(resolved auth.Resolved, profile auth.Profile, name string, base *url.URL) error {
	if !resolved.Stored() {
		return nil
	}
	saved, err := url.Parse(profile.APIURL)
	if err != nil || profile.APIURL == "" || !transport.SameOrigin(saved, base) {
		return apperr.New(apperr.KindInsecure,
			"refusing to send the key stored for profile %q to %s, which is not the server it was saved for; run 'memoria auth login --api-url <url>' for that server, or use a separate --profile",
			name, transport.Origin(base))
	}
	return nil
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
	return &session{base: base, http: httpClient, client: client, endpoint: base.String()}, nil
}

func (a *app) httpClient(base *url.URL, key string, timeout time.Duration) (*http.Client, error) {
	options := transport.Options{
		BaseURL:           base,
		Key:               key,
		AllowInsecureHTTP: a.allowInsecure(),
		Timeout:           timeout,
		UserAgent:         "memoria-cli/" + Version,
	}
	if a.flags.verbose {
		options.Verbose = a.stderr
	}
	return transport.NewClient(options)
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
