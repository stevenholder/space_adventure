package server

import (
	"strings"
	"testing"
)

func TestSanitizeName(t *testing.T) {
	a24 := strings.Repeat("a", 24)
	cases := []struct {
		in   string
		id   uint32
		want string
	}{
		{"Player One", 1, "Player One"},
		{"", 7, "Player 7"},               // empty → fallback with entity id
		{"   ", 3, "Player 3"},            // whitespace-only → empty → fallback
		{"  hi  ", 1, "hi"},               // trimmed
		{"a\nb\tc", 1, "abc"},             // ASCII controls stripped
		{"a\x7fb\x80c\x9fc", 1, "abcc"},   // DEL + C1 controls stripped
		{"héllo wörld", 1, "héllo wörld"}, // valid multi-byte UTF-8 kept
		{"ab\xffcd", 1, "abcd"},           // invalid byte dropped, neighbors kept
		{"\xff\xfe", 9, "Player 9"},       // all-invalid → fallback
		{a24, 1, a24},                     // exactly 24 bytes kept
		{a24 + "a", 1, a24},               // 25 → cut to 24
		// 23 ASCII + "é" (2 bytes) = 25 bytes; the cut must land on the
		// rune boundary, dropping the é rather than splitting it.
		{strings.Repeat("a", 23) + "é", 1, strings.Repeat("a", 23)},
		// 22 ASCII + "é" = 24 bytes: fits exactly, é kept.
		{strings.Repeat("a", 22) + "é", 1, strings.Repeat("a", 22) + "é"},
		// 29 a's with a control in the middle: strip → 29 a's → cut to 24.
		{strings.Repeat("a", 15) + "\x01" + strings.Repeat("a", 14), 1, a24},
	}
	for _, c := range cases {
		if got := SanitizeName(c.in, c.id); got != c.want {
			t.Errorf("SanitizeName(%q, %d) = %q, want %q", c.in, c.id, got, c.want)
		}
	}
}

// TestSanitizeNameLongMultibyte checks the cut stays on a rune boundary when
// the 24th byte is a continuation byte.
func TestSanitizeNameLongMultibyte(t *testing.T) {
	// 26 ASCII + 3-byte rune: 29 bytes, cut at 24 → 24 ASCII kept.
	got := SanitizeName("0123456789abcdef0123456789中", 1)
	if len(got) != 24 {
		t.Fatalf("len = %d, want 24 (%q)", len(got), got)
	}
	// 22 ASCII + 3-byte rune = 25 → cut back to 22 (would split the rune).
	got = SanitizeName("0123456789abcdef012345中", 1)
	if len(got) != 22 {
		t.Fatalf("len = %d, want 22 (%q)", len(got), got)
	}
}
