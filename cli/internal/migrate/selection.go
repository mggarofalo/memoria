package migrate

import (
	"errors"
	"path"
	"regexp"
	"strings"

	"github.com/bmatcuk/doublestar/v4"
)

// A pattern may hold only what is matched here the same way QMD's matcher does: names, which may hold + & @ # $ ~,
// '*', '?', a whole-segment '**', and {a,b}. Extglobs, negation, brackets, escapes and ranges are refused rather than guessed at, because a
// wrong guess is silent: a count of 0, or a file sent that the owner excluded.
var (
	supportedGlob = regexp.MustCompile(`^[\p{L}\p{N} _./*?{},+&@#$~-]+$`)
	braceList     = regexp.MustCompile(`\{[^{}]*,[^{}]*\}`)
	// An empty choice in an ignore rule would expand to a rule with an empty end.
	emptyAlternative = regexp.MustCompile(`\{,|,\}|,,`)
)

// Selection decides which files in a collection QMD would index. The push must use this too, so that what is
// counted and what is sent are the same files.
type Selection struct {
	patterns []string
	ignore   []string
	// pruning is the ignore rules that also stop QMD walking into a folder they match. See prunesFolders.
	pruning []string
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
		for _, each := range expandBraces(normal) {
			selection.ignore = append(selection.ignore, each)
			if prunesFolders(each) {
				selection.pruning = append(selection.pruning, each)
			}
		}
	}
	return selection, nil
}

func normalizeIgnore(rule string) (string, error) {
	// QMD does not split an ignore rule on commas. An entry holding one outside braces means something else, and isSupported refuses it.
	if strings.TrimSpace(rule) != rule || strings.HasSuffix(rule, "/") || emptyAlternative.MatchString(rule) {
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
	return segmentsAreSupported(glob) && !hasBareBraceOrComma(glob)
}

func segmentsAreSupported(glob string) bool {
	// "s*/**" takes a file called sx.md here, but not in QMD's matcher, though "?x.md/**" does in both.
	if strings.HasSuffix(glob, "*/**") && !strings.HasSuffix(glob, "**/**") {
		return false
	}
	for _, segment := range strings.Split(glob, "/") {
		if segment == "" || (strings.Contains(segment, "**") && segment != "**") {
			return false
		}
	}
	return true
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

// prunesFolders is fast-glob's rule: QMD's matcher stops walking into a matching folder only for a rule ending in
// /** or in a literal name. Any other rule is tested against files alone, so "s*" leaves everything inside sub.
func prunesFolders(rule string) bool {
	return strings.HasSuffix(rule, "/**") || !strings.ContainsAny(path.Base(rule), "*?")
}

// expandBraces gives one rule for each choice, as QMD's matcher does before it looks at how a rule ends.
func expandBraces(glob string) []string {
	found := braceList.FindStringIndex(glob)
	if found == nil {
		return []string{glob}
	}
	var rules []string
	for _, choice := range strings.Split(glob[found[0]+1:found[1]-1], ",") {
		rules = append(rules, expandBraces(glob[:found[0]]+choice+glob[found[1]:])...)
	}
	return rules
}

// Takes reports whether QMD would index this slash-separated relative path.
func (s *Selection) Takes(name string) bool {
	if !matchesAny(s.patterns, name) || matchesAny(s.ignore, name) {
		return false
	}
	for folder := path.Dir(name); folder != "."; folder = path.Dir(folder) {
		if matchesAny(s.pruning, folder) {
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
