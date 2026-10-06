-- Phase 16: accounts only, characters (docs/ROADMAP.md Phase 16).
-- Link codes are gone; the launcher signs in instead.
DROP TABLE IF EXISTS link_code;

-- The character's model + gender ("char.player", "char.ubc.f", ...).
-- Every existing player was the default body.
ALTER TABLE player ADD COLUMN body TEXT NOT NULL DEFAULT 'char.player';

-- Character names are unique case-insensitively among account-owned rows.
-- Before Phase 16 nothing enforced that, so two accounts may already hold
-- "kade" and "Kade": the older row keeps its name, every later one gets its
-- token as a suffix, rather than the index failing and the server never
-- starting. The WHOLE token: it is the primary key, so the new names cannot
-- clash with each other (a four-character prefix did, on kind: every t28
-- "legacy" token starts with the same letters). Ugly, unique, and a
-- one-row UPDATE for an admin to tidy. Guests (account_id NULL) are
-- outside the index and keep any name.
UPDATE player SET name = name || ' ' || token
WHERE account_id IS NOT NULL
  AND EXISTS (SELECT 1 FROM player p2
              WHERE p2.account_id IS NOT NULL
                AND lower(p2.name) = lower(player.name)
                AND (p2.created_ms < player.created_ms
                     OR (p2.created_ms = player.created_ms AND p2.token < player.token)));

-- Partial expression index: Postgres, and SQLite >= 3.9.
CREATE UNIQUE INDEX player_character_name ON player (lower(name)) WHERE account_id IS NOT NULL;
