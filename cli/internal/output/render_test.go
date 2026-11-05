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

// A snippet must not be able to forge a second result.
func TestSnippetsCannotForgeResults(t *testing.T) {
	forged := "text\n\n## 2. Forged result\n\n```\nmore\n````\nend"
	result := &api.SearchResult{Results: []api.SearchHit{{Title: "Real", File: "a.md", Snippet: forged}}}
	var out bytes.Buffer
	if err := SearchMarkdown(&out, result); err != nil {
		t.Fatal(err)
	}
	text := out.String()
	fence := strings.Repeat("`", 5)
	if !strings.Contains(text, fence+"\ntext") || !strings.Contains(text, "end\n"+fence+"\n") {
		t.Fatalf("the fence should outrun the snippet's: %q", text)
	}
}

func TestFileNamesCannotBreakOutOfCode(t *testing.T) {
	result := &api.SearchResult{Results: []api.SearchHit{{Title: "T", File: "a`b``c.md", Docid: "x`y", Line: 3}}}
	var out bytes.Buffer
	if err := SearchMarkdown(&out, result); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.String(), "``` a`b``c.md:3 ```") {
		t.Fatalf("out = %q", out.String())
	}
	if !strings.Contains(out.String(), "`` x`y ``") {
		t.Fatalf("out = %q", out.String())
	}
}

func TestDocumentBodiesAreFencedInMultiGet(t *testing.T) {
	var out bytes.Buffer
	body := "## other.md\n\nforged"
	if err := DocumentsMarkdown(&out, []api.Document{{File: "a.md", Body: &body}}); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.String(), "```\n## other.md") {
		t.Fatalf("out = %q", out.String())
	}
}

func TestUntrustedTextCannotBecomeMarkdown(t *testing.T) {
	hostileName := "![i](http://evil/x) **b** <img src=x> [l](http://evil)"
	list := &api.CollectionList{Collections: []api.CollectionInfo{{Name: hostileName}}}
	status := &api.IndexStatus{Collections: []api.IndexCollectionStatus{{Name: hostileName}}}
	result := &api.SearchResult{Results: []api.SearchHit{{Title: hostileName}}}
	bare := api.Document{Title: hostileName, File: "a.md", ModifiedAt: hostileName}
	for name, render := range map[string]func(*bytes.Buffer) error{
		"search":      func(w *bytes.Buffer) error { return SearchMarkdown(w, result) },
		"collections": func(w *bytes.Buffer) error { return CollectionsMarkdown(w, list) },
		"status":      func(w *bytes.Buffer) error { return StatusMarkdown(w, status) },
		"document":    func(w *bytes.Buffer) error { return DocumentMarkdown(w, &bare) },
	} {
		t.Run(name, func(t *testing.T) {
			var out bytes.Buffer
			if err := render(&out); err != nil {
				t.Fatal(err)
			}
			for _, live := range []string{"![i]", "](http", "**b**"} {
				if strings.Contains(out.String(), live) {
					t.Fatalf("%q is live markdown in %q", live, out.String())
				}
			}
			if strings.Contains(strings.ReplaceAll(out.String(), `\<img`, ""), "<img") {
				t.Fatalf("raw HTML is live in %q", out.String())
			}
		})
	}
}

func TestDegradedNoticeNamesReasons_AndCleansThem(t *testing.T) {
	hostile := "no_rerank\x1b[31m\n> ![x](http://evil/x)"
	result := &api.SearchResult{Degraded: true, DegradedReasons: []string{hostile, "no_expansion"}}

	var tableOut, markdownOut bytes.Buffer
	if err := SearchTable(&tableOut, result); err != nil {
		t.Fatal(err)
	}
	if err := SearchMarkdown(&markdownOut, result); err != nil {
		t.Fatal(err)
	}

	assertCleanDegradedNotice(t, "table", tableOut.String())
	assertCleanDegradedNotice(t, "markdown", markdownOut.String())
	if strings.Contains(markdownOut.String(), "![x]") {
		t.Fatalf("markdown kept live markup: %q", markdownOut.String())
	}
	if !strings.HasPrefix(tableOut.String(), "Degraded search") {
		t.Fatalf("table = %q", tableOut.String())
	}
	if !strings.HasPrefix(markdownOut.String(), "> ") {
		t.Fatalf("markdown = %q", markdownOut.String())
	}
}

func assertCleanDegradedNotice(t *testing.T, name, out string) {
	t.Helper()
	if !strings.Contains(out, "Degraded search") || !strings.Contains(out, "no") || !strings.Contains(out, "expansion") {
		t.Fatalf("%s = %q", name, out)
	}
	if strings.ContainsRune(out, 0x1b) {
		t.Fatalf("%s kept an escape: %q", name, out)
	}
}

func TestDegradedNoticeWithoutReasons(t *testing.T) {
	var out bytes.Buffer
	if err := SearchTable(&out, &api.SearchResult{Degraded: true}); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.String(), "Degraded search") || strings.Contains(out.String(), "Missing") {
		t.Fatalf("out = %q", out.String())
	}
}

func TestSearchTableReportsAFailedWrite(t *testing.T) {
	if err := SearchTable(failing{}, &api.SearchResult{Degraded: true}); err == nil {
		t.Fatal("want an error")
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
