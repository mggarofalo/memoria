//go:build unix

package selfupdate

import (
	"errors"
	"fmt"
	"os"
	"syscall"
)

func checkOwnedByCurrentUser(path string) error {
	info, err := os.Stat(path)
	if err != nil {
		return err
	}
	stat, ok := info.Sys().(*syscall.Stat_t)
	if !ok {
		return errors.New("cannot read the binary's owner")
	}
	if int(stat.Uid) != os.Geteuid() {
		return fmt.Errorf("%s is owned by another user", path)
	}
	return nil
}
