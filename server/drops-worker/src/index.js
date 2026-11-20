// EQ Overwatch shared drops and the anonymous player count.
//
// Shared drops: each install uploads, at most once a day, the kills and drops it logged since its last upload (gzip JSON: zones,
// mobs, items, kill and drop times; no character names). Uploads go into the events table. Once a day at 10:00 UTC a merge counts
// every mob's kills and drops with the counted-once rule (a fight several players logged counts once), and writes one merged file per
// server that the app downloads. A take-down or an admin block removes that install's events, and the next merge subtracts them.
// Nothing here stores IP addresses or character names.
//
//   GET    /v1/merged/{server}[?have=T]  the merged file (gzip); 304 when it is not newer than T. Headers X-Updated, X-Players,
//                                        X-Last-Merge (when the daily merge last finished)
//   PUT    /v1/drops/{server}/{id}       upload new kills and drops   (X-Install-Key, X-Kills, X-Drops)  once a day
//   DELETE /v1/drops/{server}/{id}       take your drops down          (X-Install-Key)
//   PUT    /v1/leveling/{server}/{id}    upload leveling stretches (gzip JSON, X-Install-Key)  once a day
//   GET    /v1/leveling/{server}[?skip=id] everyone's leveling per player, zone, 5-level band, solo/group (players numbered, not named)
//   POST   /v1/ping                      { id, version }: counts this install as active today
//   GET    /v1/stats                     public player counts
//   GET    /v1/admin/stats               the same with the versions in use        (Authorization: Bearer ADMIN_KEY)
//   DELETE /v1/admin/{server}/{id}       take an install's drops down and block it (Authorization: Bearer ADMIN_KEY)
//   POST   /v1/admin/unblock/{id}        unblock an install                        (Authorization: Bearer ADMIN_KEY)

const MAX_BYTES = 5 * 1024 * 1024;
const MAX_UNPACKED = 64 * 1024 * 1024;
const MAX_EVENTS = 3_000_000;
const MIN_SECONDS_BETWEEN_UPLOADS = 20 * 3600; // once a day, with room for the app's start time to move
const MAX_NEW_INSTALLS_PER_DAY = 500;
const MAX_NEW_PINGS_PER_DAY = 20_000;
const KILL_BACKING_SECONDS = 120; // a drop counts only when the same upload has a kill of that mob shortly before it
const BUNCH_SECONDS = 5; // lines of the same kill or drop closer than this are one event, counted once across players
const ID = /^[A-Za-z0-9_-]{16,40}$/;
const SERVER = /^[a-z0-9_-]{2,32}$/;
const KEY = /^[A-Za-z0-9_-]{32,64}$/;
const VERSION = /^[0-9A-Za-z.+-]{1,24}$/;
const FORMAT = "EQOverwatchDrops";
const LEVELING_FORMAT = "EQOverwatchLeveling";
const MAX_STRETCHES = 20_000;
const LEVELING_KEEP_SECONDS = 730 * 86400;
const BAND = 5;
const MERGED_FORMAT = "EQOverwatchDropsMerged";

export default {
  async fetch(request, env) {
    try {
      return await route(request, env);
    } catch (err) {
      return json({ error: "Something went wrong." }, 500);
    }
  },

  // 10:00 UTC, then every 10 minutes until the merge is through (each run does a part of it).
  async scheduled(event, env, ctx) {
    ctx.waitUntil(merge(env, Date.now() + 25_000));
  },
};

