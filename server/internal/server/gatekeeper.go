// Phase 6 — the LAN-exposure gatekeeper (ROADMAP tasks 7 and 8; C52).
//
// Until now the server trusted its network: kind-local, one player, every
// connection ours. Traefik on the LAN retires that. Three checks, all
// BEFORE the upgrade completes, so a refused caller costs one HTTP
// response and no goroutines:
//
//   - Origin: a native client (ClientWebSocket, the harnesses) sends no
//     Origin header and is allowed; a BROWSER always sends one, and it
//     must be on the SA_ALLOWED_ORIGINS comma-separated allowlist. This
//     retires the "must change together before public exposure" note the
//     upgrader has carried since Phase 2 — the token's own note stands,
//     per the Deferred table ("real accounts when players other than us").
//   - Per-IP connection cap: one machine gets maxConnsPerIP live sockets;
//     more is either a bug loop or a flood, and both starve the players
//     already seated.
//   - Hello/join rate: joins from one IP are token-bucket limited. A
//     reconnecting client is well inside it; a hammering loop is not.

package server

import (
	"net"
	"net/http"
	"os"
	"strings"
	"sync"
	"time"
)

const (
	// maxConnsPerIP allows a whole harness sweep from one machine (t7
	// holds ten sockets at once) with headroom; the twenty-first live
	// socket from one address is a flood, not a household.
	maxConnsPerIP = 20
	// joinBurst tokens, refilled at joinPerSec: a client crash-looping at
	// 1 Hz reconnects forever; forty joins in one second does not.
	joinBurst  = 20
	joinPerSec = 2.0
)

// gatekeeper tracks per-IP live connections and join tokens. One per
// Server; all methods are safe for concurrent use.
type gatekeeper struct {
	mu    sync.Mutex
	conns map[string]int
	rate  map[string]*bucket
}

type bucket struct {
	tokens float64
	last   time.Time
}

func newGatekeeper() *gatekeeper {
	return &gatekeeper{conns: make(map[string]int), rate: make(map[string]*bucket)}
}

// admit reports whether a new connection from ip may proceed, counting it
// if so. Callers MUST pair every true with a release(ip).
func (g *gatekeeper) admit(ip string, now time.Time) bool {
	g.mu.Lock()
	defer g.mu.Unlock()

	if g.conns[ip] >= maxConnsPerIP {
		return false
	}
	b := g.rate[ip]
	if b == nil {
		b = &bucket{tokens: joinBurst, last: now}
		g.rate[ip] = b
	}
	b.tokens += now.Sub(b.last).Seconds() * joinPerSec
	if b.tokens > joinBurst {
		b.tokens = joinBurst
	}
	b.last = now
	if b.tokens < 1 {
		return false
	}
	b.tokens--
	g.conns[ip]++
	return true
}

func (g *gatekeeper) release(ip string) {
	g.mu.Lock()
	defer g.mu.Unlock()
	if g.conns[ip] > 1 {
		g.conns[ip]--
	} else {
		delete(g.conns, ip)
		// The rate bucket stays until it refills to full and is swept
		// lazily on the next admit from a different state — a map of
		// buckets for a LAN's worth of IPs is not a leak worth code.
	}
}

// clientIP is the caller's address for the per-IP gate, chosen so a
// public client cannot spoof its way past the caps:
//
//   - CF-Connecting-IP when present: the public path is Cloudflare-
//     proxied, and this is the one header Cloudflare sets to the real
//     client, unforgeable through that path.
//   - else the LAST X-Forwarded-For entry: appended by Traefik, i.e. the
//     peer Traefik actually saw. The first entry is CLIENT-SUPPLIED and
//     trusting it (as this function first did) let anyone rotate fake
//     addresses to dodge the rate limit.
//   - else RemoteAddr (direct connections, kind, tests).
func clientIP(r *http.Request) string {
	if cf := strings.TrimSpace(r.Header.Get("CF-Connecting-IP")); cf != "" {
		return cf
	}
	if xff := r.Header.Get("X-Forwarded-For"); xff != "" {
		parts := strings.Split(xff, ",")
		return strings.TrimSpace(parts[len(parts)-1])
	}
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		return r.RemoteAddr
	}
	return host
}

// checkOrigin is the upgrader's CheckOrigin: no Origin header (native
// clients) passes; a browser origin must be allowlisted.
func checkOrigin(r *http.Request) bool {
	origin := r.Header.Get("Origin")
	if origin == "" {
		return true
	}
	for _, allowed := range strings.Split(os.Getenv("SA_ALLOWED_ORIGINS"), ",") {
		if allowed != "" && strings.EqualFold(strings.TrimSpace(allowed), origin) {
			return true
		}
	}
	return false
}
