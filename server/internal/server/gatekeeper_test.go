package server

import (
	"net/http"
	"testing"
	"time"
)

func TestGatekeeperConnCap(t *testing.T) {
	g := newGatekeeper()
	now := time.Now()
	for i := 0; i < maxConnsPerIP; i++ {
		// Space admits a second apart so the rate bucket never interferes
		// with what this test measures.
		now = now.Add(time.Second)
		if !g.admit("10.0.0.1", now) {
			t.Fatalf("admit %d refused under the cap", i+1)
		}
	}
	if g.admit("10.0.0.1", now.Add(time.Second)) {
		t.Fatal("admit over the cap succeeded")
	}
	// A different address is unaffected.
	if !g.admit("10.0.0.2", now) {
		t.Fatal("second IP refused")
	}
	// Releasing one frees one slot.
	g.release("10.0.0.1")
	if !g.admit("10.0.0.1", now.Add(2*time.Second)) {
		t.Fatal("admit after release refused")
	}
}

func TestGatekeeperJoinRate(t *testing.T) {
	g := newGatekeeper()
	now := time.Now()
	admitted := 0
	for i := 0; i < joinBurst*3; i++ {
		if g.admit("10.0.0.9", now) {
			admitted++
			g.release("10.0.0.9") // short-lived: the conn cap never binds
		}
	}
	if admitted != joinBurst {
		t.Fatalf("flood at one instant admitted %d, want the burst %d", admitted, joinBurst)
	}
	// A reconnecting client at 1 Hz is inside the refill forever.
	for i := 0; i < 30; i++ {
		now = now.Add(time.Second)
		if !g.admit("10.0.0.9", now) {
			t.Fatalf("1 Hz reconnect refused at second %d", i)
		}
		g.release("10.0.0.9")
	}
}

func TestCheckOrigin(t *testing.T) {
	t.Setenv("SA_ALLOWED_ORIGINS", "https://game.example, http://lan.local")
	mk := func(origin string) *http.Request {
		r, _ := http.NewRequest("GET", "/ws", nil)
		if origin != "" {
			r.Header.Set("Origin", origin)
		}
		return r
	}
	if !checkOrigin(mk("")) {
		t.Fatal("native client (no Origin) refused")
	}
	if !checkOrigin(mk("https://game.example")) {
		t.Fatal("allowlisted origin refused")
	}
	if !checkOrigin(mk("HTTP://LAN.LOCAL")) {
		t.Fatal("case-folded allowlisted origin refused")
	}
	if checkOrigin(mk("https://evil.example")) {
		t.Fatal("unlisted origin accepted")
	}
	t.Setenv("SA_ALLOWED_ORIGINS", "")
	if checkOrigin(mk("https://game.example")) {
		t.Fatal("browser origin accepted with an empty allowlist")
	}
	if !checkOrigin(mk("")) {
		t.Fatal("native client refused with an empty allowlist")
	}
}

func TestClientIPUnspoofable(t *testing.T) {
	r, _ := http.NewRequest("GET", "/ws", nil)
	r.RemoteAddr = "10.42.0.7:51234"
	if ip := clientIP(r); ip != "10.42.0.7" {
		t.Fatalf("remote addr ip = %q", ip)
	}
	// The LAST XFF entry is Traefik-appended (the peer it saw); the first
	// is client-supplied and must never win — a spoofer sends
	// "X-Forwarded-For: <random>" to rotate identities.
	r.Header.Set("X-Forwarded-For", "6.6.6.6, 192.168.1.50")
	if ip := clientIP(r); ip != "192.168.1.50" {
		t.Fatalf("xff ip = %q, want the Traefik-appended last hop", ip)
	}
	// Behind Cloudflare the real client is CF-Connecting-IP; XFF's last
	// entry would be a Cloudflare edge (useless for per-client limits).
	r.Header.Set("CF-Connecting-IP", "203.0.113.9")
	if ip := clientIP(r); ip != "203.0.113.9" {
		t.Fatalf("cf ip = %q", ip)
	}
}
