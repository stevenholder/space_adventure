// Phase 7 — accounts and sessions (docs/ROADMAP.md Phase 7); Phase 16
// removed link codes and gave accounts characters.
// Same portable-SQL rules as player.go: $N placeholders, no dialect
// branches, absence is (nil, nil) not an error.

package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
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
		if isUnique(err) {
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
// and every player the account owns (C57). It returns the tokens of
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

// ---- account ↔ player ------------------------------------------------------

// AccountPlayers lists the account's characters, oldest first.
func (s *Store) AccountPlayers(ctx context.Context, accountID string) ([]Player, error) {
	rows, err := s.DB.QueryContext(ctx,
		playerSelect+` WHERE account_id = $1 ORDER BY created_ms, token`, accountID)
	if err != nil {
		return nil, fmt.Errorf("store: listing players: %w", err)
	}
	defer rows.Close()
	var out []Player
	for rows.Next() {
		p, err := scanPlayer(rows)
		if err != nil {
			return nil, err
		}
		out = append(out, *p)
	}
	return out, rows.Err()
}

// CountAccountPlayers is the five-per-account check's count.
func (s *Store) CountAccountPlayers(ctx context.Context, accountID string) (int, error) {
	var n int
	if err := s.DB.QueryRowContext(ctx,
		`SELECT COUNT(*) FROM player WHERE account_id = $1`, accountID).Scan(&n); err != nil {
		return 0, fmt.Errorf("store: counting account players: %w", err)
	}
	return n, nil
}

// CharacterNameTaken asks the player_character_name index's question ahead
// of the insert, for a clean 409: is name used by any account-owned row,
// case-insensitively? Guests do not count. The index stays the real guard.
func (s *Store) CharacterNameTaken(ctx context.Context, name string) (bool, error) {
	var n int
	if err := s.DB.QueryRowContext(ctx,
		`SELECT COUNT(*) FROM player WHERE account_id IS NOT NULL AND lower(name) = lower($1)`,
		name).Scan(&n); err != nil {
		return false, fmt.Errorf("store: checking character name: %w", err)
	}
	return n > 0, nil
}

// SetPlayerAccount stamps ownership on a freshly minted account player.
// ErrNameTaken when another account-owned row already has the name.
func (s *Store) SetPlayerAccount(ctx context.Context, token, accountID string) error {
	if _, err := s.DB.ExecContext(ctx,
		`UPDATE player SET account_id = $1 WHERE token = $2`, accountID, token); err != nil {
		if isUnique(err) {
			return ErrNameTaken
		}
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
