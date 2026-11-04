// Package output renders API results. Table and markdown output pass every
// server-supplied string through termsafe, whether or not stdout is a terminal,
// because piped output often ends up on one anyway (less -R, a later cat). JSON
// output is the response body byte for byte and is written by the caller.
package output

import (
	"bytes"
	"errors"
	"fmt"
	"io"
	"strings"
	"text/tabwriter"

	"github.com/mggarofalo/memoria/cli/internal/api"
	"github.com/mggarofalo/memoria/cli/internal/termsafe"
)

// Format selects how results are shown.
type Format string

// The output formats.
const (
	JSON     Format = "json"
	Table    Format = "table"
	Markdown Format = "markdown"
)

// ParseFormat validates a --format value. Empty means the command's default.
func ParseFormat(value string, fallback Format) (Format, error) {
	switch Format(value) {
	case "":
		return fallback, nil
	case JSON, Table, Markdown:
		return Format(value), nil
	}
	return "", errors.New("unknown format: use json, table, or markdown")
}

// table buffers rows and flushes once, so a whole table reaches the writer in
// one Write and secret scrubbing sees every row intact.
func table(w io.Writer, header string, rows []string) error {
	var buf bytes.Buffer
	tw := tabwriter.NewWriter(&buf, 0, 4, 2, ' ', 0)
	fmt.Fprintln(tw, header)
	for _, row := range rows {
		fmt.Fprintln(tw, row)
	}
	_ = tw.Flush() // Writing to a bytes.Buffer cannot fail.
	_, err := w.Write(buf.Bytes())
	return err
}

// row sanitizes each cell so that no value can break the table layout.
func row(cells ...any) string {
	parts := make([]string, len(cells))
	for i, cell := range cells {
		parts[i] = termsafe.Line(fmt.Sprint(cell))
	}
	return strings.Join(parts, "\t")
}

func deref(s *string) string {
	if s == nil {
		return ""
	}
	return *s
}

func writeText(w io.Writer, b *strings.Builder) error {
	_, err := io.WriteString(w, b.String())
	return err
}

// longestBacktickRun finds the longest run of backticks, which is what a code span
// or fence has to outrun to contain the text.
func longestBacktickRun(s string) int {
	longest, run := 0, 0
	for i := 0; i < len(s); i++ {
		if s[i] != '`' {
			run = 0
			continue
		}
		run++
		longest = max(longest, run)
	}
	return longest
}

// codeSpan makes one-line untrusted text inert, whatever backticks it holds. A
// name ending in a backtick could otherwise close the span and carry on as markup.
func codeSpan(s string) string {
	delimiter := strings.Repeat("`", longestBacktickRun(s)+1)
	return delimiter + " " + s + " " + delimiter
}

// fenced puts untrusted text in a code block, with a fence longer than any run of
// backticks inside it. Unfenced, a snippet could forge a "## 2." result.
func fenced(s string) string {
	fence := strings.Repeat("`", max(3, longestBacktickRun(s)+1))
	return fence + "\n" + strings.TrimRight(s, "\n") + "\n" + fence + "\n"
}

// SearchTable renders search hits as a table.
func SearchTable(w io.Writer, result *api.SearchResult) error {
	rows := make([]string, 0, len(result.Results))
	for _, hit := range result.Results {
		rows = append(rows, row(fmt.Sprintf("%.2f", hit.Score), hit.Docid, hit.File, hit.Line, hit.Title))
	}
	return table(w, "SCORE\tDOCID\tFILE\tLINE\tTITLE", rows)
}

// SearchMarkdown renders search hits with their snippets.
func SearchMarkdown(w io.Writer, result *api.SearchResult) error {
	var b strings.Builder
	for i, hit := range result.Results {
		fmt.Fprintf(&b, "## %d. %s\n\n", i+1, termsafe.Line(hit.Title))
		location := fmt.Sprintf("%s:%d", termsafe.Line(hit.File), hit.Line)
		fmt.Fprintf(&b, "%s (score %.2f, docid %s)\n\n", codeSpan(location), hit.Score, codeSpan(termsafe.Line(hit.Docid)))
		b.WriteString(fenced(termsafe.Text(hit.Snippet)))
		b.WriteByte('\n')
	}
	return writeText(w, &b)
}