async function route(request, env) {
  const url = new URL(request.url);
  const parts = url.pathname.split("/").filter(Boolean).map(decodeURIComponent);
  const method = request.method;

  if (parts.length === 1 && parts[0] === "health") {
    return json({ ok: true });
  }

  if (parts[0] !== "v1") {
    return json({ error: "Not found." }, 404);
  }

  if (parts[1] === "merged" && parts.length === 3 && method === "GET") {
    return downloadMerged(env, parts[2].toLowerCase(), Number(url.searchParams.get("have") || "0"));
  }

  if (parts[1] === "drops" && (parts.length === 3 || (parts.length === 4 && method === "GET"))) {
    return json({ error: "Shared drops are now merged once a day. Please update EQ Overwatch." }, 410);
  }

  if (parts[1] === "drops" && parts.length === 4) {
    const server = parts[2].toLowerCase();
    const id = parts[3];
    if (!SERVER.test(server) || !ID.test(id)) {
      return json({ error: "Bad server or id." }, 400);
    }

    if (method === "PUT") return upload(request, env, server, id);
    if (method === "DELETE") return remove(request, env, server, id);
  }

  if (parts[1] === "leveling" && parts.length >= 3) {
    const server = parts[2].toLowerCase();
    if (!SERVER.test(server)) {
      return json({ error: "Bad server." }, 400);
    }

    if (parts.length === 3 && method === "GET") return levelingDownload(env, server, url.searchParams.get("skip") || "");
    if (parts.length === 4 && method === "PUT" && ID.test(parts[3])) return levelingUpload(request, env, server, parts[3]);
  }

  if (parts[1] === "ping" && parts.length === 2 && method === "POST") {
    return ping(request, env);
  }

  if (parts[1] === "stats" && parts.length === 2 && method === "GET") {
    return json(await stats(env, false), 200, { "Cache-Control": "public, max-age=300" });
  }

  if (parts[1] === "admin") {
    if (!(await isAdmin(request, env))) {
      return json({ error: "Not allowed." }, 403);
    }

    if (parts.length === 3 && parts[2] === "stats" && method === "GET") {
      return json(await stats(env, true));
    }

    if (parts.length === 4 && parts[2] !== "unblock" && method === "DELETE") {
      const server = parts[2].toLowerCase();
      const id = parts[3];
      await takeDown(env, server, id);
      await env.DB.prepare("UPDATE installs SET blocked = 1 WHERE id = ?").bind(id).run();
      return json({ ok: true, removed: id, blocked: true });
    }

    if (parts.length === 4 && parts[2] === "unblock" && method === "POST") {
      await env.DB.prepare("UPDATE installs SET blocked = 0 WHERE id = ?").bind(parts[3]).run();
      return json({ ok: true, unblocked: parts[3] });
    }
  }

  return json({ error: "Not found." }, 404);
}

// ───────────────────────── merged file ─────────────────────────

async function downloadMerged(env, server, have) {
  if (!SERVER.test(server)) {
    return json({ error: "Bad server." }, 400);
  }

  const [row, meta] = await Promise.all([
    env.DB.prepare("SELECT updated, players FROM merged WHERE server = ? AND updated > 0").bind(server).first(),
    env.DB.prepare("SELECT value FROM meta WHERE key = 'last_merge'").first(),
  ]);
  const lastMerge = String(meta ? meta.value : 0);
  if (!row) {
    return json({ error: "No merged drops for that server yet." }, 404, { "X-Last-Merge": lastMerge });
  }

  const headers = {
    "X-Updated": String(row.updated), "X-Players": String(row.players), "X-Last-Merge": lastMerge, "Cache-Control": "public, max-age=300",
  };
  if (Number.isFinite(have) && have >= row.updated) {
    return new Response(null, { status: 304, headers });
  }

  const body = await env.FILES.get(mergedKey(server), { type: "stream" });
  if (!body) {
    return json({ error: "No merged drops for that server yet." }, 404);
  }

  return new Response(body, { headers: { "Content-Type": "application/gzip", ...headers } });
}

// ───────────────────────── uploads ─────────────────────────

