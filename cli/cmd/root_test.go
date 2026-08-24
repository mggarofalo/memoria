package cmd

import "testing"

func TestRootCommandHasName(t *testing.T) {
	root := newRootCmd()
	if root.Name() != "memoria" {
		t.Fatalf("root command name = %q, want %q", root.Name(), "memoria")
	}
}

func TestRootCommandDefinesAuthFlags(t *testing.T) {
	root := newRootCmd()
	for _, name := range []string{"api-url", "api-key"} {
		if root.PersistentFlags().Lookup(name) == nil {
			t.Errorf("missing persistent flag %q", name)
		}
	}
}
