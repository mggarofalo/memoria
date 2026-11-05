package output

import (
	"fmt"
	"io"
	"strings"
	"time"

	"github.com/mggarofalo/memoria/cli/internal/api"
	"github.com/mggarofalo/memoria/cli/internal/termsafe"
)

// KeyCollections describes what a key can reach, naming the every-collection case
// outright, since that is the one a reader should notice.
func KeyCollections(info *api.ApiKeyInfo) string {
	switch {
	case info.AllCollections:
		return "ALL collections"
	case len(info.Collections) == 0:
		return "no collections"
	}
	return termsafe.Line(strings.Join(info.Collections, ","))
}

func keyState(info *api.ApiKeyInfo, now time.Time) string {
	switch {
	case info.RevokedAt != nil:
		return "revoked"
	case info.ExpiresAt != nil && !info.ExpiresAt.After(now):
		return "expired"
	}
	return "active"
}

func optionalTime(t *time.Time) string {
	if t == nil {
		return "-"
	}
	return t.UTC().Format(time.RFC3339)
}

// KeysTable renders the key list as a table.
func KeysTable(w io.Writer, list *api.ApiKeyList) error {
	now := time.Now()
	rows := make([]string, 0, len(list.Keys))
	for i := range list.Keys {
		info := &list.Keys[i]
		rows = append(rows, row(info.Id, keyState(info, now), info.Label, strings.Join(info.Scopes, ","), KeyCollections(info),
			optionalTime(&info.CreatedAt), optionalTime(info.ExpiresAt), optionalTime(info.LastUsedAt)))
	}
	return table(w, "ID\tSTATE\tLABEL\tSCOPES\tCOLLECTIONS\tCREATED\tEXPIRES\tLAST USED", rows)
}

// KeysMarkdown renders the key list as a bullet list.
func KeysMarkdown(w io.Writer, list *api.ApiKeyList) error {
	now := time.Now()
	var b strings.Builder
	for i := range list.Keys {
		info := &list.Keys[i]
		fmt.Fprintf(&b, "- %s **%s** (%s): %s; %s\n", codeSpan(termsafe.Line(info.Id)), inlineText(info.Label), keyState(info, now),
			inlineText(strings.Join(info.Scopes, ",")), markdownEscaper.Replace(KeyCollections(info)))
	}
	return writeText(w, &b)
}
