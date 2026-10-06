-- Phase 17: the character's hair (docs/GDD.md "Faces and hair (Phase 17)").
-- A worn piece id from art/manifest.json's `hair.*` rows; every existing
-- character is bald until it picks one.
ALTER TABLE player ADD COLUMN hair TEXT NOT NULL DEFAULT 'hair.none';
