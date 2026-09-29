-- Phase 10: per-player mission state. One JSON object keyed by mission id
-- ({"mission.cull":{"active":true,"count":3,"done":2}}), beside inventory
-- and equipped, which take the same shape-in-a-column approach: the server
-- is the only reader, and a queryable mission table earns its keep only
-- when something queries it.
ALTER TABLE player ADD COLUMN missions TEXT NOT NULL DEFAULT '{}';
