//go:build windows

package cmd

import (
	"encoding/json"
	"os"
	"os/exec"
	"path/filepath"
	"testing"
)

// A real .cmd file that hands its arguments on with %*, as npm's shims do. If any of these were interpreted, the marker file
// would appear, or the arguments would arrive changed.
func TestABatchFileGetsEachArgumentWhole(t *testing.T) {
	node, err := exec.LookPath("node")
	if err != nil {
		t.Skip("no node here to stand behind the shim")
	}
	dir := t.TempDir()
	marker := filepath.Join(dir, "marker")
	script := filepath.Join(dir, "print.js")
	writeFile(t, script, "console.log(JSON.stringify(process.argv.slice(2)))")
	shim := filepath.Join(dir, "qmd shim.cmd")
	writeFile(t, shim, "@\""+node+"\" \""+script+"\" %*\r\n")
	queries := []string{
		"plain words",
		"a & echo pwned> " + marker,
		"a | echo pwned> " + marker,
		"a > " + marker,
		`he said "quoted" & echo pwned> ` + marker,
		"%PATH% and %USERNAME%",
		"!PATH! ^caret^ (paren) [bracket] ;semi, comma * star ? mark",
		`back\slash\\ ends\`,
		`"`,
		"-looks-like-a-flag",
	}
	for _, query := range queries {
		out, err := runLocalQMD(t.Context(), shim, []string{"search", "--json", "--", query})
		if err != nil {
			t.Fatalf("%q: %v", query, err)
		}
		var got []string
		if err := json.Unmarshal(out, &got); err != nil {
			t.Fatalf("%q: output %q: %v", query, out, err)
		}
		if len(got) != 4 || got[3] != query {
			t.Errorf("%q arrived as %q", query, got)
		}
	}
	if _, err := os.Stat(marker); !os.IsNotExist(err) {
		t.Fatalf("a query ran as a command: %v", err)
	}
}