async function upload(request, env, server, id) {
  const key = request.headers.get("X-Install-Key") || "";
  if (!KEY.test(key)) {
    return json({ error: "Missing install key." }, 401);
  }

  const length = Number(request.headers.get("Content-Length") || "0");
  if (length > MAX_BYTES) {
    return json({ error: "That file is too large." }, 413);
  }

  const now = Math.floor(Date.now() / 1000);
  const hash = await sha256(key);
  const install = await env.DB.prepare("SELECT key_hash, blocked FROM installs WHERE id = ?").bind(id).first();
  if (install) {
    if (!equal(install.key_hash, hash)) return json({ error: "That id belongs to another install." }, 403);
    if (install.blocked) return json({ error: "This install's uploads are turned off." }, 403);
  } else {
    const today = await env.DB.prepare("SELECT new_installs FROM daily WHERE day = ?").bind(day(now)).first();
    if (today && today.new_installs >= MAX_NEW_INSTALLS_PER_DAY) {
      return json({ error: "Too many new installs today. Try again tomorrow." }, 429);
    }
  }

  const last = await env.DB.prepare("SELECT updated FROM uploads WHERE server = ? AND id = ?").bind(server, id).first();
  if (last && now - last.updated < MIN_SECONDS_BETWEEN_UPLOADS) {
    return json({ error: "Uploaded too recently.", retryAfter: MIN_SECONDS_BETWEEN_UPLOADS - (now - last.updated) }, 429);
  }

  const bytes = new Uint8Array(await request.arrayBuffer());
  if (bytes.length < 20 || bytes.length > MAX_BYTES || bytes[0] !== 0x1f || bytes[1] !== 0x8b) {
    return json({ error: "That isn't a drops file." }, 400);
  }

  const file = await unpack(bytes);
  if (!file) {
    return json({ error: "That isn't a drops file." }, 400);
  }

  if (!install) {
    await env.DB.batch([
      env.DB.prepare("INSERT INTO installs (id, key_hash, created) VALUES (?, ?, ?)").bind(id, hash, now),
      env.DB.prepare("INSERT INTO daily (day, new_installs) VALUES (?, 1) ON CONFLICT(day) DO UPDATE SET new_installs = new_installs + 1").bind(day(now)),
    ]);
  }

  const added = await ingest(env, server, id, file);
  await env.DB.prepare(
    "INSERT INTO uploads (server, id, updated, kills, drops, size) VALUES (?, ?, ?, ?, ?, ?) " +
    "ON CONFLICT(server, id) DO UPDATE SET updated = excluded.updated, kills = uploads.kills + excluded.kills, " +
    "drops = uploads.drops + excluded.drops, size = excluded.size")
    .bind(server, id, now, added.kills, added.drops, bytes.length).run();
  return json({ ok: true, updated: now, kills: added.kills, drops: added.drops });
}

/** The drops file in a gzip upload, or null when it is not one. */
async function unpack(bytes) {
  try {
    const stream = new Blob([bytes]).stream().pipeThrough(new DecompressionStream("gzip"));
    const reader = stream.getReader();
    const chunks = [];
    let total = 0;
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      total += value.length;
      if (total > MAX_UNPACKED) return null;
      chunks.push(value);
    }

    const text = new TextDecoder().decode(concat(chunks, total));
    const file = JSON.parse(text);
    if (!file || file.Format !== FORMAT || !Array.isArray(file.Kills) || !Array.isArray(file.Drops)
        || file.Kills.length + file.Drops.length > MAX_EVENTS) {
      return null;
    }

    return file;
  } catch {
    return null;
  }
}

/**
 * Adds an install's kills and drops. A drop is kept only when the same upload has a kill of that mob up to two minutes before it.
 * Events the install already shared (the same mob, item and second) are not added twice, so an overlapping upload is harmless.
 */
