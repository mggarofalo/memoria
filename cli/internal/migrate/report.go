package migrate

import (
	"bytes"
	"encoding/json"
	"errors"
)

// CheckResult is one list of files from the redaction check. It holds counts and rule names
// and never a value, a path or a line.
type CheckResult struct {
	Files   int `json:"files"`
	Changed int `json:"changed"`
	Refused int `json:"refused"`
	Skipped int `json:"skipped"`
	// CredentialNames is the files whose path itself looks like a credential, which a server refuses to store or log.
	CredentialNames int `json:"credentialNames"`
	// Unscanned is the documents gitleaks was meant to read and did not, as it stopped or gave nothing usable.
	Unscanned       int            `json:"gitleaksUnscanned"`
	Replacements    map[string]int `json:"replacements"`
	FilesByRule     map[string]int `json:"filesByRule"`
	GitleaksRan     bool           `json:"gitleaksRan"`
	GitleaksResults map[string]int `json:"gitleaksFindings"`
	// GitleaksIgnored is the findings the --gitleaks-ignore file lists by fingerprint. They do not fail the run.
	GitleaksIgnored int `json:"gitleaksIgnored"`
}

// Report is a collection's redaction check. Skipped also counts what the plan could not read, so a folder that was
// denied is not taken for one that was clean.
type Report struct {
	Name string `json:"name"`
	CheckResult
}

// Clean reports whether nothing was refused and gitleaks, if it ran, read everything and found nothing. Even so,
// that is not proof: gitleaks can miss a token at a chunk boundary, or a form its rules do not know.
func (r CheckResult) Clean() bool {
	return r.Refused == 0 && r.CredentialNames == 0 && r.Unscanned == 0 && len(r.GitleaksResults) == 0
}

// ParseCheck reads the tool's output: one JSON line, the last on stdout, with one result for
// each list it was given. Anything else is refused, as a wrong number of results would
// credit counts to the wrong collection.
func ParseCheck(stdout []byte, lists int) ([]CheckResult, error) {
	lines := bytes.Split(bytes.TrimSpace(stdout), []byte("\n"))
	var results []CheckResult
	if err := json.Unmarshal(lines[len(lines)-1], &results); err != nil {
		return nil, errors.New("the redaction check's output could not be read")
	}
	if len(results) != lists {
		return nil, errors.New("the redaction check did not report on every collection")
	}
	return results, nil
}

// CheckList is one collection for the redaction check, sent on its standard input. Root is so it can judge each file's path
// as a server would see it, relative to the collection.
type CheckList struct {
	// Name leads each fingerprint, so a reviewed finding in one collection cannot excuse one in another.
	Name  string   `json:"name"`
	Root  string   `json:"root"`
	Paths []string `json:"paths"`
}
