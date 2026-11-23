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

-- Shared leveling: each install's stretches (zone, level, solo or group, time, experience with bonuses taken out, kills, start time).
-- No character names; the contributor is the install's random id.
CREATE TABLE IF NOT EXISTS leveling (
  server TEXT NOT NULL,
  contributor TEXT NOT NULL,
  zone TEXT NOT NULL COLLATE NOCASE,
  level INTEGER NOT NULL,
  grp INTEGER NOT NULL,
  seconds REAL NOT NULL,
  xp REAL NOT NULL,
  kills INTEGER NOT NULL,
  t INTEGER NOT NULL,
  PRIMARY KEY (server, contributor, t, zone, grp)
);
CREATE INDEX IF NOT EXISTS leveling_by_server ON leveling (server, t);

-- The last leveling upload of each install on each server (once a day).
CREATE TABLE IF NOT EXISTS leveling_uploads (
  server TEXT NOT NULL,
  id TEXT NOT NULL,
  updated INTEGER NOT NULL,
  PRIMARY KEY (server, id)
);

-- Server First: raid fights an install chose to share, WITH character names and guilds (the player is warned before turning it on).
-- received is the worker's own clock, so a kill only counts as a verified first when it was shared within two days of happening.
CREATE TABLE IF NOT EXISTS firsts (
  server TEXT NOT NULL,
  contributor TEXT NOT NULL,
  mob TEXT NOT NULL COLLATE NOCASE,
  t INTEGER NOT NULL,
  seconds REAL NOT NULL,
  killed INTEGER NOT NULL,
  players INTEGER NOT NULL,
  received INTEGER NOT NULL,
  data TEXT NOT NULL,
  PRIMARY KEY (server, contributor, mob, t)
);
CREATE INDEX IF NOT EXISTS firsts_by_server ON firsts (server, mob, t);

-- Leveling extras added later (coin looted and AA points per stretch), kept beside the leveling rows.
CREATE TABLE IF NOT EXISTS leveling_extra (
  server TEXT NOT NULL,
  contributor TEXT NOT NULL,
  zone TEXT NOT NULL COLLATE NOCASE,
  grp INTEGER NOT NULL,
  t INTEGER NOT NULL,
  copper INTEGER NOT NULL,
  aa INTEGER NOT NULL,
  PRIMARY KEY (server, contributor, t, zone, grp)
);

-- Shared mob stats (no names): kills (time, damage, attackers), /con level ranges per zone and resist counts per resist type.
CREATE TABLE IF NOT EXISTS mob_kills (
  server TEXT NOT NULL,
  contributor TEXT NOT NULL,
  mob TEXT NOT NULL COLLATE NOCASE,
  t INTEGER NOT NULL,
  seconds REAL NOT NULL,
  damage INTEGER NOT NULL,
  players INTEGER NOT NULL,
  PRIMARY KEY (server, contributor, mob, t)
);
CREATE INDEX IF NOT EXISTS mob_kills_by_mob ON mob_kills (mob, t);

CREATE TABLE IF NOT EXISTS mob_levels (
  contributor TEXT NOT NULL,
  mob TEXT NOT NULL COLLATE NOCASE,
  zone TEXT NOT NULL COLLATE NOCASE,
  lo INTEGER NOT NULL,
  hi INTEGER NOT NULL,
  PRIMARY KEY (contributor, mob, zone)
);

CREATE TABLE IF NOT EXISTS mob_resists (
  contributor TEXT NOT NULL,
  mob TEXT NOT NULL COLLATE NOCASE,
  type INTEGER NOT NULL,
  landed INTEGER NOT NULL,
  resisted INTEGER NOT NULL,
  PRIMARY KEY (contributor, mob, type)
);

CREATE TABLE IF NOT EXISTS mobs_uploads (
  server TEXT NOT NULL,
  id TEXT NOT NULL,
  updated INTEGER NOT NULL,
  PRIMARY KEY (server, id)
);

-- Resource Hunter zone bonuses (zone, bonus and Pacific day only; no names). One row per install, zone and day.
CREATE TABLE IF NOT EXISTS zone_bonuses (
  server TEXT NOT NULL,
  day TEXT NOT NULL,
  zone_key TEXT NOT NULL,
  zone TEXT NOT NULL,
  bonus TEXT NOT NULL,
  contributor TEXT NOT NULL,
  PRIMARY KEY (server, day, zone_key, contributor)
);
CREATE INDEX IF NOT EXISTS zone_bonuses_by_day ON zone_bonuses (server, day);

-- Resource Hunter bonus sizes: how much one install measured a zone's bonus to add on one Pacific day (percent; kind "xp", "aa" or
-- "coin"). Zone, day and size only; no names. Kept: a zone's sizes are its history.
CREATE TABLE IF NOT EXISTS bonus_sizes (
  server TEXT NOT NULL,
  day TEXT NOT NULL,
  zone_key TEXT NOT NULL,
  zone TEXT NOT NULL,
  kind TEXT NOT NULL,
  size REAL NOT NULL,
  contributor TEXT NOT NULL,
  PRIMARY KEY (server, day, zone_key, kind, contributor)
);
CREATE INDEX IF NOT EXISTS bonus_sizes_by_zone ON bonus_sizes (server, zone_key);
CREATE INDEX IF NOT EXISTS bonus_sizes_by_day ON bonus_sizes (server, day);