async function ingest(env, server, contributor, file) {
  const name = (list, index) => (Number.isInteger(index) && index >= 0 && index < list.length && typeof list[index] === "string"
    ? list[index].trim().slice(0, 120) : null);
  const zones = Array.isArray(file.Zones) ? file.Zones : [];
  const mobs = Array.isArray(file.Mobs) ? file.Mobs : [];
  const items = Array.isArray(file.Items) ? file.Items : [];

  const events = [];
  const killTimes = new Map();
  for (const k of file.Kills) {
    if (!Array.isArray(k) || k.length < 3 || !Number.isInteger(k[0])) continue;
    const zone = name(zones, k[1]);
    const mob = name(mobs, k[2]);
    if (zone === null || !mob) continue;
    events.push({ kind: 0, zone, mob, item: "", t: k[0] });
    const key = `${zone.toLowerCase()}|${mob.toLowerCase()}`;
    if (!killTimes.has(key)) killTimes.set(key, []);
    killTimes.get(key).push(k[0]);
  }

  for (const times of killTimes.values()) times.sort((a, b) => a - b);

  for (const d of file.Drops) {
    if (!Array.isArray(d) || d.length < 4 || !Number.isInteger(d[0])) continue;
    const zone = name(zones, d[1]);
    const mob = name(mobs, d[2]);
    const item = name(items, d[3]);
    if (zone === null || !mob || !item) continue;
    if (!backed(killTimes.get(`${zone.toLowerCase()}|${mob.toLowerCase()}`), d[0])) continue;
    events.push({ kind: 1, zone, mob, item, t: d[0] });
  }

  if (events.length === 0) {
    return { kills: 0, drops: 0 };
  }

  // What this install already shared in the same time span.
  let min = Infinity;
  let max = -Infinity;
  for (const e of events) {
    if (e.t < min) min = e.t;
    if (e.t > max) max = e.t;
  }

  const { results } = await env.DB.prepare(
    "SELECT kind, zone, mob, item, t FROM events WHERE server = ? AND contributor = ? AND t BETWEEN ? AND ?")
    .bind(server, contributor, min, max).all();
  const have = new Map();
  for (const r of results) {
    const k = eventKey(r);
    have.set(k, (have.get(k) || 0) + 1);
  }

  const fresh = [];
  for (const e of events) {
    const k = eventKey(e);
    const n = have.get(k) || 0;
    if (n > 0) {
      have.set(k, n - 1);
    } else {
      fresh.push(e);
    }
  }

  // D1 allows 100 bound values a statement: 14 rows of 7.
  const statements = [];
  for (let i = 0; i < fresh.length; i += 14) {
    const rows = fresh.slice(i, i + 14);
    const values = [];
    for (const e of rows) values.push(server, contributor, e.kind, e.zone, e.mob, e.item, e.t);
    statements.push(env.DB.prepare(
      "INSERT INTO events (server, contributor, kind, zone, mob, item, t) VALUES " + rows.map(() => "(?, ?, ?, ?, ?, ?, ?)").join(", "))
      .bind(...values));
  }

  const keys = new Map();
  for (const e of fresh) keys.set(groupKey(e), e);
  const dirty = [...keys.values()];
  for (let i = 0; i < dirty.length; i += 20) {
    const rows = dirty.slice(i, i + 20);
    const values = [];
    for (const e of rows) values.push(server, e.kind, e.zone, e.mob, e.item);
    statements.push(env.DB.prepare(
      "INSERT OR IGNORE INTO dirty (server, kind, zone, mob, item) VALUES " + rows.map(() => "(?, ?, ?, ?, ?)").join(", "))
      .bind(...values));
  }

  if (fresh.length > 0) {
    statements.push(env.DB.prepare(
      "INSERT INTO merged (server, pending) VALUES (?, 1) ON CONFLICT(server) DO UPDATE SET pending = 1").bind(server));
  }

  for (let i = 0; i < statements.length; i += 50) {
    await env.DB.batch(statements.slice(i, i + 50));
  }

  return { kills: fresh.filter((e) => e.kind === 0).length, drops: fresh.filter((e) => e.kind === 1).length };
}

function backed(times, t) {
  if (!times) return false;
  // the last kill no later than two seconds after the drop
  let lo = 0;
  let hi = times.length - 1;
  let best = -1;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (times[mid] <= t + 2) {
      best = mid;
      lo = mid + 1;
    } else {
      hi = mid - 1;
    }
  }

  return best >= 0 && times[best] >= t - KILL_BACKING_SECONDS;
}

async function remove(request, env, server, id) {
  const key = request.headers.get("X-Install-Key") || "";
  const install = await env.DB.prepare("SELECT key_hash FROM installs WHERE id = ?").bind(id).first();
  if (!install || !KEY.test(key) || !equal(install.key_hash, await sha256(key))) {
    return json({ error: "Not allowed." }, 403);
  }

  await takeDown(env, server, id);
  return json({ ok: true });
}

