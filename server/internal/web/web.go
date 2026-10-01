// Phase 7 — the account site and its API, served from the game server
// binary (docs/ROADMAP.md Phase 7). The site itself is embedded plain
// HTML/CSS/JS — no build toolchain, C47's spirit applied to the web.
//
// Security posture, all enforced here:
//   - sessions are HttpOnly SameSite=Strict cookies backed by web_session
//     rows (revocable, restart-proof);
//   - every mutating route requires the X-Requested-With header, which a
//     cross-site form cannot set (CSRF);
//   - login and register ride a per-IP token bucket (its own small copy —
//     the gatekeeper's is unexported in another package, and two tiny
//     limiters beat an export coupling the web to the WS gateway);
//   - passwords are argon2id at rest and never logged.

package web

import (
	"embed"
	"encoding/json"
	"errors"
	"io/fs"
	"net"
	"net/http"
	"strings"
	"sync"
	"time"

	"space-adventure/server/internal/store"
)

//go:embed site
var siteFS embed.FS

const (
	sessionCookie  = "sa_session"
	sessionTTL     = 30 * 24 * time.Hour
	linkCodeTTL    = 10 * time.Minute
	minPasswordLen = 8
)

// Handler is the account site. Store is required; NewPlayer builds the
// default player row for a freshly minted account token (the server owns
// what a new player starts with); Online reports live connections for the
// landing page.
type Handler struct {
	Store     *store.Store
	NewPlayer func(token, name string) store.Player
	Online    func() int

	mu   sync.Mutex
	rate map[string]*loginBucket
}

type loginBucket struct {
	tokens float64
	last   time.Time
}

// Mount registers every route on mux.
func (h *Handler) Mount(mux *http.ServeMux) {
	h.rate = make(map[string]*loginBucket)
	site, _ := fs.Sub(siteFS, "site")
	mux.Handle("/", http.FileServer(http.FS(site)))

	mux.Handle("/download/", newDownloads())
	mux.HandleFunc("/api/stats", h.stats)
	mux.HandleFunc("/api/register", h.mutating(h.limited(h.register)))
	mux.HandleFunc("/api/login", h.mutating(h.limited(h.login)))
	mux.HandleFunc("/api/logout", h.mutating(h.withAccount(h.logout)))
	mux.HandleFunc("/api/me", h.withAccount(h.me))
	mux.HandleFunc("/api/link-code", h.mutating(h.withAccount(h.linkCode)))
	mux.HandleFunc("/api/import-token", h.mutating(h.withAccount(h.importToken)))
	mux.HandleFunc("/api/password", h.mutating(h.withAccount(h.password)))
	mux.HandleFunc("/api/delete", h.mutating(h.withAccount(h.deleteAccount)))
	mux.HandleFunc("/api/redeem", h.mutating(h.redeem)) // the game client; no session
}

// ---- middleware ------------------------------------------------------------

type accountCtx struct {
	id      string
	session string
}

func (h *Handler) mutating(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}
		if r.Header.Get("X-Requested-With") == "" {
			http.Error(w, "missing X-Requested-With", http.StatusForbidden)
			return
		}
		next(w, r)
	}
}

// limited is the login/register bucket: burst 5, refill 1 per 5 s per IP.
func (h *Handler) limited(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		ip := webClientIP(r)
		h.mu.Lock()
		b := h.rate[ip]
		now := time.Now()
		if b == nil {
			b = &loginBucket{tokens: 5, last: now}
			h.rate[ip] = b
		}
		b.tokens += now.Sub(b.last).Seconds() * 0.2
		if b.tokens > 5 {
			b.tokens = 5
		}
		b.last = now
		ok := b.tokens >= 1
		if ok {
			b.tokens--
		}
		h.mu.Unlock()
		if !ok {
			http.Error(w, "slow down", http.StatusTooManyRequests)
			return
		}
		next(w, r)
	}
}

func (h *Handler) withAccount(next func(http.ResponseWriter, *http.Request, accountCtx)) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		c, err := r.Cookie(sessionCookie)
		if err != nil || c.Value == "" {
			http.Error(w, "not logged in", http.StatusUnauthorized)
			return
		}
		accountID, err := h.Store.GetSession(r.Context(), c.Value, time.Now().UnixMilli())
		if err != nil {
			http.Error(w, "session lookup failed", http.StatusInternalServerError)
			return
		}
		if accountID == "" {
			http.Error(w, "not logged in", http.StatusUnauthorized)
			return
		}
		next(w, r, accountCtx{id: accountID, session: c.Value})
	}
}

