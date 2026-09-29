-- Phase 11: per-player skill state — XP per skill plus the permanently
-- discovered POI set (Recon awards first-discovery once, ever). Same
-- shape-in-a-column approach as inventory/missions: the server is the only
-- reader.
ALTER TABLE player ADD COLUMN skills TEXT NOT NULL DEFAULT '{}';
