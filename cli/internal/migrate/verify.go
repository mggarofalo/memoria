package migrate

import (
	"bytes"
	"encoding/json"
	"strings"
)

// VerifySummary is what one collection's check found. It holds counts and names no file or query.
type VerifySummary struct {
	Name   string `json:"name"`
	Target string `json:"target"`
	// LocalFiles is what a plan selects here, and ServerDocuments what the server says it indexes there.
	LocalFiles      int `json:"localFiles"`
	ServerDocuments int `json:"serverDocuments"`
	// Confirmed is paths the server records as complete, at the local file's version.
	Confirmed int `json:"confirmed"`
	TakenOver int `json:"takenOver"`
	// VersionDiffers is paths the server holds at another version than the local file's.
	VersionDiffers int `json:"versionDiffers"`
	NotHeld        int `json:"notHeld"`
	// SecondSpellings is files sharing a composed name with an earlier one. A push refuses them.
	SecondSpellings int     `json:"secondSpellings"`
	LocalUnreadable int     `json:"localUnreadable"`
	NotChecked      int     `json:"notChecked"`
	FoldersSkipped  int     `json:"foldersSkipped"`
	Sampled         int     `json:"sampled"`
	SampleSame      int     `json:"sampleSame"`
	SampleRedacted  int     `json:"sampleSameApartFromRedaction"`
	SampleDifferent int     `json:"sampleDifferent"`
	SampleFailed    int     `json:"sampleNotFetched"`
	Overlap         Overlap `json:"overlap"`
}

// Overlap is how many of local QMD's top results the server also returned. Each query's share is of local QMD's results.
type Overlap struct {
	Queries int `json:"queries"`
	// NoLocalResults is queries local QMD found nothing for, which say nothing about overlap and are left out of the mean.
	NoLocalResults int     `json:"noLocalResults"`
	Failed         int     `json:"failed"`
	Mean           float64 `json:"mean"`
	Lowest         float64 `json:"lowest"`
}

// Problems is what makes the check fail. Overlap is not here, as no threshold for it has been measured.
func (s *VerifySummary) Problems() int {
	return s.VersionDiffers + s.NotHeld + s.SecondSpellings + s.LocalUnreadable + s.NotChecked + s.FoldersSkipped + s.SampleDifferent + s.SampleFailed + s.Overlap.Failed
}

// VerifyResult is what a run says on stdout.
type VerifyResult struct {
	Collections []VerifySummary `json:"collections"`
	LocalMode   string          `json:"localMode"`
	ReportFile  string          `json:"reportFile"`
}

// VerifyProblem is one document or query in the local report, the only record that names one.
type VerifyProblem struct {
	Collection string `json:"collection"`
	Kind       string `json:"kind"`
	Name       string `json:"name"`
	Detail     string `json:"detail,omitempty"`
}

// The outcomes of comparing a fetched document with its local copy.
const (
	TextSame = iota
	TextRedacted
	TextDifferent
)

const redactedMark = "[REDACTED:"

// CompareText compares a local file with what a server returns for it. Redaction changes bytes on purpose, so a whole-file
// comparison proves nothing. Every line the server holds must be local, unless it carries a redaction, and each local line it
// lacks must be matched by a redacted one. This cannot see a change inside a redacted line.
func CompareText(local, server string) int {
	if normalized(local) == normalized(server) {
		return TextSame
	}
	localLines, redacted := lineCounts(local), 0
	for _, line := range strings.Split(normalized(server), "\n") {
		switch {
		case strings.Contains(line, redactedMark):
			redacted++
		case localLines[line] > 0:
			localLines[line]--
		case strings.TrimSpace(line) != "":
			return TextDifferent
		}
	}
	return judgeLeftover(localLines, redacted)
}

func judgeLeftover(leftover map[string]int, redacted int) int {
	missing := 0
	for line, count := range leftover {
		if strings.TrimSpace(line) != "" {
			missing += count
		}
	}
	if missing > redacted {
		return TextDifferent
	}
	return TextRedacted
}

func normalized(text string) string {
	return strings.TrimSpace(strings.ReplaceAll(text, "\r\n", "\n"))
}

func lineCounts(text string) map[string]int {
	counts := map[string]int{}
	for _, line := range strings.Split(normalized(text), "\n") {
		counts[line]++
	}
	return counts
}

// HitKey is a result's identity for comparison: its path in the collection, as QMD spells it, whichever collection name holds it.
func HitKey(file, collection string) string {
	key := strings.TrimPrefix(file, "qmd://")
	key = strings.TrimPrefix(key, collection+"/")
	return strings.ToLower(key)
}

// ParseLocalHits reads `qmd search --json`. It prints nothing but a message when there are no results.
func ParseLocalHits(output []byte, collection string) ([]string, error) {
	start := bytes.IndexByte(output, '[')
	if start < 0 {
		return nil, nil
	}
	var hits []struct {
		File string `json:"file"`
	}
	if err := json.Unmarshal(output[start:], &hits); err != nil {
		return nil, err
	}
	keys := make([]string, 0, len(hits))
	for _, hit := range hits {
		keys = append(keys, HitKey(hit.File, collection))
	}
	return keys, nil
}

// Share is the part of local's results that server also has.
func Share(local, server []string) float64 {
	held := make(map[string]bool, len(server))
	for _, key := range server {
		held[key] = true
	}
	shared := 0
	for _, key := range local {
		if held[key] {
			shared++
		}
	}
	return float64(shared) / float64(len(local))
}

// Add records one query's share.
func (o *Overlap) Add(share float64) {
	counted := o.Queries - o.NoLocalResults - o.Failed
	if counted == 0 || share < o.Lowest {
		o.Lowest = share
	}
	o.Mean = (o.Mean*float64(counted) + share) / float64(counted+1)
	o.Queries++
}
