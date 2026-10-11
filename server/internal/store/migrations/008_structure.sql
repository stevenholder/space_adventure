-- Phase 23: placed structures (docs/GDD.md "Placeable stations"). One row
-- per bench, forge or chest a character has set down. owner is the
-- character's token (never the name); owner_name is what the world labels
-- it with. stacks is a chest's contents as JSON ([] for a station), the
-- same shape-in-a-column approach as player.inventory. No foreign key:
-- DeletePlayer and DeleteAccount remove a character's rows themselves.
CREATE TABLE IF NOT EXISTS structure (
  id          TEXT    PRIMARY KEY,
  owner       TEXT    NOT NULL,
  owner_name  TEXT    NOT NULL,
  item        TEXT    NOT NULL,
  x           REAL,
  y           REAL,
  z           REAL,
  yaw         REAL,
  placed_ms   BIGINT,
  stacks      TEXT    NOT NULL DEFAULT '[]'
);

CREATE INDEX IF NOT EXISTS structure_owner ON structure (owner);
