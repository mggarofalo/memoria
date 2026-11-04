// Package transport is the only code that puts an API key on the wire. It
// decides whether a URL may receive a key at all, attaches the key to requests
// for the configured origin and no other, refuses cross-origin redirects, and
// bounds response sizes.
package transport

import (
	"errors"
	"fmt"
	"net"
	"net/netip"
	"net/url"
	"strings"

	"github.com/mggarofalo/memoria/cli/internal/apperr"
)

// Class describes how a base URL relates to the plain-http rules.
type Class int

const (
	// ClassHTTPS is any https URL.
	ClassHTTPS Class = iota
	// ClassLoopback is plain http to this machine.
	ClassLoopback
	// ClassPrivate is plain http to an RFC 1918 or ULA address.
	ClassPrivate
	// ClassHostname is plain http to a name. Its address is unknown until the
	// connection is made, so the rule is enforced at dial time.
	ClassHostname
	// ClassPublic is plain http to any other IP literal.
	ClassPublic
)

// ParseBaseURL validates a server URL. Errors never repeat the input, because
// the classic mistake here is a URL with the credential embedded in it.
func ParseBaseURL(raw string) (*url.URL, error) {
	u, err := url.Parse(strings.TrimRight(strings.TrimSpace(raw), "/"))
	if err != nil {
		return nil, apperr.New(apperr.KindUsage, "the server URL is not a valid URL")
	}
	if u.Scheme != "http" && u.Scheme != "https" {
		return nil, apperr.New(apperr.KindUsage, "the server URL must start with https:// (or http:// where allowed)")
	}
	if u.Hostname() == "" {
		return nil, apperr.New(apperr.KindUsage, "the server URL has no host")
	}
	if u.User != nil {
		return nil, apperr.New(apperr.KindUsage, "the server URL must not contain credentials; use 'memoria auth login'")
	}
	if u.RawQuery != "" || u.ForceQuery || u.Fragment != "" {
		return nil, apperr.New(apperr.KindUsage, "the server URL must not contain a query or fragment")
	}
	return u, nil
}

// Classify reports the class of a parsed base URL. Only literal addresses and
// the name localhost are trusted without a connection: names such as
// localhost.example.com or 10.0.0.1.example.com are ordinary hostnames, and so
// are integer forms like 2130706433 that some resolvers would treat as an IP.
func Classify(u *url.URL) Class {
	if u.Scheme == "https" {
		return ClassHTTPS
	}
	host := strings.ToLower(strings.TrimSuffix(u.Hostname(), "."))
	if host == "localhost" {
		return ClassLoopback
	}
	addr, err := netip.ParseAddr(host)
	if err != nil {
		return ClassHostname
	}
	return classifyAddr(addr)
}

func classifyAddr(addr netip.Addr) Class {
	addr = addr.Unmap().WithZone("")
	switch {
	case addr.IsLoopback():
		return ClassLoopback
	case addr.IsPrivate():
		return ClassPrivate
	}
	return ClassPublic
}

// Evaluate applies the plain-http rules to a base URL. It returns a warning to
// show, if any, and an error when the key must not be sent.
func Evaluate(u *url.URL, allowInsecure bool) (warning string, err error) {
	class := Classify(u)
	switch class {
	case ClassHTTPS, ClassLoopback:
		return "", nil
	case ClassPrivate:
		return "plain http to a private address: the API key crosses the local network unencrypted. Prefer https.", nil
	case ClassHostname:
		if allowInsecure {
			return insecureOverrideWarning, nil
		}
		return "plain http to a hostname: the API key crosses the network unencrypted, and the connection is refused unless the name resolves to a private address. Prefer https.", nil
	}
	if allowInsecure {
		return insecureOverrideWarning, nil
	}
	return "", insecureRefusal()
}

const insecureOverrideWarning = "insecure http is explicitly allowed: the API key is being sent unencrypted across a network anyone on the path can read."

func insecureRefusal() error {
	return apperr.New(apperr.KindInsecure, "refusing to send an API key over plain http to a non-private address; use https, or pass --allow-insecure-http if you accept that the key can be read in transit")
}

// checkDialAddress enforces the plain-http rule on the address actually being
// connected to, so a hostname cannot resolve its way around it.
func checkDialAddress(address string) error {
	host, _, err := net.SplitHostPort(address)
	if err != nil {
		return fmt.Errorf("unparseable dial address: %w", err)
	}
	addr, err := netip.ParseAddr(host)
	if err != nil {
		return fmt.Errorf("unparseable dial address: %w", err)
	}
	if classifyAddr(addr) == ClassPublic {
		return insecureRefusal()
	}
	return nil
}

// Origin returns the normalized scheme://host:port of a URL.
func Origin(u *url.URL) string {
	scheme := strings.ToLower(u.Scheme)
	host := strings.ToLower(u.Hostname())
	port := u.Port()
	if port == "" {
		switch scheme {
		case "http":
			port = "80"
		case "https":
			port = "443"
		}
	}
	return scheme + "://" + net.JoinHostPort(host, port)
}

// SameOrigin compares scheme, host and port. Unlike net/http's own redirect
// rule it treats a subdomain and an https-to-http downgrade as different.
func SameOrigin(a, b *url.URL) bool {
	return Origin(a) == Origin(b)
}

// ErrResponseTooLarge is returned when a response body exceeds the configured cap.
var ErrResponseTooLarge = errors.New("response exceeds the size limit")