/** Removes an install's events on a server; the counts they were in are merged again at the next merge. */
async function takeDown(env, server, id) {
  await env.DB.batch([
    env.DB.prepare(
      "INSERT OR IGNORE INTO dirty (server, kind, zone, mob, item) SELECT DISTINCT server, kind, zone, mob, item FROM events " +
      "WHERE server = ? AND contributor = ?").bind(server, id),
    env.DB.prepare("DELETE FROM events WHERE server = ? AND contributor = ?").bind(server, id),
    env.DB.prepare("DELETE FROM uploads WHERE server = ? AND id = ?").bind(server, id),
    env.DB.prepare("DELETE FROM leveling WHERE server = ? AND contributor = ?").bind(server, id),
    env.DB.prepare("DELETE FROM leveling_uploads WHERE server = ? AND id = ?").bind(server, id),
    env.DB.prepare("INSERT INTO merged (server, pending) VALUES (?, 1) ON CONFLICT(server) DO UPDATE SET pending = 1").bind(server),
  ]);
  await env.FILES.delete(`drops/${server}/${id}`); // a file from before the daily merge
}

// ───────────────────────── shared leveling ─────────────────────────

/** Adds an install's leveling stretches (once a day per server). The same stretch sent again is not added twice. */
async function levelingUpload(request, env, server, id) {
  const key = request.headers.get("X-Install-Key") || "";
  if (!KEY.test(key)) {
    return json({ error: "Missing install key." }, 401);
  }

  const now = Math.floor(Date.now() / 1000);
  const hash = await sha256(key);
  const install = await env.DB.prepare("SELECT key_hash, blocked FROM installs WHERE id = ?").bind(id).first();
  if (install) {
    if (!equal(install.key_hash, hash)) return json({ error: "That id belongs to another install." }, 403);
    if (install.blocked) return json({ error: "This install's uploads are turned off." }, 403);
  } else {
    const today = await env.DB.prepare("SELECT new_installs FROM daily WHERE day = ?").bind(day(now)).first();
    if (today && today.new_installs >= MAX_NEW_INSTALLS_PER_DAY) {
      return json({ error: "Too many new installs today. Try again tomorrow." }, 429);
    }
  }

  const last = await env.DB.prepare("SELECT updated FROM leveling_uploads WHERE server = ? AND id = ?").bind(server, id).first();
  if (last && now - last.updated < MIN_SECONDS_BETWEEN_UPLOADS) {
    return json({ error: "Uploaded too recently.", retryAfter: MIN_SECONDS_BETWEEN_UPLOADS - (now - last.updated) }, 429);
  }

  const bytes = new Uint8Array(await request.arrayBuffer());
  if (bytes.length < 20 || bytes.length > MAX_BYTES || bytes[0] !== 0x1f || bytes[1] !== 0x8b) {
    return json({ error: "That isn't a leveling file." }, 400);
  }

  let file;
  try {
    const stream = new Blob([bytes]).stream().pipeThrough(new DecompressionStream("gzip"));
    file = JSON.parse(await new Response(stream).text());
  } catch {
    file = null;
  }

  if (!file || file.format !== LEVELING_FORMAT || !Array.isArray(file.stretches) || file.stretches.length > MAX_STRETCHES) {
    return json({ error: "That isn't a leveling file." }, 400);
  }

  const oldest = now - LEVELING_KEEP_SECONDS;
  const rows = file.stretches.filter((s) =>
    s && typeof s.z === "string" && s.z.length > 0 && s.z.length <= 64
    && Number.isInteger(s.l) && s.l >= 1 && s.l <= 255 && (s.g === 0 || s.g === 1)
    && typeof s.s === "number" && s.s > 0 && s.s <= 86400 && typeof s.x === "number" && s.x >= 0 && s.x <= 100000
    && Number.isInteger(s.k) && s.k >= 0 && s.k <= 100000 && Number.isInteger(s.t) && s.t >= oldest && s.t <= now + 86400);

  if (!install) {
    await env.DB.batch([
      env.DB.prepare("INSERT INTO installs (id, key_hash, created) VALUES (?, ?, ?)").bind(id, hash, now),
      env.DB.prepare("INSERT INTO daily (day, new_installs) VALUES (?, 1) ON CONFLICT(day) DO UPDATE SET new_installs = new_installs + 1").bind(day(now)),
    ]);
  }

  const insert = env.DB.prepare(
    "INSERT OR IGNORE INTO leveling (server, contributor, zone, level, grp, seconds, xp, kills, t) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)");
  for (let i = 0; i < rows.length; i += 50) {
    await env.DB.batch(rows.slice(i, i + 50).map((s) => insert.bind(server, id, s.z, s.l, s.g, s.s, s.x, s.k, s.t)));
  }

  await env.DB.batch([
    env.DB.prepare("INSERT INTO leveling_uploads (server, id, updated) VALUES (?, ?, ?) ON CONFLICT(server, id) DO UPDATE SET updated = excluded.updated")
      .bind(server, id, now),
    env.DB.prepare("DELETE FROM leveling WHERE server = ? AND t < ?").bind(server, oldest),
  ]);
  return json({ ok: true, added: rows.length });
}

