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

// awsMetadataIPv6 is the instance metadata address on EC2.
var awsMetadataIPv6 = netip.MustParseAddr("fd00:ec2::254")

// Class describes how a base URL relates to the plain-http rules.
type Class int

const (
	// ClassHTTPS is any https URL.
	ClassHTTPS Class = iota
	// ClassLoopback is plain http to this machine.
	ClassLoopback
	// ClassPrivate is plain http to an RFC 1918 or ULA address.
	ClassPrivate
	// ClassHostname is plain http to a name, whose address a resolver decides.
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
	if !isASCIIHost(u) {
		return nil, apperr.New(apperr.KindUsage, "the server URL host must be plain ASCII; use its xn-- form")
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
	case addr == awsMetadataIPv6:
		// Inside the ULA range, but a key sent here goes to a metadata service.
		return ClassPublic
	case addr.IsPrivate():
		return ClassPrivate
	}
	return ClassPublic
}

// Allowance is how far the user has opted in to sending the key over plain http.
type Allowance int

const (
	// AllowNone permits https, and plain http to loopback.
	AllowNone Allowance = iota
	// AllowPrivate also permits plain http to a private address.
	AllowPrivate
	// AllowInsecure permits plain http to any address or name.
	AllowInsecure
)

// AllowanceOf is the allowance the two opt-ins grant. The broader one covers the narrower.
func AllowanceOf(insecure, private bool) Allowance {
	switch {
	case insecure:
		return AllowInsecure
	case private:
		return AllowPrivate
	}
	return AllowNone
}

// Evaluate applies the plain-http rules to a base URL. It returns a warning to
// show, if any, and an error when the key must not be sent. A private address is
// not this machine: on a network someone else runs, such as hotel Wi-Fi, the same
// address can belong to anyone, and ARP spoofing reads the key in transit. So it
// needs an opt-in too, and is never just a warning that the MCP bridge hides.
func Evaluate(u *url.URL, allow Allowance) (warning string, err error) {
	switch Classify(u) {
	case ClassHTTPS, ClassLoopback:
		return "", nil
	case ClassPrivate:
		if allow >= AllowPrivate {
			return privateOverrideWarning, nil
		}
		return "", privateRefusal()
	}
	if allow >= AllowInsecure {
		return insecureOverrideWarning, nil
	}
	return "", insecureRefusal()
}

const privateOverrideWarning = "plain http to a private address is explicitly allowed: the API key crosses the local network unencrypted, and on a network you do not run that address can be anyone's. Prefer https."

func privateRefusal() error {
	return apperr.New(apperr.KindInsecure, "refusing to send an API key over plain http to a private address, which on a network you do not run can be anyone's; use https, or pass --allow-private-http (or set MEMORIA_ALLOW_PRIVATE_HTTP=1) if this is your own network")
}

const insecureOverrideWarning = "insecure http is explicitly allowed: the API key is being sent unencrypted across a network anyone on the path can read."

func insecureRefusal() error {
	return apperr.New(apperr.KindInsecure, "refusing to send an API key over plain http to a hostname or public address; use https, or pass --allow-insecure-http if you accept that the key can be read in transit")
}

// checkLoopbackDial holds the name localhost to its meaning: a resolver that
// answers with any other address does not get the key.
func checkLoopbackDial(address string) error {
	host, _, err := net.SplitHostPort(address)
	if err != nil {
		return fmt.Errorf("unparseable dial address: %w", err)
	}
	addr, err := netip.ParseAddr(host)
	if err != nil {
		return fmt.Errorf("unparseable dial address: %w", err)
	}
	if classifyAddr(addr) != ClassLoopback {
		return insecureRefusal()
	}
	return nil
}

// Origin returns the normalized scheme://host:port of a URL. Case folds for ASCII
// only: Unicode folding would map U+0130 to i, while the network stack maps it
// through IDNA to a different name entirely.
func Origin(u *url.URL) string {
	scheme := asciiLower(u.Scheme)
	host := asciiLower(u.Hostname())
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

func isASCIIHost(u *url.URL) bool {
	host := u.Hostname()
	for i := 0; i < len(host); i++ {
		if host[i] <= 0x20 || host[i] >= 0x7f {
			return false
		}
	}
	return true
}

func asciiLower(s string) string {
	b := []byte(s)
	for i, c := range b {
		if c >= 'A' && c <= 'Z' {
			b[i] = c + ('a' - 'A')
		}
	}
	return string(b)
}

// ErrResponseTooLarge is returned when a response body exceeds the configured cap.
var ErrResponseTooLarge = errors.New("response exceeds the size limit")
