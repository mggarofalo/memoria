package migrate

import (
	"bytes"
	"encoding/json"
	"regexp"
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
	// CountDiffers is the two counts not agreeing, with no takeover to explain it.
	CountDiffers bool `json:"countDiffers"`
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
	count := s.VersionDiffers + s.NotHeld + s.SecondSpellings + s.LocalUnreadable + s.NotChecked + s.FoldersSkipped + s.SampleDifferent + s.SampleFailed + s.Overlap.Failed
	if s.CountDiffers {
		count++
	}
	return count
}

// VerifyResult is what a run says on stdout.
type VerifyResult struct {
	Collections []VerifySummary `json:"collections"`
	LocalMode   string          `json:"localMode"`
	// Seed is which sample was taken, so a run can be repeated.
	Seed       uint64 `json:"sampleSeed"`
	ReportFile string `json:"reportFile"`
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

var redactedMark = regexp.MustCompile(`\[REDACTED:[^\]]*\]`)

// maxSecretLines bounds one mark's reach. A 4096-bit key block is about 50 lines; past this a sample reads as different, not as clean.
const maxSecretLines = 256

// CompareText compares a local file with what a server returns for it, line by line and in order. Redaction changes bytes on
// purpose, so a line with a mark must match its local line apart from the marks. One mark may cover several local lines only where
// redaction really does that: a key block, or a token wrapped over lines with no spaces. Where a mark could end at more than one
// line, each is followed. This cannot see a change inside a mark.
func CompareText(local, server string) int {
	if normalized(local) == normalized(server) {
		return TextSame
	}
	localLines, serverLines := strings.Split(normalized(local), "\n"), strings.Split(normalized(server), "\n")
	reached := map[int]bool{0: true}
	for _, line := range serverLines {
		reached = stepLine(localLines, reached, line)
	}
	if !reached[len(localLines)] {
		return TextDifferent
	}
	return TextRedacted
}

// stepLine is every position one server line can lead to, from every position reached so far.
func stepLine(lines []string, reached map[int]bool, line string) map[int]bool {
	next := map[int]bool{}
	for at := range reached {
		for _, end := range lineEnds(lines, at, line) {
			next[end] = true
		}
	}
	return next
}

// lineEnds is where the next line can start if this server line matches at a position.
func lineEnds(lines []string, at int, line string) []int {
	if at >= len(lines) {
		return nil
	}
	if !redactedMark.MatchString(line) {
		if lines[at] == line {
			return []int{at + 1}
		}
		return nil
	}
	var ends []int
	for _, end := range secretEnds(lines, at) {
		if fitsApartFromMarks(strings.Join(lines[at:end], "\n"), line) {
			ends = append(ends, end)
		}
	}
	return ends
}

// secretEnds is how far one mark may reach from a line: that line alone, a key block, or a token wrapped over lines with no spaces.
// Ordinary text has spaces, so a mark cannot hide it.
func secretEnds(lines []string, at int) []int {
	ends := []int{at + 1}
	limit := min(len(lines), at+maxSecretLines)
	inBlock := strings.Contains(lines[at], "-----BEGIN")
	for end := at + 1; end < limit; end++ {
		if inBlock {
			if strings.Contains(lines[end], "-----END") {
				return append(ends, end+1)
			}
			continue
		}
		if strings.TrimSpace(lines[end]) == "" || strings.ContainsAny(strings.TrimSpace(lines[end]), " \t") {
			break
		}
		ends = append(ends, end+1)
	}
	return ends
}

// fitsApartFromMarks compares with quotes set aside, as redaction quotes a placeholder in frontmatter. Each piece around a mark
// must be there, in order, and the text must start and end as the server's does.
func fitsApartFromMarks(local, server string) bool {
	pieces := redactedMark.Split(unquoted(server), -1)
	text := unquoted(local)
	if !strings.HasPrefix(text, pieces[0]) {
		return false
	}
	text = text[len(pieces[0]):]
	for _, piece := range pieces[1 : len(pieces)-1] {
		found := strings.Index(text, piece)
		if found < 0 {
			return false
		}
		text = text[found+len(piece):]
	}
	return strings.HasSuffix(text, pieces[len(pieces)-1])
}

func unquoted(text string) string {
	return strings.NewReplacer(`"`, "", `'`, "").Replace(text)
}

func normalized(text string) string {
	return strings.TrimSpace(strings.ReplaceAll(text, "\r\n", "\n"))
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
