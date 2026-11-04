package selfupdate

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"time"
)

const (
	defaultAPIBase = "https://api.github.com"
	repoSlug       = "mggarofalo/memoria"
	checksumsAsset = "checksums.txt"
	maxListBytes   = 16 << 20
	requestTimeout = 5 * time.Minute
	userAgent      = "memoria-self-update"
)

// ErrInsecureURL means a release URL or redirect was not https.
var ErrInsecureURL = errors.New("refusing a non-https release URL")

// source reads GitHub releases over https only, redirects included. It sends no
// credentials: the repository is public, and GITHUB_TOKEN in the environment is
// not this tool's to use.
type source struct {
	apiBase string
	client  *http.Client
}

func newSource() *source {
	return newSourceWith(defaultAPIBase, &http.Client{})
}

func newSourceWith(apiBase string, client *http.Client) *source {
	httpsOnly := *client
	base := client.Transport
	if base == nil {
		base = http.DefaultTransport
	}
	httpsOnly.Transport = httpsOnlyTransport{base: base}
	httpsOnly.Timeout = requestTimeout
	httpsOnly.CheckRedirect = func(req *http.Request, via []*http.Request) error {
		if req.URL.Scheme != "https" {
			return ErrInsecureURL
		}
		if len(via) >= 10 {
			return errors.New("too many redirects")
		}
		return nil
	}
	return &source{apiBase: apiBase, client: &httpsOnly}
}

// httpsOnlyTransport refuses any request that is not https, whatever path it
// took to get here.
type httpsOnlyTransport struct{ base http.RoundTripper }

func (t httpsOnlyTransport) RoundTrip(req *http.Request) (*http.Response, error) {
	if req.URL.Scheme != "https" {
		return nil, ErrInsecureURL
	}
	return t.base.RoundTrip(req)
}

type apiAsset struct {
	Name string `json:"name"`
	URL  string `json:"browser_download_url"`
}

type apiRelease struct {
	TagName    string     `json:"tag_name"`
	Draft      bool       `json:"draft"`
	Prerelease bool       `json:"prerelease"`
	Assets     []apiAsset `json:"assets"`
}

func (r *apiRelease) assetURL(name string) string {
	for _, asset := range r.Assets {
		if asset.Name == name {
			return asset.URL
		}
	}
	return ""
}

// list returns the repository's releases.
func (s *source) list(ctx context.Context) ([]apiRelease, error) {
	data, err := s.fetchAll(ctx, s.apiBase+"/repos/"+repoSlug+"/releases?per_page=100", maxListBytes)
	if err != nil {
		return nil, fmt.Errorf("listing releases: %w", err)
	}
	var releases []apiRelease
	if err := json.Unmarshal(data, &releases); err != nil {
		return nil, errors.New("listing releases: the response is not a release list")
	}
	return releases, nil
}

func (s *source) fetch(ctx context.Context, raw string) (io.ReadCloser, error) {
	u, err := url.Parse(raw)
	if err != nil || u.Scheme != "https" {
		return nil, ErrInsecureURL
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, raw, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("Accept", "application/json, application/octet-stream")
	req.Header.Set("User-Agent", userAgent)
	resp, err := s.client.Do(req)
	if err != nil {
		return nil, err
	}
	if resp.StatusCode != http.StatusOK {
		_ = resp.Body.Close()
		return nil, fmt.Errorf("unexpected status %d", resp.StatusCode)
	}
	return resp.Body, nil
}

// fetchAll reads a whole response, failing rather than truncating at limit.
func (s *source) fetchAll(ctx context.Context, url string, limit int64) ([]byte, error) {
	body, err := s.fetch(ctx, url)
	if err != nil {
		return nil, err
	}
	defer func() { _ = body.Close() }()
	data, err := io.ReadAll(io.LimitReader(body, limit+1))
	if err != nil {
		return nil, err
	}
	if int64(len(data)) > limit {
		return nil, errors.New("the response exceeds the size limit")
	}
	return data, nil
}
