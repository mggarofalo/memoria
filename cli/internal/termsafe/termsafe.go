// Package termsafe strips what a terminal would act on from untrusted text.
//
// Document bodies and snippets come from transcripts, and transcripts capture
// raw terminal output. A stored OSC 52 sequence can write the clipboard, a CSI
// sequence can rewrite what is on screen, and a bare carriage return can
// overwrite a line. None of that should fire because someone read a search
// result.
package termsafe

import (
	"strings"
	"unicode"
	"unicode/utf8"
)

const esc = 0x1b

// scanner walks one input. noTerminator remembers that a search for a string
// terminator reached the end of the input, because then none exists further on
// either. Without it, input repeating an unterminated OSC introducer rescans the
// remainder each time, which takes quadratic time on a hostile document.
type scanner struct {
	noTerminator bool
}

// Text sanitizes multi-line content. Newlines and tabs survive; CRLF and lone
// CR become LF so a line cannot overwrite the one before it. It runs in time
// linear in the input.
func Text(s string) string {
	var b strings.Builder
	b.Grow(len(s))
	var sc scanner
	for i := 0; i < len(s); {
		r, size := utf8.DecodeRuneInString(s[i:])
		if n := sc.skipLength(s[i:], r, size); n > 0 {
			i += n
			continue
		}
		b.WriteString(replacement(s[i:i+size], r))
		i += size
	}
	return b.String()
}

// Line sanitizes text destined for one line, such as a table cell or an error
// message: whitespace runs, including newlines, collapse to one space.
func Line(s string) string {
	return strings.Join(strings.Fields(Text(s)), " ")
}

// skipLength returns how many bytes at the start of s to drop outright, or zero
// if the rune there is handled by replacement.
func (sc *scanner) skipLength(s string, r rune, size int) int {
	switch {
	case r == utf8.RuneError && size == 1:
		// Invalid UTF-8. A raw 0x9B is an 8-bit CSI on some terminals.
		return 1
	case r == esc:
		return sc.escapeLength(s)
	case r == 0x9b:
		return size + csiBodyLength(s[size:])
	case r == 0x90, r == 0x98, r == 0x9d, r == 0x9e, r == 0x9f:
		return size + sc.stringBodyLength(s[size:])
	case r == '\r' && strings.HasPrefix(s[size:], "\n"):
		return size
	}
	return 0
}

// replacement maps one rune to its output form.
func replacement(raw string, r rune) string {
	switch {
	case r == '\r', r == 0x2028, r == 0x2029:
		return "\n"
	case r == '\n', r == '\t':
		return raw
	case isStripped(r):
		return ""
	}
	return raw
}

// isStripped reports runes with no place in output: every control and format
// character (category Cc and Cf, which covers bidirectional overrides, zero-width
// characters and invisible tag characters) and variation selectors.
func isStripped(r rune) bool {
	switch {
	case unicode.Is(unicode.Cc, r), unicode.Is(unicode.Cf, r):
		return true
	case r >= 0xfe00 && r <= 0xfe0f, r >= 0xe0100 && r <= 0xe01ef:
		return true
	}
	return false
}

// escapeLength returns how many bytes of s, which starts with ESC, form one
// escape sequence.
func (sc *scanner) escapeLength(s string) int {
	if len(s) < 2 {
		return 1
	}
	switch s[1] {
	case '[':
		return 2 + csiBodyLength(s[2:])
	case ']', 'P', 'X', '^', '_':
		return 2 + sc.stringBodyLength(s[2:])
	}
	if s[1] >= 0x20 && s[1] <= 0x7e {
		return 2
	}
	return 1
}

// csiBodyLength measures CSI parameters and the final byte. An unterminated or
// malformed sequence removes only what was recognized; with ESC gone the rest
// is inert text. It stops at the first control byte, so repeated introducers
// cannot make it rescan.
func csiBodyLength(s string) int {
	for i := 0; i < len(s); i++ {
		c := s[i]
		if c >= 0x40 && c <= 0x7e {
			return i + 1
		}
		if c < 0x20 || c > 0x3f {
			return i
		}
	}
	return 0
}

// stringBodyLength measures an OSC, DCS, SOS, PM or APC payload through its
// terminator (BEL, ESC \ or C1 ST). Unterminated payloads stay as inert text
// rather than swallowing the rest of the document.
func (sc *scanner) stringBodyLength(s string) int {
	if sc.noTerminator {
		return 0
	}
	for i := 0; i < len(s); i++ {
		switch {
		case s[i] == 0x07:
			return i + 1
		case s[i] == esc && i+1 < len(s) && s[i+1] == '\\':
			return i + 2
		case s[i] == 0xc2 && i+1 < len(s) && s[i+1] == 0x9c:
			return i + 2
		}
	}
	sc.noTerminator = true
	return 0
}
