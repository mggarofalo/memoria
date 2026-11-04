package selfupdate

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"sync"
	"time"

	lib "github.com/creativeprojects/go-selfupdate"
)

const (
	defaultAPIBase = "https://api.github.com"
	repoSlug       = "mggarofalo/memoria"
	checksumsAsset = "checksums.txt"
	// maxAssetBytes bounds a download, which the library reads into memory.
	maxAssetBytes  = 256 << 20
	maxListBytes   = 16 << 20
	requestTimeout = 5 * time.Minute
)

// ErrInsecureURL means a release URL or redirect was not https.
var ErrInsecureURL = errors.New("refusing a non-https release URL")

// source implements the library's Source over GitHub's REST API. The library's
// own GitHub source downloads through http.DefaultClient, which would follow a
// redirect to plain http, and reads GITHUB_TOKEN from the environment. This one
// uses an https-only client and sends no credentials, since the repository is
// public.
type source struct {
	apiBase string
	client  *http.Client

	mu     sync.Mutex
	assets map[int64]string
}

// newSource returns a source for the real GitHub API.
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
	return &source{apiBase: apiBase, client: &httpsOnly, assets: map[int64]string{}}
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
	ID   int64  `json:"id"`
	Name string `json:"name"`
	Size int    `json:"size"`
	URL  string `json:"browser_download_url"`
}

type apiRelease struct {
	ID         int64      `json:"id"`
	TagName    string     `json:"tag_name"`
	Name       string     `json:"name"`
	Draft      bool       `json:"draft"`
	Prerelease bool       `json:"prerelease"`
	HTMLURL    string     `json:"html_url"`
	Body       string     `json:"body"`
	Published  time.Time  `json:"published_at"`
	Assets     []apiAsset `json:"assets"`
}

// ListReleases implements lib.Source. It keeps published releases with a
// well-formed tag, and only the assets the release contract defines, so that
// nothing else in a release can be selected by a loose suffix match.
func (s *source) ListReleases(ctx context.Context, _ lib.Repository) ([]lib.SourceRelease, error) {
	body, err := s.fetch(ctx, s.apiBase+"/repos/"+repoSlug+"/releases?per_page=100")
	if err != nil {
		return nil, fmt.Errorf("listing releases: %w", err)
	}
	defer func() { _ = body.Close() }()

	var releases []apiRelease
	if err := json.NewDecoder(io.LimitReader(body, maxListBytes)).Decode(&releases); err != nil {
		return nil, fmt.Errorf("listing releases: %w", err)
	}
	return s.adopt(releases), nil
}

func (s *source) adopt(releases []apiRelease) []lib.SourceRelease {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]lib.SourceRelease, 0, len(releases))
	for i := range releases {
		rel := &releases[i]
		if rel.Draft || !ValidTag(rel.TagName) {
			continue
		}
		for _, asset := range rel.Assets {
			s.assets[asset.ID] = asset.URL
		}
		out = append(out, rel)
	}
	return out
}

// DownloadReleaseAsset implements lib.Source.
func (s *source) DownloadReleaseAsset(ctx context.Context, _ *lib.Release, assetID int64) (io.ReadCloser, error) {
	s.mu.Lock()
	raw, ok := s.assets[assetID]
	s.mu.Unlock()
	if !ok {
		return nil, fmt.Errorf("unknown release asset %d", assetID)
	}
	body, err := s.fetch(ctx, raw)
	if err != nil {
		return nil, fmt.Errorf("downloading a release asset: %w", err)
	}
	return &cappedBody{ReadCloser: body, remaining: maxAssetBytes}, nil
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
	req.Header.Set("User-Agent", "memoria-self-update")
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

// cappedBody fails rather than truncating an oversized download.
type cappedBody struct {
	io.ReadCloser
	remaining int64
}

func (b *cappedBody) Read(p []byte) (int, error) {
	n, err := b.ReadCloser.Read(p)
	b.remaining -= int64(n)
	if b.remaining < 0 {
		return 0, errors.New("release asset exceeds the size limit")
	}
	return n, err
}

func (r *apiRelease) GetID() int64              { return r.ID }
func (r *apiRelease) GetTagName() string        { return r.TagName }
func (r *apiRelease) GetDraft() bool            { return r.Draft }
func (r *apiRelease) GetPrerelease() bool       { return r.Prerelease }
func (r *apiRelease) GetPublishedAt() time.Time { return r.Published }
func (r *apiRelease) GetReleaseNotes() string   { return r.Body }
func (r *apiRelease) GetName() string           { return r.Name }
func (r *apiRelease) GetURL() string            { return r.HTMLURL }

// GetAssets returns this release's archives and checksums, named exactly as the
// release contract says.
func (r *apiRelease) GetAssets() []lib.SourceAsset {
	out := make([]lib.SourceAsset, 0, len(r.Assets))
	version := r.TagName[1:]
	for i := range r.Assets {
		if isContractAsset(r.Assets[i].Name, version) {
			out = append(out, &r.Assets[i])
		}
	}
	return out
}

func (a *apiAsset) GetID() int64                  { return a.ID }
func (a *apiAsset) GetName() string               { return a.Name }
func (a *apiAsset) GetSize() int                  { return a.Size }
func (a *apiAsset) GetBrowserDownloadURL() string { return a.URL }
