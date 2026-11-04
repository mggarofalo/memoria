package transport

import (
	"context"
	"crypto/tls"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"syscall"
	"time"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
	"github.com/mggarofalo/memoria/cli/internal/termsafe"
)

// DefaultMaxResponseBytes caps a REST response. The generated client reads
// bodies whole, so without this a hostile server could exhaust memory.
const DefaultMaxResponseBytes int64 = 256 << 20

const maxRedirects = 5

// Options configures NewClient.
type Options struct {
	BaseURL *url.URL
	// Key is the bearer token. It is attached only to requests for BaseURL's origin.
	Key string
	// AllowInsecureHTTP is the explicit override for the plain-http refusal.
	AllowInsecureHTTP bool
	// Timeout bounds a whole request including the body. Zero means no limit,
	// for callers that set a deadline per request.
	Timeout time.Duration
	// MaxResponseBytes caps each response. Zero uses DefaultMaxResponseBytes.
	MaxResponseBytes int64
	// Verbose, when set, receives one line per request and response. Header
	// values are never logged.
	Verbose io.Writer
	// UserAgent is sent on every request.
	UserAgent string
}

// NewClient builds the HTTP client for a Memoria server. It returns an error
// when the base URL may not receive a key. Call Evaluate first to obtain any
// warning to show the user.
func NewClient(o Options) (*http.Client, error) {
	if o.BaseURL == nil {
		return nil, errors.New("transport: no base URL")
	}
	if _, err := Evaluate(o.BaseURL, o.AllowInsecureHTTP); err != nil {
		return nil, err
	}
	base := newBaseTransport(o.BaseURL, o.AllowInsecureHTTP)
	return newClientWithBase(o, base), nil
}

func newClientWithBase(o Options, base http.RoundTripper) *http.Client {
	limit := o.MaxResponseBytes
	if limit <= 0 {
		limit = DefaultMaxResponseBytes
	}
	return &http.Client{
		Timeout: o.Timeout,
		Transport: &guard{
			base:      base,
			origin:    o.BaseURL,
			key:       o.Key,
			limit:     limit,
			verbose:   o.Verbose,
			userAgent: o.UserAgent,
		},
		CheckRedirect: func(req *http.Request, via []*http.Request) error {
			if !SameOrigin(req.URL, o.BaseURL) {
				return apperr.New(apperr.KindInsecure, "refusing to follow a redirect from %s to a different origin, %s",
					Origin(o.BaseURL), Origin(req.URL))
			}
			if len(via) >= maxRedirects {
				return fmt.Errorf("stopped after %d redirects", maxRedirects)
			}
			return nil
		},
	}
}

func newBaseTransport(u *url.URL, allowInsecure bool) *http.Transport {
	t := http.DefaultTransport.(*http.Transport).Clone()
	t.TLSClientConfig = &tls.Config{MinVersion: tls.VersionTLS12}
	// An http proxy would see a plain-http request, bearer key included. It
	// would also defeat the dial check below by connecting to the proxy
	// instead of the server. So only https goes through a configured proxy,
	// where the key stays inside the tunnel.
	t.Proxy = func(req *http.Request) (*url.URL, error) {
		if req.URL.Scheme != "https" {
			return nil, nil
		}
		return http.ProxyFromEnvironment(req)
	}
	if u.Scheme == "http" && !allowInsecure {
		dialer := &net.Dialer{
			Timeout:   30 * time.Second,
			KeepAlive: 30 * time.Second,
			Control: func(_, address string, _ syscall.RawConn) error {
				return checkDialAddress(address)
			},
		}
		t.DialContext = dialer.DialContext
	}
	return t
}

// guard attaches the key and enforces limits. Injecting the key here, rather
// than in a request editor, means a request for any other origin cannot carry
// it, whatever the redirect logic did.
type guard struct {
	base      http.RoundTripper
	origin    *url.URL
	key       string
	limit     int64
	verbose   io.Writer
	userAgent string
}

func (g *guard) RoundTrip(req *http.Request) (*http.Response, error) {
	out := req.Clone(req.Context())
	out.Header.Del("Authorization")
	if g.key != "" && SameOrigin(req.URL, g.origin) {
		out.Header.Set("Authorization", "Bearer "+g.key)
	}
	if g.userAgent != "" {
		out.Header.Set("User-Agent", g.userAgent)
	}

	start := time.Now()
	if g.verbose != nil {
		fmt.Fprintf(g.verbose, "> %s %s\n", req.Method, req.URL.RequestURI())
	}
	resp, err := g.base.RoundTrip(out)
	if err != nil {
		if g.verbose != nil && !errors.Is(err, context.Canceled) {
			fmt.Fprintf(g.verbose, "< failed after %s\n", time.Since(start).Round(time.Millisecond))
		}
		return nil, classifyDialRefusal(err)
	}
	if g.verbose != nil {
		fmt.Fprintf(g.verbose, "< %d in %s trace=%s\n", resp.StatusCode, time.Since(start).Round(time.Millisecond), termsafe.Line(resp.Header.Get("X-Trace-Id")))
	}
	resp.Body = &limitedBody{ReadCloser: resp.Body, remaining: g.limit}
	return resp, nil
}

// classifyDialRefusal surfaces the plain-http refusal from inside a net.OpError.
func classifyDialRefusal(err error) error {
	var e *apperr.Error
	if errors.As(err, &e) {
		return e
	}
	return err
}

// limitedBody fails the read rather than truncating, so a partial JSON document
// can never be mistaken for a complete one.
type limitedBody struct {
	io.ReadCloser
	remaining int64
}

func (b *limitedBody) Read(p []byte) (int, error) {
	if b.remaining < 0 {
		return 0, ErrResponseTooLarge
	}
	if int64(len(p)) > b.remaining+1 {
		p = p[:b.remaining+1]
	}
	n, err := b.ReadCloser.Read(p)
	b.remaining -= int64(n)
	if b.remaining < 0 {
		return 0, ErrResponseTooLarge
	}
	return n, err
}
