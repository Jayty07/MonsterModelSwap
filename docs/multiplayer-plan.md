# Plan: letting other MonsterModelSwap users see each other's model

Status: proposal, no code yet.

## 1. Feasibility in one paragraph

Yes, it is possible, but **not through the game**. The swap writes `ModelCharaId` / scale into
the local client's memory only; the FFXIV server never learns about it and never forwards it, so
other clients keep drawing your normal character. (This is the same reason Anamnesis / Glamourer
changes are invisible to others.) To make it visible, every participating client needs a
*second*, plugin-to-plugin channel that carries "player X is currently model 123 at height 1.4",
and the receiving plugin then applies that state to X's actor locally using exactly the code we
already have for the local player. Mare Synchronos proves the approach works at scale for
Penumbra/Glamourer data; our payload is tiny by comparison (a few integers instead of gigabytes
of textures), which makes this considerably simpler than Mare.

Trying to smuggle the data through game systems (chat messages, emotes, party-finder text, etc.)
is *not* recommended: it is visible to non-users, is detectable server-side, and is the one
approach that would actually increase ToS exposure. The design below never sends anything to
Square Enix servers.

## 2. Non-invasive design principles

| Principle | How it's honoured |
|---|---|
| Nothing new goes to the game server | Sync uses an independent HTTPS/WebSocket connection to a small relay. Zero extra game packets. |
| Off by default | A "Sync" tab with an explicit *Enable sync* toggle; default off. Plugin behaves exactly as today when off. |
| Consent on both sides | You only ever see swaps from people you have **paired** with (mutual code exchange, same as Mare pairs / syncshells). Strangers with the plugin are unaffected in both directions. |
| Only touches actors of paired players | The receiving side applies state solely to `IPlayerCharacter` objects that match a paired identity; never to NPCs, minions, or unpaired players. |
| Minimal data | Payload is `modelId`, `height`, optional `headHeight`, optional `loopTimelineId`, and a version. No appearance data, no chat, no location. Camera settings stay local. |
| Identity is hashed | Clients identify each other by `SHA256(name + "@" + worldId + salt)`; the relay never stores plain character names. Pairing is done by exchanging a short code/UID out of band (Discord etc.). |
| Clean teardown | Revert/disable/unload/disconnect immediately restores every remote actor we changed (we keep the same "original state" bookkeeping as for the local player). |
| No persistence of others' data | Remote state lives in memory only and is dropped when the peer leaves range, disconnects, or the plugin unloads. |

## 3. Architecture options

### A. Small relay server (recommended)

```
client A ──WS──▶ relay ◀──WS── client B
   push own state         subscribe to paired UIDs
```

* Tiny ASP.NET Core (SignalR) or Node service; stateless except an in-memory
  `uid → last state` map and a `pair` table (SQLite). Could run on a $5 VPS or a free tier.
* Client pushes its state on change (throttled, ~1/s max) and on connect; relay fans it out to
  the connected peers that are paired with the sender. Peers that come online later get the last
  known state on subscribe.
* Pros: simple, NAT-agnostic, works cross-datacenter, easy to rate-limit/abuse-control.
* Cons: someone has to host it; single point of failure (but failure only means "no sync",
  the plugin still works locally).

### B. Ride on the Mare Synchronos ecosystem

Mare (and its forks, e.g. Lightless/Snowcloak/Player Sync) already have the pairing UI, the server
infrastructure, and a huge user base. Two sub-options:

* **B1 – Mare IPC.** Mare exposes IPC for *its* data types (Penumbra mods, Glamourer, Customize+,
  Honorific, Moodles, PetNames). It does not have a slot for arbitrary third-party blobs, so we'd
  need a PR accepted upstream (or into a fork) adding a "MonsterModelSwap" data slot. Realistic
  only if a fork maintainer is interested; this needs checking with them before relying on it.
* **B2 – Piggyback on an existing slot** (e.g. encode our ints into Honorific/Customize+ payloads).
  Works technically but is a hack, breaks when those plugins validate their data, and is arguably
  invasive to *their* users. Not recommended.

Pros: no hosting, users already paired. Cons: external dependency we don't control, licensing
(Mare is AGPL) and maintainer buy-in needed, and it forces users to install Mare.

### C. Peer-to-peer (no server)

Direct connections need NAT traversal (STUN/TURN, i.e. a server anyway) and peer discovery
(again a server, or exchanging IPs by hand). Not worth it for this payload size. Ruled out.

### Recommendation

Start with **A**, designed so the transport is an interface (`ISyncTransport`); a Mare-IPC
transport (B1) can be added later if a fork picks it up, without changing the actor-application
code.

## 4. What is exchanged

```jsonc
{
  "v": 1,
  "uid": "…hash…",          // sender identity hash
  "active": true,           // false => peer reverted; receivers restore original
  "modelId": 1234,          // ModelChara row id
  "height": 1.4,            // model scale multiplier
  "headHeight": 2.1,        // optional, 0 = untouched
  "loop": 3456              // optional ActionTimeline BaseOverride (looping idle), 0 = none
}
```

