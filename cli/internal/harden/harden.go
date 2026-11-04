// Package harden keeps the key out of crash artifacts. A core dump holds the
// process's memory, including every copy of the key, whatever GOTRACEBACK says.
package harden

import "errors"

// Process applies what this platform offers, and best effort: a failure here
// should not stop a command, since it only removes a defence in depth.
//
// On Linux that is PR_SET_DUMPABLE=0 and RLIMIT_CORE=0, which also stops other
// processes of the same user from attaching with ptrace. On macOS and other Unix
// systems it is RLIMIT_CORE=0. Windows has no per-process equivalent for Windows
// Error Reporting dumps, so there it does nothing.
func Process() error {
	return errors.Join(disableCoreDumps(), disableDumpable())
}
