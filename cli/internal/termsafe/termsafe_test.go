package termsafe

import (
	"strings"
	"testing"
	"time"
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
		{"zero width joiner and space", "a" + string(rune(0x200d)) + "b" + string(rune(0x200b)) + "c", "abc"},
		{"soft hyphen", "a" + string(rune(0xad)) + "b", "ab"},
		{"byte order mark", string(rune(0xfeff)) + "a", "a"},
		{"hangul fillers", "a" + string(rune(0x115f)) + string(rune(0x1160)) + string(rune(0x3164)) + string(rune(0xffa0)) + "b", "ab"},
		{"grapheme joiner", "a" + string(rune(0x034f)) + "b", "ab"},
		{"braille blank", "a" + string(rune(0x2800)) + "b", "ab"},
		{"mongolian selectors", "a" + string(rune(0x180b)) + string(rune(0x180c)) + string(rune(0x180d)) + string(rune(0x180f)) + "b", "ab"},
		{"khmer inherent vowels", "a" + string(rune(0x17b4)) + string(rune(0x17b5)) + "b", "ab"},
		{"variation selectors", "a" + string(rune(0xfe0f)) + "b" + string(rune(0xe0100)) + "c", "abc"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			if got := Text(c.in); got != c.want {
				t.Fatalf("Text(%q) = %q, want %q", c.in, got, c.want)
			}
		})
	}
}

// ZWNJ and ZWJ change how these words are spelled, so they stay between letters of
// scripts that use them, and go everywhere else.
func TestJoiners(t *testing.T) {
	const zwnj, zwj = string(rune(0x200c)), string(rune(0x200d))
	persian := string(rune(0x0645)) + string(rune(0x06cc)) + zwnj + string(rune(0x062e)) + string(rune(0x0648))
	hindi := string(rune(0x0915)) + string(rune(0x094d)) + zwj + string(rune(0x0937))
	for _, kept := range []string{persian, hindi} {
		if got := Text(kept); got != kept {
			t.Errorf("Text(%q) = %q", kept, got)
		}
	}
	arabic := string(rune(0x0645))
	for in, want := range map[string]string{
		"pass" + zwj + "word": "password",
		arabic + zwnj + "a":   arabic + "a",
		"a" + zwnj + arabic:   "a" + arabic,
		zwj + arabic:          arabic,
		arabic + zwj:          arabic,
		"👨" + zwj + "👩":       "👨👩",
	} {
		if got := Text(in); got != want {
			t.Errorf("Text(%q) = %q, want %q", in, got, want)
		}
	}
}

func TestLineCollapsesWhitespace(t *testing.T) {
	if got := Line("a\nb\t c\r\n\x1b[2Jd"); got != "a b c d" {
		t.Fatalf("got %q", got)
	}
}

// Before the fix, 1 MiB of unterminated OSC introducers took over 100 seconds.
// The bound is generous because it only has to separate linear from quadratic.
func TestHostileInputStaysLinear(t *testing.T) {
	const size = 8 << 20
	for name, unit := range map[string]string{
		"unterminated OSC":    "\x1b]",
		"unterminated C1 OSC": "\u009d",
		"unterminated DCS":    "\x1bP",
		"unterminated CSI":    "\x1b[1",
		"C1 CSI":              "\u009b1",
		"bare ESC":            "\x1b",
	} {
		t.Run(name, func(t *testing.T) {
			input := strings.Repeat(unit, size/len(unit))
			done := make(chan string, 1)
			go func() { done <- Text(input) }()
			select {
			case <-done:
			case <-time.After(30 * time.Second):
				t.Fatal("sanitizing did not finish in time")
			}
		})
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
			if r == 0x200c || r == 0x200d {
				continue // Kept between letters of some scripts; see TestJoiners.
			}
			if r != '\n' && r != '\t' && (unicode.IsControl(r) || isStripped(r)) {
				t.Fatalf("Text(%q) kept %U in %q", in, r, out)
			}
		}
		if strings.ContainsRune(out, '\r') {
			t.Fatalf("Text(%q) kept CR", in)
		}
	})
}
