// Phase 7 — the account site and its API, served from the game server
// binary (docs/ROADMAP.md Phase 7). The site itself is embedded plain
// HTML/CSS/JS — no build toolchain, C47's spirit applied to the web.
//
// Security posture, all enforced here:
//   - sessions are HttpOnly SameSite=Strict cookies backed by web_session
//     rows (revocable, restart-proof); the game client gets the same row
//     from game-login and sends it as a bearer (Phase 16);
//   - every mutating route requires the X-Requested-With header, which a
//     cross-site form cannot set (CSRF);
//   - login and register ride a per-IP token bucket (its own small copy —
//     the gatekeeper's is unexported in another package, and two tiny
//     limiters beat an export coupling the web to the WS gateway);
//   - passwords are argon2id at rest and never logged.

package web

import (
	"context"
	"embed"
	"encoding/json"
	"errors"
	"io/fs"
	"log"
	"net"
	"net/http"
	"strings"
	"sync"
	"time"
	"unicode"
	"unicode/utf8"

	"space-adventure/server/internal/server"
	"space-adventure/server/internal/store"
)

//go:embed site
var siteFS embed.FS

const (
	sessionCookie  = "sa_session"
	sessionTTL     = 30 * 24 * time.Hour
	minPasswordLen = 8
	maxCharacters  = 5
)

// bodies are the four model + gender ids a character may wear (GDD
// "Characters").
var bodies = map[string]bool{
	store.DefaultBody: true, "char.player.f": true, "char.ubc": true, "char.ubc.f": true,
}

// hairs are the hair piece ids a character may pick (GDD "Faces and hair
// (Phase 17)"): store.DefaultHair plus the `hair.<style>` rows of
// art/manifest.json. The server never reads the manifest at runtime;
// TestHairsMatchManifest fails when the two drift apart.
var hairs = map[string]bool{
	store.DefaultHair: true, "hair.beard": true, "hair.buns": true, "hair.buzzed": true,
	"hair.buzzed_female": true, "hair.long": true, "hair.simple_parted": true,
}

// Handler is the account site. Store is required; NewPlayer builds the
// default player row for a freshly minted character (the server owns
// what a new player starts with); Online reports live connections for the
// landing page.
type Handler struct {
	Store     *store.Store
	NewPlayer func(token, name string) store.Player
	Online    func() int
	// Kick drops live sessions of deleted characters so their saves cannot
	// re-create the rows (server.Kick). Nil: no game server in-process.
	Kick func(tokens []string)

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
	mux.HandleFunc("/api/game-login", h.mutating(h.limited(h.gameLogin))) // the launcher
	mux.HandleFunc("/api/logout", h.mutating(h.withAccount(h.logout)))
	mux.HandleFunc("/api/me", h.withAccount(h.me))
	mux.HandleFunc("/api/characters", h.characters)
	mux.HandleFunc("/api/password", h.mutating(h.withAccount(h.password)))
	mux.HandleFunc("/api/delete", h.mutating(h.withAccount(h.deleteAccount)))
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
		sid := bearer(r)
		if sid == "" {
			if c, err := r.Cookie(sessionCookie); err == nil {
				sid = c.Value
			}
		}
		if sid == "" {
			http.Error(w, "not logged in", http.StatusUnauthorized)
			return
		}
		accountID, err := h.Store.GetSession(r.Context(), sid, time.Now().UnixMilli())
		if err != nil {
			http.Error(w, "session lookup failed", http.StatusInternalServerError)
			return
		}
		if accountID == "" {
			http.Error(w, "not logged in", http.StatusUnauthorized)
			return
		}
		next(w, r, accountCtx{id: accountID, session: sid})
	}
}

// bearer is the game client's session: "Authorization: Bearer <id>". It
// wins over a cookie — a client that sends one meant it.
func bearer(r *http.Request) string {
	const prefix = "bearer "
	a := r.Header.Get("Authorization")
	if len(a) > len(prefix) && strings.EqualFold(a[:len(prefix)], prefix) {
		return strings.TrimSpace(a[len(prefix):])
	}
	return ""
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
	if acc := h.verifyLogin(w, r); acc != nil {
		h.startSession(w, r, acc.ID)
	}
}

