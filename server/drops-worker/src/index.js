// EQ Overwatch shared drops.
//
// Each install uploads its own drops file per server (gzip JSON: zones, mobs, items, kill and drop times; no character names). The app
// downloads everyone's files for a server and merges them itself with the counted-once rule, so a fight several people logged counts once.
// Nothing here stores IP addresses.
//
//   GET    /v1/drops/{server}          the files on a server: [{ id, updated, kills, drops, size }]
//   GET    /v1/drops/{server}/{id}     one file (gzip)
//   PUT    /v1/drops/{server}/{id}     upload or replace your file      (X-Install-Key, X-Kills, X-Drops)
//   DELETE /v1/drops/{server}/{id}     take your file down              (X-Install-Key)
//   DELETE /v1/admin/{server}/{id}     take a file down and block the install   (Authorization: Bearer ADMIN_KEY)
//   POST   /v1/admin/unblock/{id}      unblock an install                        (Authorization: Bearer ADMIN_KEY)

const MAX_BYTES = 5 * 1024 * 1024;
const MIN_SECONDS_BETWEEN_UPLOADS = 3600;
const MAX_NEW_INSTALLS_PER_DAY = 500;
const ID = /^[A-Za-z0-9_-]{16,40}$/;
const SERVER = /^[a-z0-9_-]{2,32}$/;
const KEY = /^[A-Za-z0-9_-]{32,64}$/;

