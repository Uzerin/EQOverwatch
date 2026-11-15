# EQ Overwatch shared drops service

A Cloudflare Worker that keeps each install's shared drops file per server. The app does the merging.

- Stores: a random install id, the server name, upload time, kill and drop counts, and the gzip drops file (zones, mobs, items, times).
- Never stores character names or IP addresses.

## Deploy

```
npx.cmd wrangler d1 execute eqo-drops --remote --file=schema.sql
npx.cmd wrangler deploy
npx.cmd wrangler secret put ADMIN_KEY
```

## Take down a bad upload (and block that install)

```
curl -X DELETE -H "Authorization: Bearer <ADMIN_KEY>" https://eqo-drops.eqoverwatch.workers.dev/v1/admin/<server>/<id>
```