Deliberately **not** exchanged: camera settings, one-shot animation plays (timing would be
off by network latency; can be added later as a fire-and-forget event if desired), favorites,
keybinds.

## 5. Identifying the other player locally

The relay tells us "UID `abc` is model 1234". To find their actor we need to map UID → object:

1. On sync enable, compute our own UID from `LocalPlayer.Name + HomeWorld.RowId`.
2. For each paired UID we store the peer's plain name+world **locally only**, obtained during
   pairing (the peer sends it directly in the pair handshake, encrypted by TLS; the relay only
   sees hashes). Alternative that avoids even that: iterate visible `IPlayerCharacter`s, hash
   each `name@world`, and compare with paired UIDs — O(visible players) per tick, trivially cheap
   and zero plaintext leaves the client. **Prefer the hash-scan approach.**
3. Resolve every framework tick via `IObjectTable` (indices 0–199 for real players; 200+ for
   cutscene copies, matched by the same name+world rule used in `IsLocalPlayerCopy`).
4. Entity IDs are session-scoped and change on zoning, so they're only used as a fast-path
   cache, never as identity.

## 6. Applying remote state

Refactor `ModelSwapService` so the per-actor logic (`Apply`, `EnforceScale`, redraw cooldown,
original-state capture, revert) operates on an `ActorTarget` rather than assuming the local
player. The local player becomes one target; each paired peer in range becomes another with its
own desired state. The persistence loop (territory change, condition change, per-tick
re-resolve) then works for remote actors for free — including their cutscene copies in
shared-party cutscenes.

Per remote actor we store: original `ModelCharaId`, original `ModelScale`, original
`GameObject.Scale`, `Height`; all restored on `active:false`, unpair, out-of-range, disconnect,
disable, or unload.

Edge cases:
* Peer zones away / out of range → actor disappears from object table; we drop the mapping
  (nothing to restore, the game destroyed the object). When they reappear the state is re-applied.
* Peer's plugin crashes without sending `active:false` → relay marks them offline on socket
  drop and broadcasts a synthetic `active:false`.
* Both players in a duty cutscene → their copy at index ≥200 is matched by name+world and gets the
  same enforcement as our own copy already does.
* Peer is in gpose/cutscene we are not in → invisible to us anyway; nothing to do.

## 7. Server sketch

* ASP.NET Core minimal API + SignalR hub, ~300 lines.
* Endpoints: `Register` (returns UID + secret), `Pair(code)`, `Unpair(uid)`, `PushState(state)`,
  `Subscribe()` (streams states of paired online UIDs).
* Auth: the secret from `Register` (stored in plugin config) — no account, no email, no
  Discord OAuth needed. Optional later.
* Storage: SQLite (`uid, secret_hash, created`), (`uid_a, uid_b`). States live in memory only.
* Abuse control: per-connection rate limit (1 push/s), max pairs per UID, payload validation
  (`modelId` must exist in `ModelChara`, `height` clamped 0.05–20).
* Hosting: any VPS / Fly.io / Railway free tier; publish as a separate repo + Dockerfile so users
  can self-host and point the plugin at their own URL (config field `SyncServerUrl`).

## 8. Plugin UI additions ("Sync" tab)

* Enable sync (default off) · server URL · connection status.
* Your pair code (copy button) · *Add pair* (paste code) · list of pairs with
  online/in-range/model shown, per-pair *Pause* and *Remove*.
* "Show others' swaps" and "Share my swap" as independent toggles (receive-only or send-only
  are both valid).
* Everything greyed out with an explanation while disconnected.

## 9. Risks / open questions

* **Hosting cost and uptime** – someone must run the relay (you, or users self-host).
* **Mare fork buy-in** for option B1 is unknown; treat as a later add-on.
* **Redraw churn**: applying a swap to another player forces a redraw of their actor; the
  existing 20-frame cooldown keeps this bounded, but a peer spamming model changes would make
  their actor flicker for viewers → server-side throttle handles this.
* **Interaction with Penumbra/Glamourer redraws** on the same actor: both systems redraw; our
  persistence loop already tolerates being overwritten and re-applies, but the first in-game test
  should include a Penumbra user.
* **ToS**: unchanged from today — still client-side memory edits plus an ordinary HTTPS connection
  from a plugin (same category as Mare, XIVAuth, etc.). The plan adds no game-server traffic.

## 10. Effort

| Phase | Work | Estimate |
|---|---|---|
| 1 | Refactor `ModelSwapService` to multi-target; apply a hard-coded model to a named nearby player via `/mms debugremote <name>` to prove remote-actor application works | 1 session |
| 2 | Relay server repo (SignalR, SQLite, Docker) + `ISyncTransport` client, hashed identity, pairing | 1 session |
| 3 | Sync tab UI, config, lifecycle/cleanup, throttling, docs, release | 1 session |
| 4 | Two-client in-game test (needs two accounts/PCs on your side), fixes | your testing + ~½ session |

Phase 1 is useful on its own even without a server (e.g. two friends typing each other's model
in manually) and is where any surprises with remote actors would surface, so it's the right
place to start.
