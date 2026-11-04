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
	"unicode/utf8"
)

const esc = 0x1b

// Text sanitizes multi-line content. Newlines and tabs survive; CRLF and lone
// CR become LF so a line cannot overwrite the one before it.
func Text(s string) string {
	var b strings.Builder
	b.Grow(len(s))
	for i := 0; i < len(s); {
		r, size := utf8.DecodeRuneInString(s[i:])
		if n := skipLength(s[i:], r, size); n > 0 {
			i += n
			continue
		}
		b.WriteString(replacement(s[i:i+size], r))
		i += size
	}
	return b.String()
}

// skipLength returns how many bytes at the start of s to drop outright, or zero
// if the rune there is handled by replacement.
func skipLength(s string, r rune, size int) int {
	switch {
	case r == utf8.RuneError && size == 1:
		// Invalid UTF-8. A raw 0x9B is an 8-bit CSI on some terminals.
		return 1
	case r == esc:
		return escapeLength(s)
	case r == 0x9b:
		return size + csiBodyLength(s[size:])
	case r == 0x90, r == 0x98, r == 0x9d, r == 0x9e, r == 0x9f:
		return size + stringBodyLength(s[size:])
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

// Line sanitizes text destined for one line, such as a table cell or an error
// message: whitespace runs, including newlines, collapse to one space.
func Line(s string) string {
	return strings.Join(strings.Fields(Text(s)), " ")
}

// isStripped reports control and formatting runes with no place in output: C0,
// DEL and C1 controls, bidirectional overrides that make text read differently
// than it is stored, and invisible Unicode tag characters.
func isStripped(r rune) bool {
	switch {
	case r < 0x20, r == 0x7f, r >= 0x80 && r <= 0x9f:
		return true
	case r >= 0x202a && r <= 0x202e, r >= 0x2066 && r <= 0x2069, r == 0x200e, r == 0x200f:
		return true
	case r >= 0xe0000 && r <= 0xe007f:
		return true
	}
	return false
}

// escapeLength returns how many bytes of s, which starts with ESC, form one
// escape sequence.
func escapeLength(s string) int {
	if len(s) < 2 {
		return 1
	}
	switch s[1] {
	case '[':
		return 2 + csiBodyLength(s[2:])
	case ']', 'P', 'X', '^', '_':
		return 2 + stringBodyLength(s[2:])
	}
	if s[1] >= 0x20 && s[1] <= 0x7e {
		return 2
	}
	return 1
}

// csiBodyLength measures CSI parameters and the final byte. An unterminated or
// malformed sequence removes only what was recognized; with ESC gone the rest
// is inert text.
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
func stringBodyLength(s string) int {
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
	return 0
}
