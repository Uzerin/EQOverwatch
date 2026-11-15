-- EQ Overwatch shared drops: an index of the uploaded drops files. The files themselves are in KV.
CREATE TABLE IF NOT EXISTS installs (
  id TEXT PRIMARY KEY,          -- random id the app made; never a character name
  key_hash TEXT NOT NULL,       -- SHA-256 of the install's secret key
  created INTEGER NOT NULL,
  blocked INTEGER NOT NULL DEFAULT 0
);
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
