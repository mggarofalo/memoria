//go:build !windows

package auth

import (
	"fmt"
	"os"
	"syscall"
)

// ensurePrivateDir creates dir with mode 0700 and tightens it if it already
// exists. The directory is this CLI's own, so tightening is safe.
func ensurePrivateDir(dir string) error {
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return err
	}
	return os.Chmod(dir, 0o700)
}

// protectFile restricts a file to its owner.
func protectFile(path string) error {
	return os.Chmod(path, 0o600)
}

// verifyPrivate fails with ErrInsecurePermissions unless path is owned by the
// current user and has no group or other access. It uses Lstat so a symlink,
// which could point anywhere, is rejected rather than followed.
func verifyPrivate(path string) error {
	info, err := os.Lstat(path)
	if err != nil {
		return err
	}
	if !info.Mode().IsRegular() {
		return fmt.Errorf("%w: not a regular file", ErrInsecurePermissions)
	}
	if info.Mode().Perm()&0o077 != 0 {
		return fmt.Errorf("%w: mode %o", ErrInsecurePermissions, info.Mode().Perm())
	}
	if stat, ok := info.Sys().(*syscall.Stat_t); ok && int(stat.Uid) != os.Getuid() {
		return fmt.Errorf("%w: owned by another user", ErrInsecurePermissions)
	}
	return nil
}

func permissionFixHint(path string) string {
	return "fix it with: chmod 600 " + path
}
