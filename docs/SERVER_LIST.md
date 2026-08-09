# Server list — discovery design

PunkMultiverse lets a session be hosted two ways, and they have genuinely different discovery
problems. This document covers both.

| | Steam relay | Dedicated UDP |
|---|---|---|
| Host is | a player running the game | a headless container, no Steam at all |
| Reachable via | Steam's relay network | a public `address:port` |
| Directory | **Valve's lobby list** | needs one — nothing exists yet |
| Status here | **built and shipping** | **specified below, not coded** |

The Steam half is done because it needs no infrastructure: Valve already runs the directory. The UDP
half needs something to run, and the rest of this document is the design for that something, written
down so it can be built later without re-deciding anything.

---

# Part 1 — Steam sessions (built)

## How it works

Steam's matchmaking has a lobby list. A host that opts in creates a **public** lobby instead of a
friends-only one and stamps it with metadata; the client calls `RequestLobbyList` with a filter and
reads that metadata back. Valve stores it, Valve serves it, and it costs nothing.

```
  PunkMultiverse host              Steam                    PUNK Nexus
  ───────────────────              ─────                    ──────────
  SetLobbyType(Public)      ──►  lobby directory  ◄──   RequestLobbyList
  SetLobbyData(listed=1)                                  + string filter listed=1
  SetLobbyData(name/mode/…)                               + slots available ≥ 1
                                                          GetLobbyData(…) per row
```

The client never contacts a game server to build this list. It talks to Steam and to nothing else.

## Why it cannot go stale

This is the property that makes the Steam path worth doing first, and it is not a design decision —
it is how Steam lobbies work:

- **A lobby is destroyed when its last member leaves.** A host that quits, crashes, or loses its
  connection stops being in its own lobby, and the lobby goes with it. There is no heartbeat to miss
  and no expiry to tune.
- **The list is read live on every refresh.** Nothing is cached between refreshes, so there is no
  cached copy to be wrong.
- **Fullness is Steam's to know.** The browse request carries
  `AddRequestLobbyListFilterSlotsAvailable(1)`, so a full session is filtered out server-side before
  the client ever sees it.

The one thing Steam does *not* track for us is how many of a lobby's members are actually seated
players, so the host publishes that itself — see `np` below.

## The metadata contract

These strings are the wire format between the mod and this client. They are defined twice, once on
each side, and **the two definitions must not drift**: an older mod build that still writes an old
key simply stops appearing in the browser, with nothing anywhere to warn you.

| Key | Written by | Meaning |
|---|---|---|
| `pmvver` | always | PunkMultiverse version. Joining across versions is refused by the handshake. |
| `gamebuild` | always | The base game's `Application.version`. |
| `host` | always | Host SteamID64. |
| `xport` | SteamServer sessions | `"SteamServer"` marks a discovery lobby. |
| `srvid` | SteamServer sessions | The anonymous game server's SteamID64 — what a joiner connects to. |
| `listed` | opt-in only | `"1"`. **The opt-in flag**; the browse filter keys on it. |
| `name` | opt-in only | Display name, ≤ 63 chars. |
| `mode` | opt-in only | `"Standard"` or `"Battle Royale"`. |
| `region` | opt-in only | Free-text label. Informational — nothing verifies it. |
| `maxp` | opt-in only | Player slots. |
| `np` | opt-in only | Occupied player slots, counted by the host. |
| `mods` | opt-in only | Comma-separated BepInEx plugin GUIDs, capped at 12. |
| `pw` | *nobody, yet* | Reserved. PunkMultiverse has no password feature, so every row reads as open. |

Definitions live in `src/PunkNexus/Services/SteamLobbyKeys.cs` here and in
`src/Transport/SteamLobbyController.cs` in PunkMultiverse.

`np` exists because `GetNumLobbyMembers` is only dependable for a lobby you have **joined**, and a
browser is by definition not a member of anything it is listing. Joining every row to count it would
be slow and rude, so the host counts its own slots and says so. The client prefers `np` and falls
back to Steam's count when it is absent.

## Opting in

Publishing is **off by default**, and that default is load-bearing. Hosting normally creates a
friends-only lobby reachable only by invite or by the pasted lobby code; a co-op session with three
friends must never turn up in a public browser because someone shipped a default the other way.

In `BepInEx/config/…/PunkMultiverse.cfg`:

```ini
[Session]
PublishServer = true          # off by default — this is the opt-in
ServerName    = Neon Wasteland    # blank = "<persona>'s Co-op"
ServerRegion  = EU                # blank = no region shown
```

