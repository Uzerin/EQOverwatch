# EQ Overwatch shared drops and player count service

A Cloudflare Worker (Workers Paid plan, for the daily merge's CPU time).

- **Shared drops:** each install uploads, at most once a day, the kills and drops it logged since its last upload. At 10:00 UTC a merge
  counts every mob's kills and drops (a fight several players logged counts once; a drop counts only with a kill just before it) and
  writes one merged file per server, which the app downloads. A take-down or an admin block is subtracted at the next merge.
- **Shared leveling:** each install that opts in uploads, at most once a day per server, its leveling stretches: zone, level, solo
  or group, time, experience (bonuses and power leveling taken out by the app) and kills. The app downloads them summed per player
  (players numbered per answer, not named), zone, 5-level band and solo/group, and ranks zones itself.
- **Player count:** once a day the app sends a random install id and its version. Stored: the id, the version, the first and last day seen.
- Never stores character names or IP addresses.

## Deploy

```
npx.cmd wrangler d1 execute eqo-drops --remote --file=schema.sql
npx.cmd wrangler deploy
npx.cmd wrangler secret put ADMIN_KEY
```

## Admin

```
curl -H "Authorization: Bearer <ADMIN_KEY>" https://eqo-drops.eqoverwatch.workers.dev/v1/admin/stats
curl -X DELETE -H "Authorization: Bearer <ADMIN_KEY>" https://eqo-drops.eqoverwatch.workers.dev/v1/admin/<server>/<id>
```

Public counts: https://eqo-drops.eqoverwatch.workers.dev/v1/stats
