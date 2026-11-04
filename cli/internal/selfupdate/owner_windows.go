//go:build windows

package selfupdate

// checkOwnedByCurrentUser does nothing on Windows: a binary this user may not
// replace fails at the rename, with nothing changed.
func checkOwnedByCurrentUser(string) error { return nil }