// verifyLogin is the shared check behind login and game-login: the account
// on success, nil after it has written the error.
func (h *Handler) verifyLogin(w http.ResponseWriter, r *http.Request) *store.Account {
	var req credsReq
	if !readJSON(w, r, &req) {
		return nil
	}
	acc, err := h.Store.GetAccountByEmail(r.Context(), strings.ToLower(strings.TrimSpace(req.Email)))
	if err != nil {
		http.Error(w, "login failed", http.StatusInternalServerError)
		return nil
	}
	// One code path for wrong email and wrong password: verify against a
	// dummy hash when the account is absent, so timing does not say which.
	hash := dummyHash
	if acc != nil {
		hash = acc.PwHash
	}
	if !verifyPassword(req.Password, hash) || acc == nil {
		http.Error(w, "wrong email or password", http.StatusUnauthorized)
		return nil
	}
	return acc
}

// gameLogin is the launcher's sign-in: the site's session row, returned in
// the body for an Authorization header instead of set as a cookie. It mints
// nothing: a new account has no characters and the character select
// creates the first (GDD "Character select").
func (h *Handler) gameLogin(w http.ResponseWriter, r *http.Request) {
	acc := h.verifyLogin(w, r)
	if acc == nil {
		return
	}
	sid, ok := h.newSession(w, r, acc.ID)
	if !ok {
		return
	}
	writeJSON(w, map[string]any{"session": sid, "name": acc.Email})
}

// mintCharacter makes a character the way a guest starts (NewPlayer), owned
// from its first INSERT: one PutPlayer with AccountID set, so a name the
// index refuses leaves no orphan row. store.ErrNameTaken passes through.
func (h *Handler) mintCharacter(ctx context.Context, acc *store.Account, name, body, hair string) (store.Player, error) {
	token, err := randomHex()
	if err != nil {
		return store.Player{}, err
	}
	p := h.NewPlayer(token, name)
	p.Body = body
	p.Hair = hair
	p.AccountID = acc.ID
	if err := h.Store.PutPlayer(ctx, &p); err != nil {
		return store.Player{}, err
	}
	return p, nil
}

