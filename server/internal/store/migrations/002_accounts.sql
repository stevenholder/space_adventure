-- Phase 7: accounts that issue game identity (docs/ROADMAP.md Phase 7).
-- No SQL foreign keys on purpose: the delete cascade is one code path in
-- DeleteAccount, transactional, identical on both engines — FK behaviour
-- is the kind of dialect divergence the portable-SQL rules exist to avoid.

CREATE TABLE IF NOT EXISTS account (
  id          TEXT    PRIMARY KEY,
  email       TEXT    NOT NULL UNIQUE,
  pw_hash     TEXT    NOT NULL,
  created_ms  BIGINT  NOT NULL
);

CREATE TABLE IF NOT EXISTS web_session (
  id          TEXT    PRIMARY KEY,
  account_id  TEXT    NOT NULL,
  created_ms  BIGINT  NOT NULL,
  expires_ms  BIGINT  NOT NULL
);

CREATE TABLE IF NOT EXISTS link_code (
  code        TEXT    PRIMARY KEY,
  account_id  TEXT    NOT NULL,
  created_ms  BIGINT  NOT NULL,
  expires_ms  BIGINT  NOT NULL
);

-- Nullable: a NULL account_id is a guest, exactly today's players.
ALTER TABLE player ADD COLUMN account_id TEXT;
