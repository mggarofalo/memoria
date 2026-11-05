package migrate

import (
	"bytes"
	"encoding/json"
	"errors"
)

// CheckResult is one list of files from the redaction check. It holds counts and rule names
// and never a value, a path or a line.
type CheckResult struct {
	Files           int            `json:"files"`
	Changed         int            `json:"changed"`
	Refused         int            `json:"refused"`
	Replacements    map[string]int `json:"replacements"`
	FilesByRule     map[string]int `json:"filesByRule"`
	GitleaksRan     bool           `json:"gitleaksRan"`
	GitleaksResults map[string]int `json:"gitleaksFindings"`
}

// Report is a collection's redaction check.
type Report struct {
	Name string `json:"name"`
	CheckResult
}

// Clean reports whether nothing was refused and gitleaks, if it ran, found nothing.
func (r CheckResult) Clean() bool {
	return r.Refused == 0 && len(r.GitleaksResults) == 0
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
