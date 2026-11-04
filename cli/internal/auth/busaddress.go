package auth

import (
	"errors"
	"strings"
)

// busSocketPath accepts only a single unix:path= bus address, with an optional
// guid. Anything else is refused: tcp: can be remote, abstract sockets have no
// owner to check, a list of addresses offers fallbacks nothing has checked, and
// escapes could make the checked path differ from the one connected to.
func busSocketPath(address string) (string, error) {
	rest, ok := strings.CutPrefix(address, "unix:")
	if !ok || strings.ContainsAny(address, ";%") {
		return "", errors.New("the session bus must be a unix:path address; DBUS_SESSION_BUS_ADDRESS is not")
	}
	path := ""
	for _, part := range strings.Split(rest, ",") {
		name, value, _ := strings.Cut(part, "=")
		switch name {
		case "path":
			path = value
		case "guid":
		default:
			return "", errors.New("the session bus must be a unix:path address; DBUS_SESSION_BUS_ADDRESS is not")
		}
	}
	if !strings.HasPrefix(path, "/") {
		return "", errors.New("the session bus must be a unix:path address; DBUS_SESSION_BUS_ADDRESS is not")
	}
	return path, nil
}
