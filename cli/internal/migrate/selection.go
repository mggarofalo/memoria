package migrate

import (
	"errors"
	"path"
	"regexp"
	"strings"

	"github.com/bmatcuk/doublestar/v4"
)

// A pattern may hold only what is matched here the same way QMD's matcher does: names, '*', '?', a whole-segment
// '**', and {a,b}. Extglobs, negation, brackets, escapes and ranges are refused rather than guessed at, because a
// wrong guess is silent: a count of 0, or a file sent that the owner excluded.
var (
	supportedGlob = regexp.MustCompile(`^[\p{L}\p{N} _./*?{},-]+$`)
	braceList     = regexp.MustCompile(`\{[^{}]*,[^{}]*\}`)
)

// Selection decides which files in a collection QMD would index. The push must use this too, so that what is
// counted and what is sent are the same files.
type Selection struct {
	patterns []string
	ignore   []string
}

// NewSelection reads a collection's pattern and ignore rules as QMD does, or refuses them.
func NewSelection(pattern string, ignore []string) (*Selection, error) {
	selection := &Selection{}
	for _, part := range splitPattern(pattern) {
		normal, err := normalize(part)
		if err != nil {
			return nil, err
		}
		selection.patterns = append(selection.patterns, normal)
	}
	if len(selection.patterns) == 0 {
		return nil, errors.New("it has no pattern")
	}
	for _, rule := range ignore {
		normal, err := normalizeIgnore(rule)
		if err != nil {
			return nil, err
		}
		selection.ignore = append(selection.ignore, normal)
	}
	return selection, nil
}

func normalizeIgnore(rule string) (string, error) {
	// QMD does not split an ignore rule on commas. An entry holding one outside braces means something else, and isSupported refuses it.
	if strings.TrimSpace(rule) != rule || strings.HasSuffix(rule, "/") {
		return "", refused(rule)
	}
	return normalize(rule)
}

func normalize(glob string) (string, error) {
	normal := strings.TrimPrefix(glob, "./")
	if !isSupported(normal) {
		return "", refused(glob)
	}
	return normal, nil
}

func isSupported(glob string) bool {
	if !supportedGlob.MatchString(glob) || strings.HasPrefix(glob, "/") || strings.Contains(glob, "..") || !doublestar.ValidatePattern(glob) {
		return false
	}
	for _, segment := range strings.Split(glob, "/") {
		if segment == "" || (strings.Contains(segment, "**") && segment != "**") {
			return false
		}
	}
	return !hasBareBraceOrComma(glob)
}

// hasBareBraceOrComma finds {x}, which the two matchers read differently, and a comma outside a list.
func hasBareBraceOrComma(glob string) bool {
	rest := glob
	for braceList.MatchString(rest) {
		rest = braceList.ReplaceAllString(rest, "")
	}
	return strings.ContainsAny(rest, "{},")
}

func refused(glob string) error {
	return errors.New("its pattern or ignore rule " + quoteRule(glob) + " is one this cannot read the way QMD would, so it is refused rather than guessed at")
}

// quoteRule keeps a rule on one line. A rule is the owner's own text, and holds no document's text.
func quoteRule(glob string) string {
	return "\"" + strings.Join(strings.Fields(glob), " ") + "\""
}

// Takes reports whether QMD would index this slash-separated relative path. An ignore rule matching a folder
// also covers what is inside it, as QMD's matcher stops walking there.
func (s *Selection) Takes(name string) bool {
	if !matchesAny(s.patterns, name) {
		return false
	}
	for current := name; current != "."; current = path.Dir(current) {
		if matchesAny(s.ignore, current) {
			return false
		}
	}
	return true
}

func matchesAny(patterns []string, name string) bool {
	for _, pattern := range patterns {
		if doublestar.MatchUnvalidated(pattern, name) {
			return true
		}
	}
	return false
}

// splitPattern splits on commas outside braces, trims each part and drops empty ones, as QMD does.
func splitPattern(pattern string) []string {
	var parts []string
	depth, start := 0, 0
	for i, c := range pattern {
		switch {
		case c == '{':
			depth++
		case c == '}' && depth > 0:
			depth--
		case c == ',' && depth == 0:
			parts = appendPart(parts, pattern[start:i])
			start = i + 1
		}
	}
	return appendPart(parts, pattern[start:])
}

func appendPart(parts []string, part string) []string {
	if trimmed := strings.TrimSpace(part); trimmed != "" {
		return append(parts, trimmed)
	}
	return parts
}