/**
 * Everyone's leveling on a server, summed per player, zone, 5-level band and solo/group. Players are numbered for this answer only
 * (never named, and not the install id); the asking install's own rows are left out when it sends its id as skip.
 */
async function levelingDownload(env, server, skip) {
  const band = `((level - 1) / ${BAND}) * ${BAND} + 1`;
  const result = await env.DB.prepare(
    `SELECT contributor, zone, ${band} AS b, grp, SUM(seconds) AS s, SUM(xp) AS x, SUM(kills) AS k FROM leveling ` +
    "WHERE server = ? AND contributor <> ? AND contributor NOT IN (SELECT id FROM installs WHERE blocked = 1) " +
    "GROUP BY contributor, zone, b, grp").bind(server, ID.test(skip) ? skip : "").all();
  const numbers = new Map();
  const rows = (result.results || []).map((r) => {
    if (!numbers.has(r.contributor)) numbers.set(r.contributor, numbers.size + 1);
    return { z: r.zone, b: r.b, g: r.grp, p: numbers.get(r.contributor), s: r.s, x: r.x, k: r.k };
  });
  const zones = [...new Set(rows.map((r) => r.z))];
  return json({ rows, zones, players: numbers.size }, 200, { "Cache-Control": "public, max-age=3600" });
}

// ───────────────────────── the daily merge ─────────────────────────

