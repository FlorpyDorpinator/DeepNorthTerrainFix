# Changelog

## 1.2.1

- Existing config files are migrated automatically. BepInEx keeps whatever value is already in the `.cfg`, so
  the 1.2.0 change of `PatchNetworkBudget` to off never reached anyone who installed 1.1.x. On the first start
  after this update the mod resets that one key to `false`, logs `Config migrated from a pre-1.2.0 file`, and
  records a config version so the reset happens only once. If you really want it on, set it back afterwards
  and it stays.
- No other changes; same protection and healing as 1.2.0.

## 1.2.0

**Critical fix for server-only installs. Update now.**

- 1.1.x could not stop an *unmodded* player from deleting a zone's terraforming. Vanilla still ran on their
  game: when a duplicate compiler showed up on their screen while they owned the real one, their game destroyed
  the real one, and the server accepted that. The server then kept whichever fresh compiler came next, so the
  zone snapped back to world generation and every player who dug there afterwards lost their strokes to the
  next arrival. Two changes close this for good:
  - **The server refuses a client's destroy of a zone's richest compiler** (`ProtectRichestCompiler`, on by
    default). The compiler is kept, its owner is cleared, and it is re-sent to every player, so the game that
    dropped it gets it straight back. Destroys of duplicates that have an equal or richer twin still go through.
  - **Duplicates are removed the instant they arrive and are never forwarded.** The game forwards new objects to
    other players before it processes its own destroy list, so a duplicate handled one frame later had already
    reached everyone. The arrival heal now runs synchronously inside the receive path on the server, and a
    duplicate queued for removal is excluded from every outgoing sync.
- `PatchNetworkBudget` now defaults to **off**. Raising the server's send rate pushes more data at every player,
  and a player on a weak upload then sees delayed hits and rubber-banding. Turn it on only if everyone has the
  bandwidth. Existing config files keep their value; set it to `false` yourself if you updated from 1.1.x.
- Log lines: `[protect] refused destroy of terrain compiler ...` shows the protection firing (first 5 per
  compiler, then every 50th).

## 1.1.3

- Documentation only: the listing now says up front that installing on the server alone is enough and
  players need nothing, and explains how to use the commands without console access (admin + F5).
- No code changes; same behaviour as 1.1.2.

## 1.1.2

- Server-side ghost purge (`PurgeGhostTerrainOps`, on by default): any ZDO whose prefab is a self-destructing
  terrain op is removed on world load, the moment it arrives over the network, and during the periodic sweep.
  The server has the prefab table, so this works with no client mod installed. These ZDOs are always garbage:
  the stroke was already applied, and every client that spawns one re-applies it and is left with a dead
  instance that blocks "area ready" until the owner leaves the zone.
- A single modded client with `ClientMayHeal` on also purges them in its area (it claims ownership first).

## 1.1.1

- New `dntf_zdos <zoneX> <zoneY> [radius]`: lists every object type in a zone as the running game sees it,
  with prefab names, persistent/non-persistent counts, owners, and (on a client) how many are stuck as
  "created but dead". Non-persistent objects never reach the save, so this is the only way to see them.
- New `dntf_purge <zoneX> <zoneY> <prefab> [radius]`: removes non-persistent objects of one prefab from a
  zone. Never touches anything persistent (buildings, chests, terrain data).
- New safety net `CleanupNetworkedTerrainOps`: a terrain-op object that carries a network view is destroyed
  through the scene when it self-destructs, so it cannot leave a dead ZDO that blocks "area ready" for every
  arriving player until its owner leaves the zone.

## 1.1.0

First public release, by FlorpyDorp.

- Heals duplicate `_TerrainCompiler` objects in the loaded world: on world load, when a new compiler arrives
  over the network, and in a sliced background sweep every 2 minutes (server / local host).
- Merges the removed duplicate's terrain vertices into the survivor before removal.
- Console commands: `dntf_scan`, `dntf_zone`, `dntf_fix`, `dntf_seams`, `dntf_reset`. Write commands run on
  the server; joined clients are forwarded and must be on the adminlist.
- Client-side prevention: reuse the zone's existing compiler instead of spawning a duplicate; keep the
  compiler with data on collision and remove the other once; stop the create/destroy loop.
- Hard 25 s teleport timeout so a never-ready area cannot trap a player in a portal or on login.
- Stops a hoe/shovel stroke that crosses a zone line from being applied twice on the shared column.
- Only copies edge paint into neighbouring compilers this client owns.
- Optional network changes: Steam send-rate cap 150 KB/s to 500 KB/s, ZDO in-flight budget 10 KB to 30 KB,
  and throttled per-piece snow-buildup sync (snow still accumulates locally between syncs).
- Every change is switchable in the config.

## 1.0.0

Internal build: client-side prevention only.
