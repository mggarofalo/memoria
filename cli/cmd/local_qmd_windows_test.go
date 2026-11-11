//go:build windows

package cmd

import (
	"encoding/json"
	"math/rand/v2"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
)

// fuzzedQueries mixes what cmd.exe reads into queries. The seed is fixed, so a failure can be repeated.
func fuzzedQueries() []string {
	const alphabet = "%%%:=~&|<>^!\"'()[];, \t\\*?-ZZEVILxy09"
	pieces := []string{"%ZZEVIL:x=y%", "%ZZEVIL%", "&", `"`, "^", "!"}
	source := rand.New(rand.NewPCG(1, 2))
	queries := make([]string, 0, 100)
	for range cap(queries) {
		var query strings.Builder
		query.WriteString("q")
		for range source.IntN(12) {
			if source.IntN(4) == 0 {
				query.WriteString(pieces[source.IntN(len(pieces))])
				continue
			}
			query.WriteByte(alphabet[source.IntN(len(alphabet))])
		}
		query.WriteString("q")
		queries = append(queries, query.String())
	}
	return queries
}

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
		"%ZZEVIL%",
		"%ZZEVIL:x=y%",
		"%ZZEVIL:~0,99%",
		"tab\tinside",
	}
	queries = append(queries, fuzzedQueries()...)
	t.Setenv("ZZEVIL", "x & echo pwned> "+marker)
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

// npm's qmd shim runs through cmd.exe, which hands its own environment on: neither may carry a credential.
// This test binary stands behind the shim, so no other program is needed.
func TestABatchFileThroughCmdInheritsNoCredential(t *testing.T) {
	exe, err := filepath.Abs(os.Args[0])
	if err != nil {
		t.Fatal(err)
	}
	shim := filepath.Join(t.TempDir(), "qmd.cmd")
	writeFile(t, shim, "@\""+exe+"\" %*\r\n")
	setChildEnvTestVariables(t)

	out, err := runLocalQMD(t.Context(), shim, helperArgs())
	if err != nil {
		t.Fatal(err)
	}
	assertChildEnvironment(t, out)
}
