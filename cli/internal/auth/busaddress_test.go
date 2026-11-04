package auth

import "testing"

func TestBusSocketPath(t *testing.T) {
	for address, want := range map[string]string{
		"unix:path=/run/user/1000/bus":          "/run/user/1000/bus",
		"unix:path=/run/user/1000/bus,guid=abc": "/run/user/1000/bus",
		"unix:guid=abc,path=/run/user/1000/bus": "/run/user/1000/bus",
	} {
		got, err := busSocketPath(address)
		if err != nil || got != want {
			t.Errorf("busSocketPath(%q) = %q, %v", address, got, err)
		}
	}
	for _, address := range []string{
		"tcp:host=evil.example.com,port=1234",
		"nonce-tcp:host=evil,port=1",
		"unix:abstract=/tmp/dbus-x",
		"unix:tmpdir=/tmp",
		"unix:runtime=yes",
		"unix:path=relative/bus",
		"unix:guid=abc",
		"unix:path=/run/user/1000/bus;tcp:host=evil,port=1",
		"unix:path=/run/user/1000/b%75s",
		"autolaunch:",
		"launchd:env=X",
		"",
	} {
		if _, err := busSocketPath(address); err == nil {
			t.Errorf("busSocketPath(%q) should refuse", address)
		}
	}
}