The mod re-publishes on a 3-second tick, but only when something a browsing player would notice has
actually changed — the listing is fingerprinted and an unchanged fingerprint writes nothing. Steam
rate-limits `SetLobbyData`, and spending that budget on no-ops would eventually cost a real update.

Two lifecycle cases are handled explicitly, because both would otherwise leave a wrong row in the
browser:

- **Publishing turned off mid-session** clears `listed` *and* moves the lobby type back to
  friends-only. Clearing the key alone is not enough — a public lobby stays reachable by id.
- **Host migration** clears the previous host's listing on takeover. Lobby data outlives its author,
  so an inherited listing would otherwise advertise the old host's name and player count forever.
  The new host's own publish tick decides whether to relist.

## Load

A refresh is one `RequestLobbyList` plus one `GetLobbyData` per row. Both are Steam client calls
against Valve's infrastructure, which exists to serve exactly this. The number of PUNK sessions in
flight at any moment is small enough that this is not worth optimizing, and the browse is only
issued when the user opens or refreshes the tab — never on a timer.

## What Steam discovery does not give you

- **Steam must be running.** The client borrows `steam_api64.dll` from the verified game folder
  (Valve's binary is not ours to redistribute), and reports plainly when Steam is absent instead of
  failing. Someone who only came to install mods sees an explanation, not an error.
- **Headless servers are invisible to it.** The Docker server is deliberately Steam-free — see
  `server_image/README.md` in PunkMultiverse — so it has no lobby to publish. That is Part 2.
- **There is no in-game browser.** PunkMultiverse publishes; it does not yet browse. Joining a listed
  session today means going through PUNK Nexus or an invite. Adding `RequestLobbyList` to the mod's
  own PLAY ONLINE screen is a natural follow-on and needs no change to anything above.

---

# Part 2 — Dedicated UDP servers (specified, not built)

**Nothing in this part is implemented.** The client models these rows as
`ServerSource.Dedicated` and reads them from the published `manifest/servers.json`, which is
currently hand-edited and nearly empty. Everything below is the plan for filling it.

## Why the Steam trick does not transfer

A dedicated PunkMultiverse server runs in a container with no Steam client, no Steam login, and no
SDR. That is not an oversight — it is what makes a $5 VPS a viable host. But it means there is no
Valve-side lobby to create, so there is no free directory. Someone has to keep the list.

## The shape of the problem

The obvious design — the browser pings every server for live stats — hands every client a list of
game-server addresses and invites each of them to send traffic at those servers. That is a free
amplification target and a free reconnaissance list.

So the flow is split, and the two halves never talk to each other directly:

```
  game server ──push──►  relay  ◄──pull── PUNK Nexus
  (PunkMultiverse)     (public)          (many clients)
```

- **Push.** Each server heartbeats its own stats on a timer. The server chooses what it publishes;
  nothing queries it.
- **Pull.** The client reads one published document. It never learns of, connects to, or measures a
  game server as part of browsing.

A client cannot be pointed at a game server by anything it reads while browsing. It contacts a
server exactly once: when the user clicks join.

## Registration is separate from liveness

These are two different questions and conflating them is what makes server lists bad:

- **"Is this server allowed in the list?"** — answered once, by a pull request against
  `manifest/servers.json` in this repo. The entry carries identity only: `id`, `name`, `address`,
  `port`, `region`, owner contact. Same review path modders already use to get listed, same
  accountability, and it cannot be spammed by anyone who has not opened a PR.
- **"Is it up right now?"** — answered continuously, by heartbeat. Never by the repo.

The repo therefore holds a list of *known* servers that changes rarely; the relay holds *liveness*
that changes constantly. Neither goes stale, because neither is being asked a question it cannot
answer. A registered server that stops heartbeating shows as offline within a couple of minutes; it
does not need a pull request to be removed, and it comes back on its own.

The self-serve alternative — any server can register itself with a token — is less friction for
operators and strictly worse for everyone else: the list becomes spammable, names become
unaccountable, and there is no human in the loop when a server misbehaves. Given how small this
community is, PR registration is the right trade for now. Revisit if it ever becomes the bottleneck.

## The relay, on Cloudflare, at zero cost

A **Cloudflare Worker** with a **Workers KV** namespace behind it:

```
  POST /v1/heartbeat   ──►  Worker ──► KV: server:<id>  (short TTL)
  GET  /v1/servers.json ─►  Worker ──► KV read + edge cache (~30s)
  GET  /v1/server/<id>  ──►  Worker ──► KV read           (liveness probe)
```

Workers run at the edge with no server to keep alive, no OS to patch, and no bill at low volume.

**Budget.** Free-tier limits change, so treat these as *check before building, not as gospel* — at
the time of writing the free plan allows on the order of 100k Worker requests/day and 100k KV
reads/day, but only about **1,000 KV writes/day**. That write limit is the binding constraint and it
shapes the design:

- A server heartbeating every 60s is ~1,440 writes/day. **One server alone exceeds the free write
  budget.** So heartbeats do not each write.
- Instead the Worker **coalesces**: it accepts heartbeats freely (reads and in-memory work are
  cheap), and flushes the whole aggregate to a single KV key at most once every ~2 minutes. That is
  ~720 writes/day total, *regardless of how many servers there are* — the design scales in servers
  for free, which is exactly the axis that matters.
- The cost is bounded staleness: a server can be up to ~2 minutes out of date. For "is this server
  worth clicking" that is fine, and the join-time check below covers the rest.

If the write budget ever becomes the wrong shape, a SQLite-backed **Durable Object** holds the roster
in memory with no KV writes at all and removes the staleness window entirely. Confirm its current
free-tier terms before committing to it.

## Heartbeat (the push side)

```
POST /v1/heartbeat
Authorization: Bearer <per-server token>
Content-Type: application/json

{ "id", "players", "maxPlayers", "gameMode", "version", "mods" }
```

- Every **30–60s**. An entry not heard from in ~3 missed beats is marked offline, so a crashed
  server ages out on its own.
- `lastSeenUtc` is stamped by the **relay from its own clock** — never taken from the payload. A
  server cannot claim to be fresher than it is.
- The token binds a heartbeat to a registered `id`. Issued when the registration PR merges. Without
  it, anyone could publish a payload claiming any id, name, or player count.
- `name`, `address`, `port` and `region` come from the **registry**, not the payload. A server does
  not get to rename itself or advertise someone else's address between reviews.
- Everything else is clamped on ingest — name length, `players ≤ maxPlayers`, `mods` count — so one
  buggy or compromised server cannot push a document that breaks every client parsing it.

## Serving (the pull side)

One static JSON document, edge-cached with a ~30s TTL. Clients get a CDN hit and the Worker is not
in the hot path. Because it is static and public there is no client authentication, no per-client
state, and nothing to attack that is not already a cached file.

Shape — the one the client already parses:

```json
{
  "schemaVersion": 1,
  "updatedUtc": "2026-08-08T12:00:00Z",
  "servers": [
    {
      "id": "eu-west-1",
      "source": "Dedicated",
      "name": "Neon Wasteland",
      "address": "203.0.113.10",
      "port": 7777,
      "players": 6,
      "maxPlayers": 16,
      "gameMode": "Battle Royale",
      "region": "EU-West",
      "version": "0.1.245",
      "gameVersion": "0.12.4",
      "passworded": false,
      "mods": ["PunkMultiverse", "PunkScoreboard"],
      "lastSeenUtc": "2026-08-08T11:59:30Z"
    }
  ]
}
```

Every field except `name` is optional to the client — a missing `gameMode` just means that server
never matches a game-mode filter. `mods` carries registry mod **ids**, which is what makes "servers
running the mods I have" filterable client-side with no extra lookup.

## The join-time check

Before connecting, the client asks the relay about that one server:

```
GET /v1/server/<id>   →   { "online": true, "players": 6, "lastSeenUtc": "…" }
```

This is the answer to "let me check it is still up before I try". It is a **read from the relay**,
not a probe of the game server — so it stays inside the no-client-to-server rule, it is a single
cheap request, and a dead server is reported as dead rather than as a connection that hangs.

If the relay itself is unreachable, the client says so and still offers the join. A directory being
down is not a reason to refuse to connect to a server the user already chose.

## Where the pieces would live

The Worker and its KV binding belong in this repo (it owns the registry and the client). The
heartbeat sender belongs in PunkMultiverse's dedicated-server path, next to the code that already
knows the player count. PunkMultiverse's `infra/` directory holds AWS-flavored deployment scripts
today; a Cloudflare Worker does not fit there and should not be forced into it.

## Open questions

- Whether the Worker should also serve `manifest/mods.json`. Probably not — the mod registry is
  static, GitHub Raw serves it fine, and combining them couples a rarely-changing document to a
  constantly-changing one.
- Whether registered-but-offline servers should be shown greyed out or hidden. Greyed out is more
  informative and makes "is it back yet" answerable without a refresh loop; hidden is less
  cluttered. Leaning greyed out with a filter.
- Whether the mod should verify the relay's identity beyond TLS. Almost certainly not worth it: the
  worst a spoofed relay achieves is showing a player a server list that is wrong, and the handshake
  still gates the actual join.
