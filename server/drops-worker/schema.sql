-- EQ Overwatch shared drops and the anonymous player count. No IP addresses and no character names are stored.

-- Installs that share drops: a random id the app made, and the hash of its secret key.
CREATE TABLE IF NOT EXISTS installs (
  id TEXT PRIMARY KEY,
  key_hash TEXT NOT NULL,
  created INTEGER NOT NULL,
  blocked INTEGER NOT NULL DEFAULT 0
);

-- The last upload of each install on each server (for the once-a-day limit and the sharer count).
CREATE TABLE IF NOT EXISTS uploads (
  server TEXT NOT NULL,
  id TEXT NOT NULL,
  updated INTEGER NOT NULL,
  kills INTEGER NOT NULL,
  drops INTEGER NOT NULL,
  size INTEGER NOT NULL,
  PRIMARY KEY (server, id)
);

CREATE TABLE IF NOT EXISTS daily (
  day TEXT PRIMARY KEY,
  new_installs INTEGER NOT NULL
);

-- Every kill (kind 0) and drop (kind 1) each install shared, so a take-down or a block can be subtracted at the next merge.
CREATE TABLE IF NOT EXISTS events (
  server TEXT NOT NULL,
  contributor TEXT NOT NULL,
  kind INTEGER NOT NULL,
  zone TEXT NOT NULL COLLATE NOCASE,
  mob TEXT NOT NULL COLLATE NOCASE,
  item TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
  t INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS events_by_key ON events (server, kind, zone, mob, item, t);
CREATE INDEX IF NOT EXISTS events_by_contributor ON events (server, contributor, t);

-- Mob/item counts that changed since the last merge.
CREATE TABLE IF NOT EXISTS dirty (
  server TEXT NOT NULL,
  kind INTEGER NOT NULL,
  zone TEXT NOT NULL COLLATE NOCASE,
  mob TEXT NOT NULL COLLATE NOCASE,
  item TEXT NOT NULL COLLATE NOCASE,
  PRIMARY KEY (server, kind, zone, mob, item)
);

-- The merged counts (a fight several players logged counts once) and when each was last seen.
CREATE TABLE IF NOT EXISTS totals (
  server TEXT NOT NULL,
  kind INTEGER NOT NULL,
  zone TEXT NOT NULL COLLATE NOCASE,
  mob TEXT NOT NULL COLLATE NOCASE,
  item TEXT NOT NULL COLLATE NOCASE,
  count INTEGER NOT NULL,
  last_seen INTEGER NOT NULL,
  PRIMARY KEY (server, kind, zone, mob, item)
);

-- The merged file of each server (the file itself is in KV at merged/{server}).
CREATE TABLE IF NOT EXISTS merged (
  server TEXT PRIMARY KEY,
  updated INTEGER NOT NULL DEFAULT 0,
  players INTEGER NOT NULL DEFAULT 0,
  kills INTEGER NOT NULL DEFAULT 0,
  drops INTEGER NOT NULL DEFAULT 0,
  size INTEGER NOT NULL DEFAULT 0,
  pending INTEGER NOT NULL DEFAULT 0
);

-- The anonymous player count: a random id per install, its app version, and the first and last day (UTC) it was seen.
CREATE TABLE IF NOT EXISTS pings (
  id TEXT PRIMARY KEY,
  version TEXT NOT NULL,
  first_day TEXT NOT NULL,
  last_day TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS pings_by_last_day ON pings (last_day);

-- Small values: last_merge is when the daily merge last finished (unix seconds).
CREATE TABLE IF NOT EXISTS meta (
  key TEXT PRIMARY KEY,
  value INTEGER NOT NULL
);
