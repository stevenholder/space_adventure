// Phase 7 — accounts, sessions and link codes (docs/ROADMAP.md Phase 7).
// Same portable-SQL rules as player.go: $N placeholders, no dialect
// branches, absence is (nil, nil) not an error.

package store

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
)

// Account is one row of `account`. PwHash is the argon2id encoded string —
// hashing lives in the web package; the store never sees a password.
type Account struct {
	ID        string
	Email     string
	PwHash    string
	CreatedMs int64
}

// ErrEmailTaken distinguishes the one constraint violation registration
// cares about from real failures.
var ErrEmailTaken = errors.New("store: email already registered")

func (s *Store) CreateAccount(ctx context.Context, a *Account) error {
	_, err := s.DB.ExecContext(ctx,
		`INSERT INTO account (id, email, pw_hash, created_ms) VALUES ($1, $2, $3, $4)`,
		a.ID, a.Email, a.PwHash, a.CreatedMs)
	if err != nil {
		// Both engines say "unique" somewhere in a unique-violation message;
		// matching the text beats importing two driver error types.
		if strings.Contains(strings.ToLower(err.Error()), "unique") {
			return ErrEmailTaken
		}
		return fmt.Errorf("store: creating account: %w", err)
	}
	return nil
}

func (s *Store) GetAccountByEmail(ctx context.Context, email string) (*Account, error) {
	return s.scanAccount(s.DB.QueryRowContext(ctx,
		`SELECT id, email, pw_hash, created_ms FROM account WHERE email = $1`, email))
}

func (s *Store) GetAccount(ctx context.Context, id string) (*Account, error) {
	return s.scanAccount(s.DB.QueryRowContext(ctx,
		`SELECT id, email, pw_hash, created_ms FROM account WHERE id = $1`, id))
}

func (s *Store) scanAccount(row *sql.Row) (*Account, error) {
	var a Account
	err := row.Scan(&a.ID, &a.Email, &a.PwHash, &a.CreatedMs)
	if errors.Is(err, sql.ErrNoRows) {
		return nil, nil
	}
	if err != nil {
		return nil, fmt.Errorf("store: selecting account: %w", err)
	}
	return &a, nil
}

// SetPassword replaces the hash and drops every session except keep (the
// one that made the change) — C57's "other sessions invalidated".
func (s *Store) SetPassword(ctx context.Context, accountID, pwHash, keepSession string) error {
	tx, err := s.DB.BeginTx(ctx, nil)
	if err != nil {
		return fmt.Errorf("store: begin: %w", err)
	}
	defer tx.Rollback()
	if _, err := tx.ExecContext(ctx,
		`UPDATE account SET pw_hash = $1 WHERE id = $2`, pwHash, accountID); err != nil {
		return fmt.Errorf("store: updating password: %w", err)
	}
	if _, err := tx.ExecContext(ctx,
		`DELETE FROM web_session WHERE account_id = $1 AND id <> $2`, accountID, keepSession); err != nil {
		return fmt.Errorf("store: dropping sessions: %w", err)
	}
	return tx.Commit()
}

// DeleteAccount is the whole cascade, one transaction: account, sessions,
// codes, and every player the account owns (C57). It returns the tokens of
// the deleted players so the caller can kick live connections.
func (s *Store) DeleteAccount(ctx context.Context, accountID string) ([]string, error) {
	tx, err := s.DB.BeginTx(ctx, nil)
	if err != nil {
		return nil, fmt.Errorf("store: begin: %w", err)
	}
	defer tx.Rollback()

	rows, err := tx.QueryContext(ctx,
		`SELECT token FROM player WHERE account_id = $1`, accountID)
	if err != nil {
		return nil, fmt.Errorf("store: listing owned players: %w", err)
	}
	var tokens []string
	for rows.Next() {
		var t string
		if err := rows.Scan(&t); err != nil {
			rows.Close()
			return nil, fmt.Errorf("store: scanning token: %w", err)
		}
		tokens = append(tokens, t)
	}
	rows.Close()

	for _, q := range []string{
		`DELETE FROM player WHERE account_id = $1`,
		`DELETE FROM web_session WHERE account_id = $1`,
		`DELETE FROM link_code WHERE account_id = $1`,
		`DELETE FROM account WHERE id = $1`,
	} {
		if _, err := tx.ExecContext(ctx, q, accountID); err != nil {
			return nil, fmt.Errorf("store: cascade %q: %w", q, err)
		}
	}
	return tokens, tx.Commit()
}

