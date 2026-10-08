-- Phase 21: the character's skin tone and undersuit colour (docs/GDD.md
-- "Skin and suit colours (Phase 21)"). Palette ids from art/manifest.json
-- `palettes`; the defaults are the bodies as baked, so every existing
-- character keeps its look.
ALTER TABLE player ADD COLUMN skin TEXT NOT NULL DEFAULT 'skin.01';
ALTER TABLE player ADD COLUMN suit TEXT NOT NULL DEFAULT 'suit.slate';
