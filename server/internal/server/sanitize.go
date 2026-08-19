package server

import (
	"fmt"
	"strings"
	"unicode/utf8"
)

// maxNameBytes is the PROTOCOL display-name limit (≤ 24 bytes).
const maxNameBytes = 24

// SanitizeName applies the server-side name rules of PROTOCOL "Player
// identity" to the untrusted hello.name: drop invalid UTF-8 bytes and
// control characters, trim, cap at 24 bytes on a rune boundary, and replace
// an empty result with "Player <entity_id>". The result is what every
// client renders, so it must be safe and stable.
func SanitizeName(raw string, id uint32) string {
	var b strings.Builder
	b.Grow(len(raw))
	for i := 0; i < len(raw); {
		r, size := utf8.DecodeRuneInString(raw[i:])
		if r == utf8.RuneError && size == 1 {
			i++ // invalid byte: drop
			continue
		}
		// ASCII controls + C1 controls.
		if r < 0x20 || (r >= 0x7f && r <= 0x9f) {
			i += size
			continue
		}
		b.WriteRune(r)
		i += size
	}
	s := strings.TrimSpace(b.String())
	if len(s) > maxNameBytes {
		cut := maxNameBytes
		for cut > 0 && !utf8.RuneStart(s[cut]) {
			cut--
		}
		s = s[:cut]
	}
	if s == "" {
		return fmt.Sprintf("Player %d", id)
	}
	return s
}