/** One run of the merge: folds in old-style files, recounts what changed, and writes the merged files that are through. */
async function merge(env, deadline) {
  const timeLeft = () => Date.now() < deadline;

  // Files uploaded before the daily merge existed are read in once, then removed.
  const old = await env.FILES.list({ prefix: "drops/", limit: 50 });
  for (const k of old.keys) {
    if (!timeLeft()) return;
    const [, server, id] = k.name.split("/");
    const install = await env.DB.prepare("SELECT blocked FROM installs WHERE id = ?").bind(id).first();
    if (install && !install.blocked && SERVER.test(server || "")) {
      const bytes = await env.FILES.get(k.name, { type: "arrayBuffer" });
      const file = bytes ? await unpack(new Uint8Array(bytes)) : null;
      if (file) await ingest(env, server, id, file);
    }

    await env.FILES.delete(k.name);
  }

  // Recount each mob and item that changed.
  while (timeLeft()) {
    const { results: keys } = await env.DB.prepare("SELECT server, kind, zone, mob, item FROM dirty LIMIT 50").all();
    if (keys.length === 0) break;
    for (const g of keys) {
      if (!timeLeft()) return;
      const { results: rows } = await env.DB.prepare(
        "SELECT contributor, t FROM events WHERE server = ? AND kind = ? AND zone = ? AND mob = ? AND item = ? ORDER BY t")
        .bind(g.server, g.kind, g.zone, g.mob, g.item).all();
      const { count, lastSeen } = countOnce(rows);
      await env.DB.batch([
        count > 0
          ? env.DB.prepare(
            "INSERT INTO totals (server, kind, zone, mob, item, count, last_seen) VALUES (?, ?, ?, ?, ?, ?, ?) " +
            "ON CONFLICT(server, kind, zone, mob, item) DO UPDATE SET count = excluded.count, last_seen = excluded.last_seen")
            .bind(g.server, g.kind, g.zone, g.mob, g.item, count, lastSeen)
          : env.DB.prepare("DELETE FROM totals WHERE server = ? AND kind = ? AND zone = ? AND mob = ? AND item = ?")
            .bind(g.server, g.kind, g.zone, g.mob, g.item),
        env.DB.prepare("DELETE FROM dirty WHERE server = ? AND kind = ? AND zone = ? AND mob = ? AND item = ?")
          .bind(g.server, g.kind, g.zone, g.mob, g.item),
      ]);
    }
  }

  // Write the merged file of every server whose recount is through.
  const { results: servers } = await env.DB.prepare(
    "SELECT server FROM merged WHERE pending = 1 AND server NOT IN (SELECT DISTINCT server FROM dirty)").all();
  for (const s of servers) {
    if (!timeLeft()) return;
    await writeMerged(env, s.server);
  }

  // Through: the apps check again after the next one.
  await env.DB.prepare(
    "INSERT INTO meta (key, value) VALUES ('last_merge', ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value")
    .bind(Math.floor(Date.now() / 1000)).run();
}

/** Kills (or drops) of one mob (and item): lines closer than five seconds are one bunch, counted as the most any one player saw. */
function countOnce(rows) {
  let count = 0;
  let lastSeen = 0;
  let last = -Infinity;
  let bunch = new Map();
  const close = () => {
    if (bunch.size > 0) count += Math.max(...bunch.values());
    bunch = new Map();
  };

  for (const r of rows) {
    if (r.t - last > BUNCH_SECONDS) close();
    bunch.set(r.contributor, (bunch.get(r.contributor) || 0) + 1);
    last = r.t;
    if (r.t > lastSeen) lastSeen = r.t;
  }

  close();
  return { count, lastSeen };
}

async function writeMerged(env, server) {
  const { results } = await env.DB.prepare(
    "SELECT kind, zone, mob, item, count, last_seen FROM totals WHERE server = ? ORDER BY zone, mob, item").bind(server).all();
  const zones = new Names();
  const mobs = new Names();
  const items = new Names();
  const file = { Format: MERGED_FORMAT, Version: 1, Server: server, Updated: Math.floor(Date.now() / 1000), Players: 0, Zones: zones.list, Mobs: mobs.list, Items: items.list, Kills: [], Drops: [] };
  let kills = 0;
  let drops = 0;
  for (const r of results) {
    if (r.kind === 0) {
      file.Kills.push([zones.of(r.zone), mobs.of(r.mob), r.count, r.last_seen]);
      kills += r.count;
    } else {
      file.Drops.push([zones.of(r.zone), mobs.of(r.mob), items.of(r.item), r.count, r.last_seen]);
      drops += r.count;
    }
  }

  const players = await env.DB.prepare("SELECT COUNT(DISTINCT contributor) AS n FROM events WHERE server = ?").bind(server).first();
  file.Players = players ? players.n : 0;
  const gz = await gzip(JSON.stringify(file));
  await env.FILES.put(mergedKey(server), gz);
  await env.DB.prepare(
    "UPDATE merged SET updated = ?, players = ?, kills = ?, drops = ?, size = ?, pending = 0 WHERE server = ?")
    .bind(file.Updated, file.Players, kills, drops, gz.byteLength, server).run();
}

class Names {
  constructor() {
    this.list = [];
    this.index = new Map();
  }

  of(name) {
    const key = name.toLowerCase();
    if (!this.index.has(key)) {
      this.index.set(key, this.list.length);
      this.list.push(name);
    }

    return this.index.get(key);
  }
}

// ───────────────────────── player count ─────────────────────────

