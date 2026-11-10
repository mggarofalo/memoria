//go:build linux

package auth

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"syscall"
	"time"

	dbus "github.com/godbus/dbus/v5"
)

const (
	secretServiceName = "org.freedesktop.secrets"
	busCallTimeout    = 25 * time.Second
)

// sessionBusCheck runs before anything talks to the Secret Service. It is a
// variable so that tests of the store above it can use go-keyring's mock.
var sessionBusCheck = checkSessionBus

// checkSessionBus decides whether the session bus is really this user's. Whoever
// owns the bus owns the Secret Service on it, so DBUS_SESSION_BUS_ADDRESS, set by a
// project's .env for example, could otherwise receive a key at login. godbus would
// also accept tcp:, or run a dbus-launch found through PATH.
func checkSessionBus() error {
	path, err := busSocketFromEnvironment()
	if err != nil {
		return err
	}
	if err := verifyBusSocket(path, runtimeDirectories(os.Getuid()), os.Getuid()); err != nil {
		return fmt.Errorf("%w: %w", ErrBusRefused, err)
	}
	// Pin what was checked, so godbus has nothing left to discover or launch.
	if err := os.Setenv("DBUS_SESSION_BUS_ADDRESS", "unix:path="+path); err != nil {
		return err
	}
	conn, err := dbus.SessionBus()
	if err != nil {
		return fmt.Errorf("cannot connect to the session bus: %w", err)
	}
	return verifySecretsOwner(conn, uint32(os.Getuid())) //nolint:gosec // G115: a uid is a uint32 on Linux, so the conversion cannot overflow
}

// busSocketFromEnvironment finds the bus to check. An address that is set and cannot
// be used is a refusal. With no address, the first bus found in a runtime directory
// is the one to check, so a bad XDG_RUNTIME_DIR cannot hide a real bus in
// /run/user/<uid>. Finding no bus at all is the one case that means "no keyring".
func busSocketFromEnvironment() (string, error) {
	if address := os.Getenv("DBUS_SESSION_BUS_ADDRESS"); address != "" {
		path, err := busSocketPath(address)
		if err != nil {
			return "", fmt.Errorf("%w: %w", ErrBusRefused, err)
		}
		return path, nil
	}
	for _, dir := range runtimeDirectories(os.Getuid()) {
		path := filepath.Join(dir, "bus")
		if _, err := os.Lstat(path); err == nil {
			return path, nil
		}
	}
	return "", errors.New("no session bus was found")
}

// runtimeDirectories lists where this user's bus may live, the verified
// XDG_RUNTIME_DIR first. verifyBusSocket checks ownership of whichever is used.
func runtimeDirectories(uid int) []string {
	fixed := "/run/user/" + strconv.Itoa(uid)
	if dir := os.Getenv("XDG_RUNTIME_DIR"); filepath.IsAbs(dir) && dir != fixed {
		return []string{filepath.Clean(dir), fixed}
	}
	return []string{fixed}
}

// verifyBusSocket requires a socket owned by uid, with every directory from it up
// to a runtime directory real, owned by uid, and the runtime directory private.
func verifyBusSocket(path string, runtimeDirs []string, uid int) error {
	root := enclosingDirectory(path, runtimeDirs)
	if root == "" {
		return fmt.Errorf("the session bus at %s is outside the user's runtime directory", path)
	}
	if err := verifyBusSocketFile(path, uid); err != nil {
		return err
	}
	return verifyBusDirectories(filepath.Dir(path), root, uid)
}

func verifyBusSocketFile(path string, uid int) error {
	socket, err := os.Lstat(path)
	if err != nil {
		return fmt.Errorf("no session bus at %s: %w", path, err)
	}
	if socket.Mode()&os.ModeSocket == 0 || !ownedBy(socket, uid) {
		return fmt.Errorf("%s is not a socket owned by this user", path)
	}
	return nil
}

func enclosingDirectory(path string, dirs []string) string {
	if filepath.Clean(path) != path {
		return ""
	}
	for _, dir := range dirs {
		if strings.HasPrefix(path, dir+"/") {
			return dir
		}
	}
	return ""
}

func verifyBusDirectories(dir, root string, uid int) error {
	for ; ; dir = filepath.Dir(dir) {
		info, err := os.Lstat(dir)
		if err != nil {
			return fmt.Errorf("cannot check %s: %w", dir, err)
		}
		if !info.IsDir() || !ownedBy(info, uid) {
			return fmt.Errorf("%s is not a directory owned by this user", dir)
		}
		if dir == root {
			if info.Mode().Perm()&0o077 != 0 {
				return fmt.Errorf("the runtime directory %s is open to other users", dir)
			}
			return nil
		}
	}
}

func ownedBy(info os.FileInfo, uid int) bool {
	stat, ok := info.Sys().(*syscall.Stat_t)
	return ok && int(stat.Uid) == uid
}

// verifySecretsOwner asks the bus who holds the Secret Service name. Another user
// holding it would receive every secret stored through it.
func verifySecretsOwner(conn *dbus.Conn, uid uint32) error {
	owner, err := secretsOwner(conn)
	if err != nil {
		return err
	}
	if owner != uid {
		return fmt.Errorf("%w: the Secret Service on this bus belongs to uid %d, not this user", ErrBusRefused, owner)
	}
	return nil
}

func secretsOwner(conn *dbus.Conn) (uint32, error) {
	ctx, cancel := context.WithTimeout(context.Background(), busCallTimeout)
	defer cancel()
	uid, err := lookupOwner(ctx, conn)
	if err == nil {
		return uid, nil
	}
	var dbusErr dbus.Error
	if !errors.As(err, &dbusErr) {
		return 0, fmt.Errorf("cannot find the Secret Service: %w", err)
	}
	// Nothing holds the name yet: a keyring can be started on demand.
	var started uint32
	call := conn.BusObject().CallWithContext(ctx, "org.freedesktop.DBus.StartServiceByName", 0, secretServiceName, uint32(0))
	if err := call.Store(&started); err != nil {
		return 0, fmt.Errorf("no Secret Service is running: %w", err)
	}
	return lookupOwner(ctx, conn)
}

func lookupOwner(ctx context.Context, conn *dbus.Conn) (uint32, error) {
	var uid uint32
	call := conn.BusObject().CallWithContext(ctx, "org.freedesktop.DBus.GetConnectionUnixUser", 0, secretServiceName)
	return uid, call.Store(&uid)
}
