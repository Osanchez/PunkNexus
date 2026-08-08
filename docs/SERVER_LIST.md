# Server list — design

The Servers tab is built and filtering works; the feed behind it is empty because the relay that
fills it does not exist yet. This is the contract for that relay, written down so the pieces can be
built independently.

## Why a relay at all

The obvious design — the browser pings every server for live stats — hands out a list of game-server
addresses to every client and invites each of them to send traffic at those servers. That is a
free amplification target and a free reconnaissance list.

So the flow is split, and the two halves never talk to each other directly:

```
  game server ──push──►  relay  ◄──pull── PUNK Nexus
  (PunkMultiverse)     (public)          (many clients)
```

- **Push.** Each server heartbeats its own stats to the relay on a timer. The server chooses what it
  publishes; nothing queries it.
- **Pull.** The client only ever reads one published document. It never learns of, connects to, or
  measures a game server as part of browsing.

Client load lands on the relay, which is a static document behind a CDN and is cheap to make
resilient. A client cannot be pointed at a game server by anything it reads here.

## The published document

`servers.json`, the shape the client already parses:

```json
{
  "schemaVersion": 1,
  "updatedUtc": "2026-08-08T12:00:00Z",
  "servers": [
    {
      "id": "eu-west-1",
      "name": "Neon Wasteland",
      "address": "203.0.113.10",
      "port": 7777,
      "players": 6,
      "maxPlayers": 16,
      "gameMode": "Battle Royale",
      "region": "EU-West",
      "version": "0.1.244",
      "passworded": false,
      "mods": ["PunkMultiverse", "PunkScoreboard"],
      "lastSeenUtc": "2026-08-08T11:59:30Z"
    }
  ]
}
```

Every field except `name` is optional as far as the client is concerned — a missing `gameMode` just
means that server never matches a game-mode filter. `mods` carries manifest mod **ids**, which is
what makes "servers running the mods I have" filterable client-side with no extra lookup.

While the relay does not exist, the client reads
`manifest/servers.json` from this repo — the same shape, hand-edited. Pointing the client at a real
relay later is a URL change under **Settings → Catalog source**, not a code change.

## Heartbeat (the push side)

```
POST /v1/heartbeat
Authorization: Bearer <per-server token>
Content-Type: application/json

{ "id", "name", "address", "port", "players", "maxPlayers",
  "gameMode", "region", "version", "passworded", "mods" }
```

Expected behavior:

- Servers heartbeat every **30–60s**. The relay drops an entry it has not heard from in ~3 missed
  beats, so a crashed server ages out instead of lingering.
- `lastSeenUtc` is stamped by the **relay**, from its own clock — never taken from the payload.
- The token identifies the server and is what a name is bound to; without it anyone could publish an
  entry claiming any name or player count.
- `address` is what the relay observed the request come from, not what the payload claims, unless
  the server is explicitly allowed to declare one (needed for NAT and for a server behind a
  different public address than it dials out from).
- Payload fields are clamped on ingest — name length, `players` ≤ `maxPlayers`, `mods` count — so a
  compromised or buggy server cannot push a document that breaks every client parsing it.

## Serving (the pull side)

The relay republishes the aggregate as a single static document, cached at the edge with a short TTL
(~30s). Clients get a CDN hit; the relay is not in the hot path. Because the document is static and
public, no client authentication is involved and there is no per-client state to attack.

## What is not decided yet

Where the relay runs, and whether it is a small always-on service or a function that writes the
aggregate to object storage on each heartbeat. The latter is attractive: the "serving" half becomes
a static file with no compute, and the only running code is the ingest path.

PunkMultiverse already has an `infra/` directory with AWS setup scripts and Lambda sources, which is
the natural place for that ingest function to live.
