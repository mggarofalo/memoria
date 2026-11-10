package output

import (
	"bytes"
	"strings"
	"testing"

	"github.com/mggarofalo/memoria/cli/internal/api"
)

func statusWith(needs *int32) *api.IndexStatus {
	return &api.IndexStatus{
		TotalDocuments: 3723,
		NeedsEmbedding: needs,
		HasVectorIndex: true,
		Collections:    []api.IndexCollectionStatus{{Name: "notes", Documents: 1575}},
	}
}

// The count is a pointer the server may leave out, and printed with %d it was the pointer's address.
func TestStatusSaysHowManyNeedEmbedding(t *testing.T) {
	pending := int32(44)
	for name, render := range map[string]func(*bytes.Buffer, *api.IndexStatus) error{
		"table":    func(b *bytes.Buffer, s *api.IndexStatus) error { return StatusTable(b, s) },
		"markdown": func(b *bytes.Buffer, s *api.IndexStatus) error { return StatusMarkdown(b, s) },
	} {
		var counted, missing bytes.Buffer
		if err := render(&counted, statusWith(&pending)); err != nil {
			t.Fatalf("%s: %v", name, err)
		}
		if err := render(&missing, statusWith(nil)); err != nil {
			t.Fatalf("%s: %v", name, err)
		}
		if !strings.Contains(counted.String(), "needs embedding: 44\n") {
			t.Errorf("%s with a count: %q", name, counted.String())
		}
		if !strings.Contains(missing.String(), "needs embedding: unknown\n") {
			t.Errorf("%s with none: %q", name, missing.String())
		}
		for _, out := range []string{counted.String(), missing.String()} {
			if strings.Contains(out, "0xc") || !strings.Contains(out, "documents: 3723") || !strings.Contains(out, "notes") {
				t.Errorf("%s: %q", name, out)
			}
		}
	}
}
