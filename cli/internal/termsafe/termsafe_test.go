package termsafe

import (
	"strings"
	"testing"
	"unicode"
)

func TestText(t *testing.T) {
	cases := []struct{ name, in, want string }{
		{"plain", "hello world", "hello world"},
		{"keeps newlines and tabs", "a\n\tb\n", "a\n\tb\n"},
		{"keeps unicode", "café 日本語 \U0001F600", "café 日本語 \U0001F600"},
		{"csi colour", "\x1b[31mred\x1b[0m", "red"},
		{"csi clear screen", "a\x1b[2Jb", "ab"},
		{"osc 52 clipboard", "a\x1b]52;c;ZXZpbA==\x07b", "ab"},
		{"osc 8 hyperlink st", "\x1b]8;;http://evil\x1b\\link\x1b]8;;\x1b\\", "link"},
		{"dcs", "a\x1bPq#0;2;0\x1b\\b", "ab"},
		{"apc", "a\x1b_payload\x1b\\b", "ab"},
		{"two byte escape", "a\x1bcb", "ab"},
		{"c1 csi", "a\u009b2Jb", "ab"},
		{"c1 osc", "a\u009d52;c;x\u009cb", "ab"},
		{"raw 8-bit csi byte", "a\x9b2Jb", "a2Jb"},
		{"unterminated osc keeps text inert", "a\x1b]52;c;x", "a52;c;x"},
		{"trailing esc", "a\x1b", "a"},
		{"cr overwrite", "safe\rEVIL", "safe\nEVIL"},
		{"crlf", "a\r\nb", "a\nb"},
		{"backspace", "a\bb", "ab"},
		{"bell and nul", "a\x07\x00b", "ab"},
		{"del", "a\x7fb", "ab"},
		{"bidi override", "a" + string(rune(0x202e)) + "b" + string(rune(0x2066)) + "c", "abc"},
		{"line separator", "a b", "a\nb"},
		{"tag characters", "a\U000E0041b", "ab"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			if got := Text(c.in); got != c.want {
				t.Fatalf("Text(%q) = %q, want %q", c.in, got, c.want)
			}
		})
	}
}

func TestLineCollapsesWhitespace(t *testing.T) {
	if got := Line("a\nb\t c\r\n\x1b[2Jd"); got != "a b c d" {
		t.Fatalf("got %q", got)
	}
}

// Whatever comes in, nothing a terminal treats as control may come out.
func FuzzTextHasNoControls(f *testing.F) {
	for _, seed := range []string{"\x1b[31m", "\x1b]0;x\x07", "\u009b1m", "\xff\xfe", "\x1b\x1b[", "a\r\nb"} {
		f.Add(seed)
	}
	f.Fuzz(func(t *testing.T, in string) {
		out := Text(in)
		for _, r := range out {
			if r != '\n' && r != '\t' && (unicode.IsControl(r) || isStripped(r)) {
				t.Fatalf("Text(%q) kept %U in %q", in, r, out)
			}
		}
		if strings.ContainsRune(out, '\r') {
			t.Fatalf("Text(%q) kept CR", in)
		}
	})
}
