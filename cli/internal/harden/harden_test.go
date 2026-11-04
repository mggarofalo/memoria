package harden

import "testing"

func TestProcessSucceeds(t *testing.T) {
	if err := Process(); err != nil {
		t.Fatal(err)
	}
}
