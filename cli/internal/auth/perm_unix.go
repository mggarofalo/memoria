//go:build !windows

package auth

import (
	"fmt"
	"os"
	"path/filepath"
	"syscall"
)

// ensurePrivateDir creates dir with mode 0700 and tightens it if it already
// exists. The directory is this CLI's own, so tightening is safe.
func ensurePrivateDir(dir string) error {
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return err
	}
	return os.Chmod(dir, 0o700) //nolint:gosec // G302: a directory needs its execute bit; 0700 is owner-only
}

// protectFile restricts a file to its owner.
func protectFile(path string) error {
	return os.Chmod(path, 0o600)
}

// verifyPrivate fails with ErrInsecurePermissions unless path and its directory
// are this user's alone. It uses Lstat so a symlink, which could point anywhere,
// is rejected rather than followed. A hard link is rejected too, since it can
// expose the file under another path and permissions-checked directory.
func verifyPrivate(path string) error {
	info, err := os.Lstat(path)
	if err != nil {
		return err
	}
	if !info.Mode().IsRegular() {
		return fmt.Errorf("%w: not a regular file", ErrInsecurePermissions)
	}
	if err := checkOwnerOnly(info); err != nil {
		return err
	}
	if stat, ok := info.Sys().(*syscall.Stat_t); ok && stat.Nlink > 1 {
		return fmt.Errorf("%w: it has more than one hard link", ErrInsecurePermissions)
	}
	dir, err := os.Lstat(filepath.Dir(path))
	if err != nil {
		return err
	}
	return checkOwnerOnly(dir)
}

func checkOwnerOnly(info os.FileInfo) error {
	if info.Mode().Perm()&0o077 != 0 {
		return fmt.Errorf("%w: %s has mode %o", ErrInsecurePermissions, info.Name(), info.Mode().Perm())
	}
	if stat, ok := info.Sys().(*syscall.Stat_t); ok && int(stat.Uid) != os.Getuid() {
		return fmt.Errorf("%w: %s is owned by another user", ErrInsecurePermissions, info.Name())
	}
	return nil
}

func permissionFixHint(path string) string {
	return "fix it with: chmod 700 " + filepath.Dir(path) + " && chmod 600 " + path
}