// webClientIP mirrors the gatekeeper's rule: CF-Connecting-IP, else the
// LAST X-Forwarded-For hop, else RemoteAddr. Same reasoning, same order.
func webClientIP(r *http.Request) string {
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

// ---- handlers --------------------------------------------------------------

func (h *Handler) stats(w http.ResponseWriter, r *http.Request) {
	players, err := h.Store.CountPlayers(r.Context())
	if err != nil {
		http.Error(w, "stats unavailable", http.StatusInternalServerError)
		return
	}
	writeJSON(w, map[string]any{"players": players, "online": h.Online()})
}

type credsReq struct {
	Email    string `json:"email"`
	Password string `json:"password"`
}

func (h *Handler) register(w http.ResponseWriter, r *http.Request) {
	var req credsReq
	if !readJSON(w, r, &req) {
		return
	}
	req.Email = strings.ToLower(strings.TrimSpace(req.Email))
	if !strings.Contains(req.Email, "@") || len(req.Email) < 3 || len(req.Email) > 254 {
		http.Error(w, "that is not an email address", http.StatusBadRequest)
		return
	}
	if len(req.Password) < minPasswordLen {
		http.Error(w, "password too short (8 minimum)", http.StatusBadRequest)
		return
	}
	hash, err := hashPassword(req.Password)
	if err != nil {
		http.Error(w, "hashing failed", http.StatusInternalServerError)
		return
	}
	id, err := randomHex()
	if err != nil {
		http.Error(w, "id failed", http.StatusInternalServerError)
		return
	}
	err = h.Store.CreateAccount(r.Context(), &store.Account{
		ID: id, Email: req.Email, PwHash: hash, CreatedMs: time.Now().UnixMilli(),
	})
	if errors.Is(err, store.ErrEmailTaken) {
		http.Error(w, "email already registered", http.StatusConflict)
		return
	}
	if err != nil {
		http.Error(w, "registration failed", http.StatusInternalServerError)
		return
	}
	h.startSession(w, r, id)
}

func (h *Handler) login(w http.ResponseWriter, r *http.Request) {
	var req credsReq
	if !readJSON(w, r, &req) {
		return
	}
	acc, err := h.Store.GetAccountByEmail(r.Context(), strings.ToLower(strings.TrimSpace(req.Email)))
	if err != nil {
		http.Error(w, "login failed", http.StatusInternalServerError)
		return
	}
	// One code path for wrong email and wrong password: verify against a
	// dummy hash when the account is absent, so timing does not say which.
	hash := "$argon2id$v=19$m=65536,t=1,p=4$AAAAAAAAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
	if acc != nil {
		hash = acc.PwHash
	}
	if !verifyPassword(req.Password, hash) || acc == nil {
		http.Error(w, "wrong email or password", http.StatusUnauthorized)
		return
	}
	h.startSession(w, r, acc.ID)
}

func (h *Handler) startSession(w http.ResponseWriter, r *http.Request, accountID string) {
	sid, err := randomHex()
	if err != nil {
		http.Error(w, "session failed", http.StatusInternalServerError)
		return
	}
	now := time.Now()
	if err := h.Store.CreateSession(r.Context(), sid, accountID,
		now.UnixMilli(), now.Add(sessionTTL).UnixMilli()); err != nil {
		http.Error(w, "session failed", http.StatusInternalServerError)
		return
	}
	http.SetCookie(w, &http.Cookie{
		Name:     sessionCookie,
		Value:    sid,
		Path:     "/",
		MaxAge:   int(sessionTTL.Seconds()),
		HttpOnly: true,
		SameSite: http.SameSiteStrictMode,
		// Secure when the CLIENT's leg is TLS — Cloudflare terminates and
		// forwards the scheme; plain LAN http keeps working without it.
		Secure: r.TLS != nil || r.Header.Get("X-Forwarded-Proto") == "https",
	})
	writeJSON(w, map[string]any{"ok": true})
}

func (h *Handler) logout(w http.ResponseWriter, r *http.Request, a accountCtx) {
	if err := h.Store.DeleteSession(r.Context(), a.session); err != nil {
		http.Error(w, "logout failed", http.StatusInternalServerError)
		return
	}
	http.SetCookie(w, &http.Cookie{Name: sessionCookie, Value: "", Path: "/", MaxAge: -1})
	writeJSON(w, map[string]any{"ok": true})
}

func (h *Handler) me(w http.ResponseWriter, r *http.Request, a accountCtx) {
	acc, err := h.Store.GetAccount(r.Context(), a.id)
	if err != nil || acc == nil {
		http.Error(w, "account lookup failed", http.StatusInternalServerError)
		return
	}
	players, err := h.Store.AccountPlayers(r.Context(), a.id)
	if err != nil {
		http.Error(w, "player lookup failed", http.StatusInternalServerError)
		return
	}
	type playerView struct {
		Name      string        `json:"name"`
		Credits   int64         `json:"credits"`
		Inventory []store.Stack `json:"inventory"`
		LastSeen  int64         `json:"last_seen_ms"`
	}
	views := make([]playerView, 0, len(players))
	for _, p := range players {
		views = append(views, playerView{Name: p.Name, Credits: p.Credits,
			Inventory: p.Inventory, LastSeen: p.UpdatedMs})
	}
	writeJSON(w, map[string]any{"email": acc.Email, "players": views})
}

func (h *Handler) linkCode(w http.ResponseWriter, r *http.Request, a accountCtx) {
	code, err := randomCode()
	if err != nil {
		http.Error(w, "code failed", http.StatusInternalServerError)
		return
	}
	now := time.Now()
	if err := h.Store.PutLinkCode(r.Context(), code, a.id,
		now.UnixMilli(), now.Add(linkCodeTTL).UnixMilli()); err != nil {
		http.Error(w, "code failed", http.StatusInternalServerError)
		return
	}
	writeJSON(w, map[string]any{"code": code, "expires_in_s": int(linkCodeTTL.Seconds())})
}

// redeem is the GAME CLIENT's exchange: code in, game token out. The
// account's existing player wins; otherwise a fresh player row is minted
// already owned. No session — the code is the credential.
func (h *Handler) redeem(w http.ResponseWriter, r *http.Request) {
	var req struct {
		Code string `json:"code"`
	}
	if !readJSON(w, r, &req) {
		return
	}
	code := strings.ToUpper(strings.TrimSpace(req.Code))
	accountID, err := h.Store.RedeemLinkCode(r.Context(), code, time.Now().UnixMilli())
	if err != nil {
		http.Error(w, "redeem failed", http.StatusInternalServerError)
		return
	}
	if accountID == "" {
		http.Error(w, "unknown or expired code", http.StatusUnauthorized)
		return
	}
	token, err := h.Store.AccountPlayerToken(r.Context(), accountID)
	if err != nil {
		http.Error(w, "lookup failed", http.StatusInternalServerError)
		return
	}
	if token == "" {
		token, err = randomHex()
		if err != nil {
			http.Error(w, "token failed", http.StatusInternalServerError)
			return
		}
		acc, err := h.Store.GetAccount(r.Context(), accountID)
		if err != nil || acc == nil {
			http.Error(w, "account lookup failed", http.StatusInternalServerError)
			return
		}
		name := strings.SplitN(acc.Email, "@", 2)[0]
		p := h.NewPlayer(token, name)
		if err := h.Store.PutPlayer(r.Context(), &p); err != nil {
			http.Error(w, "player mint failed", http.StatusInternalServerError)
			return
		}
		if err := h.Store.SetPlayerAccount(r.Context(), token, accountID); err != nil {
			http.Error(w, "player bind failed", http.StatusInternalServerError)
			return
		}
	}
	writeJSON(w, map[string]any{"token": token})
}

func (h *Handler) importToken(w http.ResponseWriter, r *http.Request, a accountCtx) {
	var req struct {
		Token string `json:"token"`
	}
	if !readJSON(w, r, &req) {
		return
	}
	if err := h.Store.AdoptPlayer(r.Context(), a.id, strings.TrimSpace(req.Token)); err != nil {
		http.Error(w, "import refused: "+err.Error(), http.StatusConflict)
		return
	}
	writeJSON(w, map[string]any{"ok": true})
}

func (h *Handler) password(w http.ResponseWriter, r *http.Request, a accountCtx) {
	var req struct {
		Old string `json:"old"`
		New string `json:"new"`
	}
	if !readJSON(w, r, &req) {
		return
	}
	acc, err := h.Store.GetAccount(r.Context(), a.id)
	if err != nil || acc == nil || !verifyPassword(req.Old, acc.PwHash) {
		http.Error(w, "wrong password", http.StatusUnauthorized)
		return
	}
	if len(req.New) < minPasswordLen {
		http.Error(w, "password too short (8 minimum)", http.StatusBadRequest)
		return
	}
	hash, err := hashPassword(req.New)
	if err != nil {
		http.Error(w, "hashing failed", http.StatusInternalServerError)
		return
	}
	if err := h.Store.SetPassword(r.Context(), a.id, hash, a.session); err != nil {
		http.Error(w, "change failed", http.StatusInternalServerError)
		return
	}
	writeJSON(w, map[string]any{"ok": true})
}

func (h *Handler) deleteAccount(w http.ResponseWriter, r *http.Request, a accountCtx) {
	var req struct {
		Password string `json:"password"`
	}
	if !readJSON(w, r, &req) {
		return
	}
	acc, err := h.Store.GetAccount(r.Context(), a.id)
	if err != nil || acc == nil || !verifyPassword(req.Password, acc.PwHash) {
		http.Error(w, "wrong password", http.StatusUnauthorized)
		return
	}
	if _, err := h.Store.DeleteAccount(r.Context(), a.id); err != nil {
		http.Error(w, "delete failed", http.StatusInternalServerError)
		return
	}
	http.SetCookie(w, &http.Cookie{Name: sessionCookie, Value: "", Path: "/", MaxAge: -1})
	writeJSON(w, map[string]any{"ok": true})
}

// ---- plumbing --------------------------------------------------------------

func readJSON(w http.ResponseWriter, r *http.Request, v any) bool {
	r.Body = http.MaxBytesReader(w, r.Body, 4096)
	if err := json.NewDecoder(r.Body).Decode(v); err != nil {
		http.Error(w, "bad request body", http.StatusBadRequest)
		return false
	}
	return true
}

func writeJSON(w http.ResponseWriter, v any) {
	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(v)
}