async function ping(request, env) {
  let body;
  try {
    body = await request.json();
  } catch {
    return json({ error: "Bad request." }, 400);
  }

  const id = typeof body?.id === "string" ? body.id : "";
  const version = typeof body?.version === "string" ? body.version : "";
  if (!ID.test(id) || !VERSION.test(version)) {
    return json({ error: "Bad request." }, 400);
  }

  const today = day(Math.floor(Date.now() / 1000));
  const known = await env.DB.prepare("SELECT 1 FROM pings WHERE id = ?").bind(id).first();
  if (!known) {
    const fresh = await env.DB.prepare("SELECT COUNT(*) AS n FROM pings WHERE first_day = ?").bind(today).first();
    if (fresh && fresh.n >= MAX_NEW_PINGS_PER_DAY) {
      return json({ error: "Try again tomorrow." }, 429);
    }
  }

  await env.DB.prepare(
    "INSERT INTO pings (id, version, first_day, last_day) VALUES (?, ?, ?, ?) " +
    "ON CONFLICT(id) DO UPDATE SET version = excluded.version, last_day = excluded.last_day").bind(id, version, today, today).run();
  return json({ ok: true });
}

async function stats(env, withVersions) {
  const now = Math.floor(Date.now() / 1000);
  const today = day(now);
  const week = day(now - (6 * 86400));
  const month = day(now - (29 * 86400));
  const q = (sql, ...args) => env.DB.prepare(sql).bind(...args).first();
  const [active, sharers] = await Promise.all([
    q("SELECT SUM(last_day = ?) AS today, SUM(last_day >= ?) AS week, SUM(last_day >= ?) AS month, " +
      "SUM(first_day = ?) AS new_today, SUM(first_day >= ?) AS new_week, COUNT(*) AS ever FROM pings", today, week, month, today, week),
    q("SELECT COUNT(DISTINCT id) AS n FROM uploads WHERE updated >= ?", now - (30 * 86400)),
  ]);
  const result = {
    activeToday: active?.today || 0,
    active7Days: active?.week || 0,
    active30Days: active?.month || 0,
    newToday: active?.new_today || 0,
    new7Days: active?.new_week || 0,
    everSeen: active?.ever || 0,
    sharingDrops: sharers?.n || 0,
  };

  if (withVersions) {
    const { results } = await env.DB.prepare(
      "SELECT version, COUNT(*) AS players FROM pings WHERE last_day >= ? GROUP BY version ORDER BY players DESC").bind(month).all();
    result.versions30Days = results;
  }

  return result;
}

// ───────────────────────── helpers ─────────────────────────

async function isAdmin(request, env) {
  const header = request.headers.get("Authorization") || "";
  if (!env.ADMIN_KEY || !header.startsWith("Bearer ")) {
    return false;
  }

  return equal(await sha256(header.slice(7)), await sha256(env.ADMIN_KEY));
}

function mergedKey(server) {
  return `merged/${server}`;
}

function eventKey(e) {
  return `${e.kind}|${e.zone.toLowerCase()}|${e.mob.toLowerCase()}|${(e.item || "").toLowerCase()}|${e.t}`;
}

function groupKey(e) {
  return `${e.kind}|${e.zone.toLowerCase()}|${e.mob.toLowerCase()}|${(e.item || "").toLowerCase()}`;
}

function day(unixSeconds) {
  return new Date(unixSeconds * 1000).toISOString().slice(0, 10);
}

function concat(chunks, total) {
  const out = new Uint8Array(total);
  let at = 0;
  for (const c of chunks) {
    out.set(c, at);
    at += c.length;
  }

  return out;
}

async function gzip(text) {
  const stream = new Blob([new TextEncoder().encode(text)]).stream().pipeThrough(new CompressionStream("gzip"));
  return await new Response(stream).arrayBuffer();
}

async function sha256(text) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

function equal(a, b) {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

function json(value, status = 200, headers = {}) {
  return new Response(JSON.stringify(value), { status, headers: { "Content-Type": "application/json", ...headers } });
}
