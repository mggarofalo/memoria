package output

import (
	"bytes"
	"strings"
	"testing"

	"github.com/mggarofalo/memoria/cli/internal/api"
)

const hostile = "\x1b]52;c;ZXZpbA==\x07\x1b[2Jvisible\rEVIL"

func TestRenderersSanitizeEveryServerString(t *testing.T) {
	result := &api.SearchResult{Results: []api.SearchHit{{Score: 0.5, Docid: hostile, File: hostile, Line: 3, Title: hostile, Snippet: hostile}}}
	doc := api.Document{Docid: hostile, File: hostile, Title: hostile, ModifiedAt: hostile, Body: ptr(hostile)}
	bare := api.Document{Docid: hostile, File: hostile, Title: hostile, ModifiedAt: hostile}
	list := &api.CollectionList{Collections: []api.CollectionInfo{{Name: hostile, LastModified: ptr(hostile), Context: ptr(hostile), Documents: 1}}}
	status := &api.IndexStatus{TotalDocuments: 1, Collections: []api.IndexCollectionStatus{{Name: hostile, LastUpdated: hostile, Documents: 1}}}

	renderers := map[string]func(*bytes.Buffer) error{
		"search table":        func(w *bytes.Buffer) error { return SearchTable(w, result) },
		"search markdown":     func(w *bytes.Buffer) error { return SearchMarkdown(w, result) },
		"document table":      func(w *bytes.Buffer) error { return DocumentTable(w, []api.Document{doc}) },
		"document markdown":   func(w *bytes.Buffer) error { return DocumentMarkdown(w, &doc) },
		"metadata markdown":   func(w *bytes.Buffer) error { return DocumentMarkdown(w, &bare) },
		"documents markdown":  func(w *bytes.Buffer) error { return DocumentsMarkdown(w, []api.Document{doc}) },
		"collections table":   func(w *bytes.Buffer) error { return CollectionsTable(w, list) },
		"collections bullets": func(w *bytes.Buffer) error { return CollectionsMarkdown(w, list) },
		"status table":        func(w *bytes.Buffer) error { return StatusTable(w, status) },
		"status bullets":      func(w *bytes.Buffer) error { return StatusMarkdown(w, status) },
	}
	for name, render := range renderers {
		t.Run(name, func(t *testing.T) {
			var out bytes.Buffer
			if err := render(&out); err != nil {
				t.Fatal(err)
			}
			text := out.String()
			for _, bad := range []string{"\x1b", "\x07", "\r", "]52;", "[2J"} {
				if strings.Contains(text, bad) {
					t.Fatalf("kept %q: %q", bad, text)
				}
			}
			if !strings.Contains(text, "visible") {
				t.Fatalf("lost the text: %q", text)
			}
		})
	}
}

func TestStatusTableReportsAFailedTableWrite(t *testing.T) {
	limited := &failAfterFirst{}
	if err := StatusTable(limited, &api.IndexStatus{}); err == nil {
		t.Fatal("expected an error")
	}
}

type failAfterFirst struct{ writes int }

func (f *failAfterFirst) Write(p []byte) (int, error) {
	f.writes++
	if f.writes > 1 {
		return failing{}.Write(p)
	}
	return len(p), nil
}