export default {
  async fetch(request, env) {
    try {
      return await route(request, env);
    } catch (err) {
      return json({ error: "Something went wrong." }, 500);
    }
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

  if (parts[1] === "drops" && parts.length === 3 && method === "GET") {
    return list(env, parts[2].toLowerCase());
  }

  if (parts[1] === "drops" && parts.length === 4) {
    const server = parts[2].toLowerCase();
    const id = parts[3];
    if (!SERVER.test(server) || !ID.test(id)) {
      return json({ error: "Bad server or id." }, 400);
    }

    if (method === "GET") return download(env, server, id);
    if (method === "PUT") return upload(request, env, server, id);
    if (method === "DELETE") return remove(request, env, server, id);
  }

  if (parts[1] === "admin") {
    if (!(await isAdmin(request, env))) {
      return json({ error: "Not allowed." }, 403);
    }

    if (parts.length === 4 && method === "DELETE") {
      const server = parts[2].toLowerCase();
      const id = parts[3];
      await env.DB.batch([
        env.DB.prepare("DELETE FROM uploads WHERE server = ? AND id = ?").bind(server, id),
        env.DB.prepare("UPDATE installs SET blocked = 1 WHERE id = ?").bind(id),
      ]);
      await env.FILES.delete(fileKey(server, id));
      return json({ ok: true, removed: id, blocked: true });
    }

    if (parts.length === 4 && parts[2] === "unblock" && method === "POST") {
      await env.DB.prepare("UPDATE installs SET blocked = 0 WHERE id = ?").bind(parts[3]).run();
      return json({ ok: true, unblocked: parts[3] });
    }
  }

  return json({ error: "Not found." }, 404);
}

async function list(env, server) {
  if (!SERVER.test(server)) {
    return json({ error: "Bad server." }, 400);
  }

  const { results } = await env.DB.prepare(
    "SELECT u.id, u.updated, u.kills, u.drops, u.size FROM uploads u JOIN installs i ON i.id = u.id " +
    "WHERE u.server = ? AND i.blocked = 0 ORDER BY u.updated DESC LIMIT 2000").bind(server).all();
  return json({ server, files: results }, 200, { "Cache-Control": "public, max-age=300" });
}

async function download(env, server, id) {
  const row = await env.DB.prepare(
    "SELECT u.updated FROM uploads u JOIN installs i ON i.id = u.id WHERE u.server = ? AND u.id = ? AND i.blocked = 0").bind(server, id).first();
  if (!row) {
    return json({ error: "No such file." }, 404);
  }

  const body = await env.FILES.get(fileKey(server, id), { type: "stream" });
  if (!body) {
    return json({ error: "No such file." }, 404);
  }

  return new Response(body, {
    headers: { "Content-Type": "application/gzip", "Cache-Control": "public, max-age=300", "X-Updated": String(row.updated) },
  });
}

async function upload(request, env, server, id) {
  const key = request.headers.get("X-Install-Key") || "";
  if (!KEY.test(key)) {
    return json({ error: "Missing install key." }, 401);
  }

  const kills = count(request.headers.get("X-Kills"));
  const drops = count(request.headers.get("X-Drops"));
  if (kills < 0 || drops < 0) {
    return json({ error: "Bad counts." }, 400);
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
    const day = new Date().toISOString().slice(0, 10);
    const today = await env.DB.prepare("SELECT new_installs FROM daily WHERE day = ?").bind(day).first();
    if (today && today.new_installs >= MAX_NEW_INSTALLS_PER_DAY) {
      return json({ error: "Too many new installs today. Try again tomorrow." }, 429);
    }
  }

  const last = await env.DB.prepare("SELECT updated FROM uploads WHERE server = ? AND id = ?").bind(server, id).first();
  if (last && now - last.updated < MIN_SECONDS_BETWEEN_UPLOADS) {
    return json({ error: "Uploaded too recently.", retryAfter: MIN_SECONDS_BETWEEN_UPLOADS - (now - last.updated) }, 429);
  }

  const body = await request.arrayBuffer();
  const bytes = new Uint8Array(body);
  if (bytes.length < 20 || bytes.length > MAX_BYTES || bytes[0] !== 0x1f || bytes[1] !== 0x8b) {
    return json({ error: "That isn't a drops file." }, 400);
  }

  await env.FILES.put(fileKey(server, id), body);
  const statements = [];
  if (!install) {
    const day = new Date().toISOString().slice(0, 10);
    statements.push(env.DB.prepare("INSERT INTO installs (id, key_hash, created) VALUES (?, ?, ?)").bind(id, hash, now));
    statements.push(env.DB.prepare(
      "INSERT INTO daily (day, new_installs) VALUES (?, 1) ON CONFLICT(day) DO UPDATE SET new_installs = new_installs + 1").bind(day));
  }

  statements.push(env.DB.prepare(
    "INSERT INTO uploads (server, id, updated, kills, drops, size) VALUES (?, ?, ?, ?, ?, ?) " +
    "ON CONFLICT(server, id) DO UPDATE SET updated = excluded.updated, kills = excluded.kills, drops = excluded.drops, size = excluded.size")
    .bind(server, id, now, kills, drops, bytes.length));
  await env.DB.batch(statements);
  return json({ ok: true, updated: now });
}

async function remove(request, env, server, id) {
  const key = request.headers.get("X-Install-Key") || "";
  const install = await env.DB.prepare("SELECT key_hash FROM installs WHERE id = ?").bind(id).first();
  if (!install || !KEY.test(key) || !equal(install.key_hash, await sha256(key))) {
    return json({ error: "Not allowed." }, 403);
  }

  await env.DB.prepare("DELETE FROM uploads WHERE server = ? AND id = ?").bind(server, id).run();
  await env.FILES.delete(fileKey(server, id));
  return json({ ok: true });
}

async function isAdmin(request, env) {
  const header = request.headers.get("Authorization") || "";
  if (!env.ADMIN_KEY || !header.startsWith("Bearer ")) {
    return false;
  }

  return equal(await sha256(header.slice(7)), await sha256(env.ADMIN_KEY));
}

function fileKey(server, id) {
  return `drops/${server}/${id}`;
}

function count(text) {
  const n = Number(text ?? "0");
  return Number.isInteger(n) && n >= 0 && n <= 50_000_000 ? n : -1;
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