// ---- sessions --------------------------------------------------------------

func (s *Store) CreateSession(ctx context.Context, id, accountID string, createdMs, expiresMs int64) error {
	_, err := s.DB.ExecContext(ctx,
		`INSERT INTO web_session (id, account_id, created_ms, expires_ms) VALUES ($1, $2, $3, $4)`,
		id, accountID, createdMs, expiresMs)
	if err != nil {
		return fmt.Errorf("store: creating session: %w", err)
	}
	return nil
}

// GetSession returns the account id for a live session, "" when absent or
// expired (expired rows are deleted opportunistically).
func (s *Store) GetSession(ctx context.Context, id string, nowMs int64) (string, error) {
	var accountID string
	var expires int64
	err := s.DB.QueryRowContext(ctx,
		`SELECT account_id, expires_ms FROM web_session WHERE id = $1`, id).
		Scan(&accountID, &expires)
	if errors.Is(err, sql.ErrNoRows) {
		return "", nil
	}
	if err != nil {
		return "", fmt.Errorf("store: selecting session: %w", err)
	}
	if nowMs >= expires {
		_, _ = s.DB.ExecContext(ctx, `DELETE FROM web_session WHERE id = $1`, id)
		return "", nil
	}
	return accountID, nil
}

func (s *Store) DeleteSession(ctx context.Context, id string) error {
	if _, err := s.DB.ExecContext(ctx, `DELETE FROM web_session WHERE id = $1`, id); err != nil {
		return fmt.Errorf("store: deleting session: %w", err)
	}
	return nil
}

// ---- link codes ------------------------------------------------------------

// PutLinkCode mints a code, replacing any existing codes for the account —
// the newest code is the only live one, so a mis-typed code can simply be
// re-minted.
func (s *Store) PutLinkCode(ctx context.Context, code, accountID string, createdMs, expiresMs int64) error {
	tx, err := s.DB.BeginTx(ctx, nil)
	if err != nil {
		return fmt.Errorf("store: begin: %w", err)
	}
	defer tx.Rollback()
	if _, err := tx.ExecContext(ctx,
		`DELETE FROM link_code WHERE account_id = $1`, accountID); err != nil {
		return fmt.Errorf("store: clearing codes: %w", err)
	}
	if _, err := tx.ExecContext(ctx,
		`INSERT INTO link_code (code, account_id, created_ms, expires_ms) VALUES ($1, $2, $3, $4)`,
		code, accountID, createdMs, expiresMs); err != nil {
		return fmt.Errorf("store: inserting code: %w", err)
	}
	return tx.Commit()
}

// RedeemLinkCode consumes a live code (single-use: the row is deleted in
// the same transaction) and returns its account id, "" when the code is
// unknown or expired.
func (s *Store) RedeemLinkCode(ctx context.Context, code string, nowMs int64) (string, error) {
	tx, err := s.DB.BeginTx(ctx, nil)
	if err != nil {
		return "", fmt.Errorf("store: begin: %w", err)
	}
	defer tx.Rollback()

	var accountID string
	var expires int64
	err = tx.QueryRowContext(ctx,
		`SELECT account_id, expires_ms FROM link_code WHERE code = $1`, code).
		Scan(&accountID, &expires)
	if errors.Is(err, sql.ErrNoRows) {
		return "", nil
	}
	if err != nil {
		return "", fmt.Errorf("store: selecting code: %w", err)
	}
	if _, err := tx.ExecContext(ctx, `DELETE FROM link_code WHERE code = $1`, code); err != nil {
		return "", fmt.Errorf("store: consuming code: %w", err)
	}
	if err := tx.Commit(); err != nil {
		return "", err
	}
	if nowMs >= expires {
		return "", nil
	}
	return accountID, nil
}

