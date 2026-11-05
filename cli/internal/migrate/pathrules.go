package migrate

import (
	"path"
	"regexp"
	"strings"
	"unicode"
	"unicode/utf16"

	"golang.org/x/text/cases"
	"golang.org/x/text/language"
	"golang.org/x/text/unicode/norm"
)

// The reasons a selected file can never be stored at its path. This copies the rules in the API's IngestValidation and the
// sidecar's path-rules.ts, as a plan runs with no server. testdata/path-rules.json holds cases this package reads, and that
// scripts/e2e sends to a real API, so a change on either side fails one of them. A name that looks like a credential is not
// here: only the server's redaction rules can say, so `migrate report` counts those.
const (
	ReasonTooLong        = "path_too_long"
	ReasonTooDeep        = "too_many_folders"
	ReasonBackslash      = "backslash"
	ReasonDotEnd         = "ends_in_a_dot"
	ReasonSpaceEdge      = "space_at_the_edge"
	ReasonControl        = "control_or_invisible_character"
	ReasonWindowsChar    = "character_windows_forbids"
	ReasonDeviceName     = "windows_device_name"
	ReasonShortName      = "windows_short_name"
	ReasonSegmentLong    = "name_too_long"
	ReasonExcludedFolder = "folder_qmd_excludes"
	ReasonNotMarkdown    = "not_ending_in_md"
	ReasonCaseConflict   = "differs_only_in_case"
	ReasonNotUTF8        = "text_not_valid_utf8"
	ReasonFutureTime     = "modified_over_an_hour_ahead"
)

const (
	maxIdentityChars = 1024
	maxPathSegments  = 32
	maxSegmentBytes  = 255
)

var (
	deviceName = regexp.MustCompile(`(?i)^(?:CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³]|CONIN\$|CONOUT\$|CLOCK\$)$`)
	shortName  = regexp.MustCompile(`~\d+(?:\.|$)`)
	foldUpper  = cases.Upper(language.Und)
	foldLower  = cases.Lower(language.Und)
)

// ServerPath is the path a file is sent at. A name in decomposed form, as a Mac can hold, is refused as it stands, so it goes
// in composed form. That is the same every time for the same file, so its identity stays stable between sends.
func ServerPath(name string) string {
	return norm.NFC.String(name)
}

// FoldName is the sidecar's foldName, by which two names count as the same file.
func FoldName(name string) string {
	return foldLower.String(foldUpper.String(norm.NFC.String(name)))
}

// PathProblem says why a server would refuse this path whatever its text, or "" if it would not. It judges what ServerPath sends.
func PathProblem(name string) string {
	sent := ServerPath(name)
	switch {
	case strings.Contains(sent, `\`):
		return ReasonBackslash
	case len(utf16.Encode([]rune(sent))) > maxIdentityChars:
		return ReasonTooLong
	}
	segments := strings.Split(sent, "/")
	if len(segments) > maxPathSegments {
		return ReasonTooDeep
	}
	for _, segment := range segments {
		if problem := segmentProblem(segment); problem != "" {
			return problem
		}
	}
	return wholePathProblem(sent, segments)
}

func wholePathProblem(sent string, segments []string) string {
	for _, segment := range segments[:len(segments)-1] {
		if excludedDirectories[FoldName(segment)] {
			return ReasonExcludedFolder
		}
	}
	// The target's own pattern decides, and every target this makes has QMD's default one. It cares about case.
	if path.Ext(sent) != ".md" {
		return ReasonNotMarkdown
	}
	return ""
}

func segmentProblem(segment string) string {
	switch {
	case strings.HasSuffix(segment, "."):
		return ReasonDotEnd
	case hasSpaceAtEdge(segment):
		return ReasonSpaceEdge
	case strings.IndexFunc(segment, isControlOrInvisible) >= 0:
		return ReasonControl
	case strings.ContainsAny(segment, `<>:"|?*`):
		return ReasonWindowsChar
	case isDeviceName(segment):
		return ReasonDeviceName
	case shortName.MatchString(segment):
		return ReasonShortName
	case len(segment) > maxSegmentBytes:
		return ReasonSegmentLong
	}
	return ""
}

// isJSSpace is what \s means to the sidecar's rule, which is not quite unicode.IsSpace.
func isJSSpace(r rune) bool {
	switch r {
	case '\t', '\n', '\v', '\f', '\r', ' ', 0xA0, 0x1680, 0x2028, 0x2029, 0x202F, 0x205F, 0x3000, 0xFEFF:
		return true
	}
	return r >= 0x2000 && r <= 0x200A
}

func hasSpaceAtEdge(segment string) bool {
	runes := []rune(segment)
	return len(runes) > 0 && (isJSSpace(runes[0]) || isJSSpace(runes[len(runes)-1]))
}

func isControlOrInvisible(r rune) bool {
	return unicode.In(r, unicode.Cc, unicode.Cf, unicode.Cs, unicode.Zl, unicode.Zp)
}

func isDeviceName(segment string) bool {
	stem, _, _ := strings.Cut(segment, ".")
	return deviceName.MatchString(strings.TrimRightFunc(stem, isJSSpace))
}

// Unstorable counts the selected paths a server would always refuse, by reason. Of names that differ only in case, in the file
// or any folder above it, one can be stored, and the rest get a conflict every time. Two files with one composed spelling are
// the same case: they would be one document, so the second would replace the first.
func Unstorable(names []string) map[string]int {
	counts := map[string]int{}
	spellings := map[string]string{}
	sent := map[string]bool{}
	for _, name := range names {
		problem := PathProblem(name)
		path := ServerPath(name)
		if problem == "" && (sent[path] || conflictsInCase(spellings, path)) {
			problem = ReasonCaseConflict
		}
		sent[path] = true
		if problem != "" {
			counts[problem]++
		}
	}
	return counts
}

// conflictsInCase notes each prefix's spelling, and reports a prefix already seen spelled otherwise.
func conflictsInCase(spellings map[string]string, sent string) bool {
	segments := strings.Split(sent, "/")
	for i := range segments {
		prefix := strings.Join(segments[:i+1], "/")
		folded := FoldName(prefix)
		if seen, known := spellings[folded]; known && seen != prefix {
			return true
		}
		spellings[folded] = prefix
	}
	return false
}
