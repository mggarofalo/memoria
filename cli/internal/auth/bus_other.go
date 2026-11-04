//go:build !linux && !darwin

package auth

// sessionBusCheck does nothing where the keyring is not reached over D-Bus.
var sessionBusCheck = func() error { return nil }
