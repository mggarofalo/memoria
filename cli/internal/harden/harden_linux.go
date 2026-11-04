//go:build linux

package harden

import "golang.org/x/sys/unix"

func disableDumpable() error {
	return unix.Prctl(unix.PR_SET_DUMPABLE, 0, 0, 0, 0)
}
