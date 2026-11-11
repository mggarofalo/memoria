//go:build !windows

package cmd

import (
	"context"
	"os/exec"
)

// localCommand needs nothing special here: no shell is involved, so no argument can be interpreted.
func localCommand(ctx context.Context, command string, args []string) (*exec.Cmd, error) {
	return exec.CommandContext(ctx, command, args...), nil //nolint:gosec // G204: local QMD, found on PATH, is what verify compares with; no shell is involved
}
