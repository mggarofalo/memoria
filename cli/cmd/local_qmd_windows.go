//go:build windows

package cmd

import (
	"context"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"syscall"
)

// localCommand runs a batch file through cmd.exe with its arguments escaped, as Go's own quoting does not cover cmd.exe, and a
// query could otherwise run as a command. npm installs qmd as a .cmd file, so this is the usual case here.
func localCommand(ctx context.Context, command string, args []string) (*exec.Cmd, error) {
	resolved, err := exec.LookPath(command)
	if err != nil {
		return nil, err
	}
	extension := strings.ToLower(filepath.Ext(resolved))
	if extension != ".cmd" && extension != ".bat" {
		return exec.CommandContext(ctx, resolved, args...), nil
	}
	shell := os.Getenv("ComSpec")
	if shell == "" {
		shell = "cmd.exe"
	}
	run := exec.CommandContext(ctx, shell)
	run.SysProcAttr = &syscall.SysProcAttr{CmdLine: `cmd ` + CmdLine(resolved, args)}
	return run, nil
}