// DocumentTable renders document metadata as a table.
func DocumentTable(w io.Writer, docs []api.Document) error {
	rows := make([]string, 0, len(docs))
	for _, doc := range docs {
		rows = append(rows, row(doc.Docid, doc.File, doc.BodyLength, doc.ModifiedAt, doc.Title))
	}
	return table(w, "DOCID\tFILE\tBYTES\tMODIFIED\tTITLE", rows)
}

// DocumentMarkdown renders one document: its body, or its metadata if the body
// was not requested.
func DocumentMarkdown(w io.Writer, doc *api.Document) error {
	var b strings.Builder
	if doc.Body == nil {
		fmt.Fprintf(&b, "# %s\n\n%s, %d bytes, modified %s\n", termsafe.Line(doc.Title), termsafe.Line(doc.File), doc.BodyLength, termsafe.Line(doc.ModifiedAt))
		return writeText(w, &b)
	}
	b.WriteString(termsafe.Text(*doc.Body))
	if !strings.HasSuffix(b.String(), "\n") {
		b.WriteByte('\n')
	}
	return writeText(w, &b)
}

// DocumentsMarkdown renders several documents, each under a heading naming it.
func DocumentsMarkdown(w io.Writer, docs []api.Document) error {
	var b strings.Builder
	for _, doc := range docs {
		fmt.Fprintf(&b, "## %s\n\n", codeSpan(termsafe.Line(doc.File)))
		b.WriteString(fenced(termsafe.Text(deref(doc.Body))))
		b.WriteByte('\n')
	}
	return writeText(w, &b)
}

// CollectionsTable renders the collection list.
func CollectionsTable(w io.Writer, list *api.CollectionList) error {
	rows := make([]string, 0, len(list.Collections))
	for _, c := range list.Collections {
		rows = append(rows, row(c.Name, c.Documents, c.IncludeByDefault, deref(c.LastModified), deref(c.Context)))
	}
	return table(w, "NAME\tDOCUMENTS\tDEFAULT\tMODIFIED\tCONTEXT", rows)
}

// CollectionsMarkdown renders the collection list as a bullet list.
func CollectionsMarkdown(w io.Writer, list *api.CollectionList) error {
	var b strings.Builder
	for _, c := range list.Collections {
		fmt.Fprintf(&b, "- **%s**: %d documents\n", termsafe.Line(c.Name), c.Documents)
	}
	return writeText(w, &b)
}

// StatusTable renders index status as a summary and a per-collection table.
func StatusTable(w io.Writer, status *api.IndexStatus) error {
	var b strings.Builder
	fmt.Fprintf(&b, "documents: %d\nneeds embedding: %d\nvector index: %t\n\n", status.TotalDocuments, status.NeedsEmbedding, status.HasVectorIndex)
	if err := writeText(w, &b); err != nil {
		return err
	}
	rows := make([]string, 0, len(status.Collections))
	for _, c := range status.Collections {
		rows = append(rows, row(c.Name, c.Documents, c.LastUpdated))
	}
	return table(w, "COLLECTION\tDOCUMENTS\tUPDATED", rows)
}

// StatusMarkdown renders index status as a bullet list.
func StatusMarkdown(w io.Writer, status *api.IndexStatus) error {
	var b strings.Builder
	fmt.Fprintf(&b, "- documents: %d\n- needs embedding: %d\n- vector index: %t\n", status.TotalDocuments, status.NeedsEmbedding, status.HasVectorIndex)
	for _, c := range status.Collections {
		fmt.Fprintf(&b, "- %s: %d documents\n", termsafe.Line(c.Name), c.Documents)
	}
	return writeText(w, &b)
}
