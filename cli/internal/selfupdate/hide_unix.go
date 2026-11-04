//go:build !windows

package selfupdate

// hideFile does nothing: the leading dot already hides the backup, and Unix can
// delete a running binary, so a leftover here is rare.
func hideFile(string) {}
