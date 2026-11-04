package output

import (
	"bytes"
	"errors"
	"strings"
	"testing"

	"github.com/mggarofalo/memoria/cli/internal/api"
)

func TestParseFormat(t *testing.T) {
	for _, value := range []string{"json", "table", "markdown"} {
		got, err := ParseFormat(value, Table)
		if err != nil || string(got) != value {
			t.Errorf("ParseFormat(%q) = %q, %v", value, got, err)
		}
	}
	if got, err := ParseFormat("", Markdown); err != nil || got != Markdown {
		t.Errorf("default = %q, %v", got, err)
	}
	if _, err := ParseFormat("yaml-secret", Table); err == nil || strings.Contains(err.Error(), "yaml-secret") {
		t.Errorf("err = %v", err)
	}
}

func ptr[T any](v T) *T { return &v }

func TestTableCellsCannotBreakTheLayout(t *testing.T) {
	result := &api.SearchResult{Results: []api.SearchHit{{
		Score: 0.5, Docid: "d", File: "a\tb\nc", Line: 1, Title: "x\r\ny\x1b[2J",
	}}}
	var out bytes.Buffer
	if err := SearchTable(&out, result); err != nil {
		t.Fatal(err)
	}
	if lines := strings.Split(strings.TrimRight(out.String(), "\n"), "\n"); len(lines) != 2 {
		t.Fatalf("a value broke the table: %q", out.String())
	}
	if strings.Contains(out.String(), "\x1b") {
		t.Fatalf("out = %q", out.String())
	}
}

func TestWholeTableArrivesInOneWrite(t *testing.T) {
	counter := &writeCounter{}
	docs := []api.Document{{Docid: "a"}, {Docid: "b"}, {Docid: "c"}}
	if err := DocumentTable(counter, docs); err != nil {
		t.Fatal(err)
	}
	if counter.writes != 1 {
		t.Fatalf("writes = %d; scrubbing works per write", counter.writes)
	}
}

func TestDocumentMarkdownEndsWithANewline(t *testing.T) {
	var out bytes.Buffer
	if err := DocumentMarkdown(&out, &api.Document{Body: ptr("no newline")}); err != nil {
		t.Fatal(err)
	}
	if out.String() != "no newline\n" {
		t.Fatalf("out = %q", out.String())
	}
	out.Reset()
	if err := DocumentMarkdown(&out, &api.Document{Body: ptr("has one\n")}); err != nil {
		t.Fatal(err)
	}
	if out.String() != "has one\n" {
		t.Fatalf("out = %q", out.String())
	}
}

func TestDocumentsMarkdownHandlesAMissingBody(t *testing.T) {
	var out bytes.Buffer
	if err := DocumentsMarkdown(&out, []api.Document{{File: "a.md"}, {File: "b.md", Body: ptr("text")}}); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.String(), "## ` a.md `") || !strings.Contains(out.String(), "## ` b.md `\n\n```\ntext\n```") {
		t.Fatalf("out = %q", out.String())
	}
}

func TestRenderersReportWriteFailures(t *testing.T) {
	status := &api.IndexStatus{}
	list := &api.CollectionList{}
	result := &api.SearchResult{}
	for name, render := range map[string]func() error{
		"search table":    func() error { return SearchTable(failing{}, result) },
		"search markdown": func() error { return SearchMarkdown(failing{}, result) },
		"collections":     func() error { return CollectionsTable(failing{}, list) },
		"collections md":  func() error { return CollectionsMarkdown(failing{}, list) },
		"status":          func() error { return StatusTable(failing{}, status) },
		"status md":       func() error { return StatusMarkdown(failing{}, status) },
		"document":        func() error { return DocumentMarkdown(failing{}, &api.Document{}) },
		"documents":       func() error { return DocumentsMarkdown(failing{}, nil) },
	} {
		if err := render(); err == nil {
			t.Errorf("%s: expected an error", name)
		}
	}
}

type writeCounter struct{ writes int }

func (c *writeCounter) Write(p []byte) (int, error) {
	c.writes++
	return len(p), nil
}

type failing struct{}

func (failing) Write([]byte) (int, error) { return 0, errors.New("closed") }
