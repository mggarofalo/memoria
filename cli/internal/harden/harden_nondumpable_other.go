//go:build !linux

package harden

func disableDumpable() error { return nil }