// ---- account ↔ player ------------------------------------------------------

// AccountPlayerToken returns the token of the account's player, "" when it
// has none yet (one player per account per world).
func (s *Store) AccountPlayerToken(ctx context.Context, accountID string) (string, error) {
	var token string
	err := s.DB.QueryRowContext(ctx,
		`SELECT token FROM player WHERE account_id = $1`, accountID).Scan(&token)
	if errors.Is(err, sql.ErrNoRows) {
		return "", nil
	}
	if err != nil {
		return "", fmt.Errorf("store: selecting account player: %w", err)
	}
	return token, nil
}

// AdoptPlayer attaches an existing (guest) player row to an account — the
// legacy-import path (C59). Refused when the player is already owned or
// the account already has a player.
func (s *Store) AdoptPlayer(ctx context.Context, accountID, token string) error {
	existing, err := s.AccountPlayerToken(ctx, accountID)
	if err != nil {
		return err
	}
	if existing != "" {
		return errors.New("store: account already has a player")
	}
	res, err := s.DB.ExecContext(ctx,
		`UPDATE player SET account_id = $1 WHERE token = $2 AND account_id IS NULL`,
		accountID, token)
	if err != nil {
		return fmt.Errorf("store: adopting player: %w", err)
	}
	n, _ := res.RowsAffected()
	if n == 0 {
		return errors.New("store: no such unowned player")
	}
	return nil
}

// AccountPlayers lists the account's players for the profile page.
func (s *Store) AccountPlayers(ctx context.Context, accountID string) ([]Player, error) {
	rows, err := s.DB.QueryContext(ctx,
		`SELECT `+playerColumns+` FROM player WHERE account_id = $1`, accountID)
	if err != nil {
		return nil, fmt.Errorf("store: listing players: %w", err)
	}
	defer rows.Close()
	var out []Player
	for rows.Next() {
		p, err := scanPlayerRow(rows)
		if err != nil {
			return nil, err
		}
		out = append(out, *p)
	}
	return out, rows.Err()
}

// scanPlayerRow is GetPlayer's scan over a *sql.Rows, same JSON rules.
func scanPlayerRow(rows *sql.Rows) (*Player, error) {
	var p Player
	var inventory, equipped, missions string
	if err := rows.Scan(&p.Token, &p.Name, &p.Credits, &inventory, &equipped, &missions,
		&p.Pos[0], &p.Pos[1], &p.Pos[2], &p.CreatedMs, &p.UpdatedMs); err != nil {
		return nil, fmt.Errorf("store: scanning player: %w", err)
	}
	if err := json.Unmarshal([]byte(missions), &p.Missions); err != nil {
		return nil, fmt.Errorf("store: corrupt missions JSON: %w", err)
	}
	if p.Missions == nil {
		p.Missions = map[string]*MissionState{}
	}
	if err := json.Unmarshal([]byte(inventory), &p.Inventory); err != nil {
		return nil, fmt.Errorf("store: corrupt inventory JSON: %w", err)
	}
	if err := json.Unmarshal([]byte(equipped), &p.Equipped); err != nil {
		return nil, fmt.Errorf("store: corrupt equipped JSON: %w", err)
	}
	return &p, nil
}

// SetPlayerAccount stamps ownership on a freshly minted account player.
func (s *Store) SetPlayerAccount(ctx context.Context, token, accountID string) error {
	if _, err := s.DB.ExecContext(ctx,
		`UPDATE player SET account_id = $1 WHERE token = $2`, accountID, token); err != nil {
		return fmt.Errorf("store: setting player account: %w", err)
	}
	return nil
}

// CountPlayers is the landing page's public statistic.
func (s *Store) CountPlayers(ctx context.Context) (int64, error) {
	var n int64
	if err := s.DB.QueryRowContext(ctx, `SELECT COUNT(*) FROM player`).Scan(&n); err != nil {
		return 0, fmt.Errorf("store: counting players: %w", err)
	}
	return n, nil
}