func (h *Handler) startSession(w http.ResponseWriter, r *http.Request, accountID string) {
	sid, ok := h.newSession(w, r, accountID)
	if !ok {
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

// newSession writes the web_session row; false after it has written the
// error.
func (h *Handler) newSession(w http.ResponseWriter, r *http.Request, accountID string) (string, bool) {
	sid, err := randomHex()
	if err != nil {
		http.Error(w, "session failed", http.StatusInternalServerError)
		return "", false
	}
	now := time.Now()
	if err := h.Store.CreateSession(r.Context(), sid, accountID,
		now.UnixMilli(), now.Add(sessionTTL).UnixMilli()); err != nil {
		http.Error(w, "session failed", http.StatusInternalServerError)
		return "", false
	}
	return sid, true
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
		Body      string        `json:"body"`
		Hair      string        `json:"hair"`
		Credits   int64         `json:"credits"`
		Inventory []store.Stack `json:"inventory"`
		LastSeen  int64         `json:"last_seen_ms"`
	}
	views := make([]playerView, 0, len(players))
	for _, p := range players {
		views = append(views, playerView{Name: p.Name, Body: p.Body, Hair: p.Hair, Credits: p.Credits,
			Inventory: p.Inventory, LastSeen: p.UpdatedMs})
	}
	writeJSON(w, map[string]any{"email": acc.Email, "players": views})
}

// characterView is one row of GET and POST /api/characters.
type characterView struct {
	Token    string `json:"token"`
	Name     string `json:"name"`
	Body     string `json:"body"`
	Hair     string `json:"hair"`
	Credits  int64  `json:"credits"`
	LastSeen int64  `json:"last_seen_ms"`
}

func viewCharacter(p store.Player) characterView {
	return characterView{Token: p.Token, Name: p.Name, Body: p.Body, Hair: p.Hair,
		Credits: p.Credits, LastSeen: p.UpdatedMs}
}

// characters is GET (list) and POST (create) on one path; POST keeps the
// CSRF header rule every mutating route has.
func (h *Handler) characters(w http.ResponseWriter, r *http.Request) {
	switch r.Method {
	case http.MethodGet:
		h.withAccount(h.listCharacters)(w, r)
	case http.MethodPost:
		h.mutating(h.withAccount(h.createCharacter))(w, r)
	default:
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
	}
}

func (h *Handler) listCharacters(w http.ResponseWriter, r *http.Request, a accountCtx) {
	players, err := h.Store.AccountPlayers(r.Context(), a.id) // oldest first
	if err != nil {
		http.Error(w, "character lookup failed", http.StatusInternalServerError)
		return
	}
	out := make([]characterView, 0, len(players))
	for _, p := range players {
		out = append(out, viewCharacter(p))
	}
	writeJSON(w, out)
}

func (h *Handler) createCharacter(w http.ResponseWriter, r *http.Request, a accountCtx) {
	var req struct {
		Name string `json:"name"`
		Body string `json:"body"`
		Hair string `json:"hair"` // optional: "" is hair.none
	}
	if !readJSON(w, r, &req) {
		return
	}
	// SanitizeName turns an empty name into "Player 0"; that is not a pick.
	name := server.SanitizeName(req.Name, 0)
	if !nameOK(name) || (name == server.SanitizeName("", 0) && strings.TrimSpace(req.Name) != name) {
		http.Error(w, "bad name", http.StatusBadRequest)
		return
	}
	if !bodies[req.Body] {
		http.Error(w, "bad body", http.StatusBadRequest)
		return
	}
	if req.Hair == "" {
		req.Hair = store.DefaultHair
	}
	if !hairs[req.Hair] {
		http.Error(w, "bad hair", http.StatusBadRequest)
		return
	}
	ctx := r.Context()
	taken, err := h.Store.CharacterNameTaken(ctx, name)
	if err != nil {
		http.Error(w, "name check failed", http.StatusInternalServerError)
		return
	}
	if taken {
		http.Error(w, "name taken", http.StatusConflict)
		return
	}
	// Count-then-insert can race to six under two simultaneous creates;
	// harmless, and the next create is refused again.
	n, err := h.Store.CountAccountPlayers(ctx, a.id)
	if err != nil {
		http.Error(w, "count failed", http.StatusInternalServerError)
		return
	}
	if n >= maxCharacters {
		http.Error(w, "character limit", http.StatusConflict)
		return
	}
	acc, err := h.Store.GetAccount(ctx, a.id)
	if err != nil || acc == nil {
		http.Error(w, "account lookup failed", http.StatusInternalServerError)
		return
	}
	p, err := h.mintCharacter(ctx, acc, name, req.Body, req.Hair)
	if errors.Is(err, store.ErrNameTaken) { // the index caught a racing create
		http.Error(w, "name taken", http.StatusConflict)
		return
	}
	if err != nil {
		http.Error(w, "character mint failed", http.StatusInternalServerError)
		return
	}
	writeJSON(w, viewCharacter(p))
}

// nameOK is the character-name rule after SanitizeName: 3–16 runes of
// letters, digits, space, hyphen and apostrophe; no edge or double spaces.
func nameOK(name string) bool {
	if n := utf8.RuneCountInString(name); n < 3 || n > 16 {
		return false
	}
	if strings.TrimSpace(name) != name || strings.Contains(name, "  ") {
		return false
	}
	for _, c := range name {
		if !unicode.IsLetter(c) && !unicode.IsDigit(c) && c != ' ' && c != '-' && c != '\'' {
			return false
		}
	}
	return true
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
	tokens, err := h.Store.DeleteAccount(r.Context(), a.id)
	if err != nil {
		http.Error(w, "delete failed", http.StatusInternalServerError)
		return
	}
	if h.Kick != nil && len(tokens) > 0 {
		h.Kick(tokens)
		// A save already in flight when the delete committed can still
		// land after it; Kick waited it out, so sweep once more (the
		// cascade is idempotent and the zombie keeps its account_id).
		if _, err := h.Store.DeleteAccount(r.Context(), a.id); err != nil {
			log.Printf("web: delete sweep: %v", err)
		}
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
