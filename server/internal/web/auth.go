// Phase 7 — password hashing and random identifiers.
//
// argon2id with the RFC 9106 SECOND recommended parameters (m=19 MiB,
// t=2, p=1): the first set (64 MiB) OOM-killed the server inside the kind
// pod's 128Mi limit on the very first registration — a password hash must
// fit the smallest pod this binary runs in. The encoded form is
// self-describing, so parameters can change again without a migration and
// old hashes keep verifying. The store carries only this string; a
// password never leaves this file unhashed.

package web

import (
	"crypto/rand"
	"crypto/subtle"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"net/http"
	"strings"
	"time"

	"golang.org/x/crypto/argon2"
)

const (
	argonTime    = 2
	argonMemory  = 19 * 1024
	argonThreads = 1
	argonKeyLen  = 32
	argonSaltLen = 16
)

// dummyHash is verified when the email is unknown, so timing does not say
// which half of "wrong email or password" it was. Built from the SAME
// parameters as a real hash: a literal with m=65536 cost 64 MiB per unknown
// email and OOM-killed the 128Mi pod the first time a harness tried one
// (2026-10-06), the way the first registration did in Phase 7.
var dummyHash = func() string {
	h, err := hashPassword("not a password anyone has")
	if err != nil {
		panic(err) // crypto/rand failing at startup is not something to limp past
	}
	return h
}()

// kdfSlots caps argon2 calls process-wide. The per-IP login bucket bounds one
// address, but N addresses in parallel cost N x 19 MiB inside a 128Mi pod.
// ponytail: 2 slots = 38 MiB of KDF at most; a caller waits kdfWait for one,
// then gets 503 + Retry-After: 1 instead of queueing without bound.
var (
	kdfSlots = make(chan struct{}, 2)
	kdfWait  = 2 * time.Second
	kdfHeld  = func() {} // test hook: runs while a slot is held
)

// kdfGate takes a slot, or writes 503 and returns nil. Every handler that
// hashes or verifies (the dummy-hash path included) goes through it, so the
// unknown-email timing stays the same as a known one.
func kdfGate(w http.ResponseWriter) (release func()) {
	t := time.NewTimer(kdfWait)
	defer t.Stop()
	select {
	case kdfSlots <- struct{}{}:
	case <-t.C:
		w.Header().Set("Retry-After", "1")
		http.Error(w, "busy, try again", http.StatusServiceUnavailable)
		return nil
	}
	kdfHeld()
	return func() { <-kdfSlots }
}

func hashPassword(pw string) (string, error) {
	salt := make([]byte, argonSaltLen)
	if _, err := rand.Read(salt); err != nil {
		return "", fmt.Errorf("web: salt: %w", err)
	}
	key := argon2.IDKey([]byte(pw), salt, argonTime, argonMemory, argonThreads, argonKeyLen)
	return fmt.Sprintf("$argon2id$v=19$m=%d,t=%d,p=%d$%s$%s",
		argonMemory, argonTime, argonThreads,
		base64.RawStdEncoding.EncodeToString(salt),
		base64.RawStdEncoding.EncodeToString(key)), nil
}

func verifyPassword(pw, encoded string) bool {
	parts := strings.Split(encoded, "$")
	if len(parts) != 6 || parts[1] != "argon2id" {
		return false
	}
	var m uint32
	var t uint32
	var p uint8
	if _, err := fmt.Sscanf(parts[3], "m=%d,t=%d,p=%d", &m, &t, &p); err != nil {
		return false
	}
	salt, err := base64.RawStdEncoding.DecodeString(parts[4])
	if err != nil {
		return false
	}
	want, err := base64.RawStdEncoding.DecodeString(parts[5])
	if err != nil {
		return false
	}
	got := argon2.IDKey([]byte(pw), salt, t, m, p, uint32(len(want)))
	return subtle.ConstantTimeCompare(got, want) == 1
}

// randomHex is ids, session cookies and game tokens: 128 bits.
func randomHex() (string, error) {
	b := make([]byte, 16)
	if _, err := rand.Read(b); err != nil {
		return "", fmt.Errorf("web: random: %w", err)
	}
	return hex.EncodeToString(b), nil
}
