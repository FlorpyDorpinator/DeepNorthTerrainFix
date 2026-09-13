# Deep North: duplicate `_TerrainCompiler` ZDOs in one zone cause permanent terraforming loss and portal/login hangs

**Build:** client `[fill from Player.log header, e.g. 1.0.12 / network version N]`, server `[fill from server log]`.
**Setup:** dedicated server on `[OS]`, `[Steam-only / -crossplay (PlayFab)]`, `[N]` players. **No mods** on server or clients when this happened `[confirm]`.
**World:** `[name]`, created on `[1.0 / migrated from PTB]`, `optterrain` never run `[confirm]`. Deep North base spanning zones 9/132 and 9/133 (world ~557, 8470).
**Impact:** all terraforming in a 64 m zone deleted; every player portalling or logging into that zone is soft-locked on the loading screen while the base owner is inside. Has happened `[once / N times]` since `[date]`.

## Summary

1. **A second `_TerrainCompiler` ZDO gets created for a zone that already has one.** `Heightmap.GetAndCreateTerrainCompiler()` only checks `TerrainComp.s_instances` (compilers instantiated in this process). If the zone's compiler ZDO exists but is not instantiated here yet, it instantiates `m_terrainCompilerPrefab` and `ZNetView.Awake` mints a brand-new persistent ZDO. Nothing consults `ZDOMan` for an existing compiler in the sector.
2. **On collision, `TerrainComp.Awake` destroys the compiler that was already registered, regardless of which one holds data.** `ZNetScene.Destroy` only calls `ZDOMan.DestroyZDO` when the ZDO is locally owned. Owned: the zone's terrain block is deleted world-wide (our "terrain reverted"). Not owned: the ZDO is left with `Created = false`, `CreateObjectsSorted` re-instantiates it on a later 33 ms tick, its `Awake` destroys the other one, and this repeats until ownership changes.
3. **While that repeats, `ZNetScene.IsAreaReady` is never true for the zone** (one compiler always lacks an instance), and both `Player.UpdateTeleport` and `Game.UpdateRespawn` wait on `IsAreaReady` with no timeout that escapes it. Arriving players hang until the in-zone player leaves, the server's `ReleaseNearbyZDOS` hands ownership to them, and the next destroy actually lands.

## What we observed

- Player A was building and shovelling snow inside the base, which spans the 9/132 | 9/133 boundary (z = 8480). Player B portalled in during a fight.
- Zone 9/132's terrain snapped to its worldgen shape; every terraform edit in that zone was gone. Builds were untouched.
- Since then: whenever A is inside the zone, anyone portalling or logging into it hangs on the loading screen indefinitely. The moment A walks out, they load in.
- When A returns, edits made since are usually gone; when they are not, other players cannot see them, and the terrain visibly flips between two shapes while several players are present.
- We did not have logging on during the original event, so the exact sequence that created the 9/132 duplicate is inferred from code (Section A), not captured. We will attach `Player.log` from both clients and the server log from the next occurrence `[attach]`.

## Evidence from the world save

Chunked save, world version 41 (`Version.World.DeepNorth`), parsed with a byte-exact reader written against `ZDOMan.SaveChunk` / `ZDO.Save` / `TerrainComp.Save`. The chunk format stores neither ZDOIDs nor creation time, so compilers cannot be ordered by age from the file.

| zone | `_TerrainCompiler` ZDOs | `m_operations` (largest) | with 0 ops | TCData bytes (largest) | note |
|------|---:|---:|---:|---:|------|
| 9/132 (base, lost data) | 1 | 1,188 | 0 | 1,616 | only post-revert work; 130 height / 422 paint vertices |
| 9/133 (base, north half) | 1 | 43,445 | 0 | 13,509 | 1,419 height / 3,819 paint vertices |
| 8/132 | 1 | 7,861 | 0 | 10,268 | |
| 3/141 | **21** | 68 | 20 | 515 | the 20 have exactly 3 painted vertices each and no height edits |
| 5/142 | **3** | 13 | 0 | 204 | |
| 17/137 (other chunk file) | **2** | 6,443 | 0 | 6,941 | second one has 2 ops, 17 painted vertices |

Every payload parses cleanly: version 1, 65x65 arrays, no NaN/Inf, level deltas within ±8, smooth within ±1, paint channels within 0..1. This is not corrupt terrain data; it is several compiler objects for one zone. The "0 ops + 3 painted vertices" fingerprint matches the edge-spread path in Section A (spread paints one vertex per call and never increments `m_operations`).

## Code path (decompiled 1.0 `assembly_valheim`; line numbers from our decompile)

**A. How the duplicate is born**

- `Heightmap.GetAndCreateTerrainCompiler()` (Heightmap.cs:1277) → `TerrainComp.FindTerrainCompiler(pos)` (TerrainComp.cs:228), a bounding-box scan over `s_instances` only. On null it `Instantiate`s the prefab; `ZNetView.Awake` (ZNetView.cs:57-63) takes the no-init-ZDO branch and calls `ZDOMan.CreateNewZDO`. No sector lookup anywhere in this chain.
- Gameplay callers: `TerrainOp.Awake` (TerrainOp.cs:19) for every heightmap whose AABB expanded by the op radius contains the op point (so any op near a seam hits 2 or 4 heightmaps); `TerrainComp.PaintCleared` directly (TerrainComp.cs:522) when `m_centerMultiplicationFactor > 0` and the op centre lies outside this compiler's heightmap; and `TerrainComp.GetNeighbor` (TerrainComp.cs:685) from the `spread` local function, which runs whenever a painted vertex has x or y equal to 0 or `m_width`. Console-only callers: `optterrain` (Terminal.cs:217) via `Heightmap.UpdateTerrainAlpha` and `TerrainComp.UpgradeTerrain`.
- In the Deep North every shovel stroke is a paint op: `Attack.DoMeleeAttack` (Attack.cs:1221-1231) instantiates `Player.m_snowShovelDefault/Strong` once per `Heightmap` collider inside a 0.5 m overlap sphere. `Character` also instantiates `m_deepSnowWalkObj` every ~1 m of movement in deep snow and on landing (Character.cs:1134, 2334). Whether those prefabs carry a `TerrainOp` is prefab data we cannot see; if the walk/landing object is one, every arrival in deep snow fires `GetAndCreateTerrainCompiler` before the arriving client has instantiated the zone's compilers, which would explain why this triggers on every portal arrival. Please check.
- The window is real and easy to hit: the arriving client may not have received the compiler ZDO yet (ZDO sync is distance-sorted and budgeted), `CreateObjectsSorted` (ZNetScene.cs:191-232) creates only `max(backlog/100, 10)` objects per 33 ms tick (100 floor on the loading screen) and does nothing at all while `ZoneSystem.IsActiveAreaLoaded()` is false, while heightmaps for already-spawned zones exist and accept ops.

**B. Why the original loses**

- `TerrainComp.Awake` (TerrainComp.cs:19-25): if `FindTerrainCompiler` finds a compiler covering this position, log "Found another terrain compiler in this area, removing it", `ZNetScene.instance.Destroy(existing.gameObject)`, then register itself. The decision is positional only.
- `ZNetScene.Destroy` (ZNetScene.cs:105-118): `ResetZDO()` (sets `Created = false`), `m_instances.Remove`, and `ZDOMan.DestroyZDO` **only if `zdo.IsOwner()`**; `DestroyZDO` itself also returns for non-owners.
  - Owned → the ZDO carrying the zone's whole `TCData` is destroyed and propagated. Our 9/132 loss fits this: the arriving client B creates the duplicate (owned by B), it syncs to A, A instantiates it, A's `Awake` destroys A's real compiler, A owns it, it is gone everywhere. The surviving duplicate holds only spread residue plus whatever was edited afterwards, which is what the save shows.
  - Not owned → `CreateObjectsSorted` picks the un-created ZDO up again on the next tick, its `Awake` destroys the newcomer, and so on. For any compiler that already carries `TCData`, `Awake → CheckLoad → Load → m_hmap.Poke(0)` runs `Heightmap.Regenerate` synchronously (base heights, `ApplyModifiers`, collision and render mesh), so the terrain alternates between the two datasets on every swap.

**C. Why players hang in the portal / on login**

- `ZNetScene.IsAreaReady` (ZNetScene.cs:151-169): false if any ZDO with a valid prefab in the 3x3 zones around the point has no instance. During the loop above one compiler always lacks one.
- `Player.UpdateTeleport` (Player.cs:5726-5755): the 15 s / `$msg_portal_blocked` fallback is nested inside `if ((m_teleportTimer > 8f || !m_distantTeleport) && ZNetScene.instance.IsAreaReady(...))`. `TeleportWorld` teleports are distant. There is no exit while `IsAreaReady` is false.
- `Game.UpdateRespawn` (Game.cs:388, 419, 444) gates spawning on the same `IsAreaReady(logoutPoint)`, so logging into the zone hangs the same way.
- The server's `ReleaseNearbyZDOS` (ZDOMan.cs:836-888, every 2 s) re-owns the zone's ZDOs to whoever's active area contains them once the owner leaves; the next `Destroy` on that client then deletes one compiler for real, the loop ends, and the teleport completes with a random winner.

**D. Related (minor): seam edits only exist locally for non-owners**

`spread` (TerrainComp.cs:944-963) writes into `neighbor.m_modifiedPaint/m_paintMask` and calls `neighbor.Save()`, which returns early unless this client owns that compiler (TerrainComp.cs:88-91). The local heightmap then differs from the owner's ZDO until the next `CheckLoad` overwrites it. This is the "other players don't see it, then it snaps back" symptom and is separate from the duplicate problem.

## Reproduction

Not yet re-run from scratch on a fresh world; observed on our world on every portal arrival while the owner is inside. Proposed minimal repro:

1. Dedicated server, two clients, Deep North, deep snow. Client A stands within 1 m of a zone boundary (e.g. x = 543.5..544.5) and shovels snow on the boundary continuously.
2. Client B portals into that zone while A is shovelling.
3. Mechanism fired: either client's `Player.log` contains `Found another terrain compiler in this area, removing it`.
4. Expected: B arrives, terrain unchanged. Actual: B stays on the loading screen until A leaves the zone; if the destroyed compiler was A's owned original, A's terraforming in that zone is gone.
5. Confirmation from the save: count `_TerrainCompiler` ZDOs per zone in the `.chunk` files; affected zones have more than one.

## Suggested fixes

1. `Heightmap.GetAndCreateTerrainCompiler`: look up an existing `_TerrainCompiler` ZDO in the zone's sector (ZDOMan) before instantiating a new one, and instantiate/return that.
2. `Player.UpdateTeleport` and the respawn path: put the hard timeout outside the `IsAreaReady` condition so a never-ready area cannot trap a player.
3. `TerrainComp.Awake`: when two compilers collide, prefer the one with data (`m_operations` / `TCData` present) when deciding which to remove, instead of instantiation order.
4. (D) Don't write spread paint into a compiler the client doesn't own; the owner already applies the same op via `RPC_ApplyOperation`.

## How to verify with the attached world

Load the attached world, stand in zone 9/132 with a second client portalling in, and watch for the log line above. Run the attached parser on `30_20__1_75.chunk` (zones 3/141, 5/142) and `30_22__1_22.chunk` (zone 17/137) to see the stacked compilers.

## Attachments

- World folder `[name]` (`_main.176.*` + all `.chunk` files).
- `Player.log` from both clients and the server log, with timestamps for: B's teleport start, first "Found another terrain compiler" line per client, the moment terrain reverted, the hang, A leaving, B loading in `[attach from next occurrence]`.
- Chunk parser (`vhchunk.py`) used for the table.

---

## Separate ticket: piece snow-buildup writes a ZDO float every wear pass

`WearNTear.UpdateWear` (WearNTear.cs:376-395), for an owned Deep North piece with `m_snowBuildup < 1` while `EnvMan.GetSnowBuildup() > 0`, does `m_snowBuildup += buildup * Time.deltaTime * Game.m_snowBuildupSpeed` (code default 0.1) and then `ZDO.Set(ZDOVars.s_snow, ...)` on every call. `WearNTearUpdater` visits each piece about once per second, so every uncovered piece bumps its `DataRevision` and re-syncs its full ZDO roughly once per second for the whole accumulation window. Because the increment uses the frame delta inside a 1 Hz sweep, that window is frame-rate dependent: ~5 min at 30 fps, ~10 min at 60 fps, ~20 min at 120 fps. On a base with several hundred uncovered pieces this is hundreds of ZDO updates per second during snowfall. Suggest accumulating locally and only writing the ZDO on a meaningful change (visuals only switch at 0.25), and using the sweep interval rather than `Time.deltaTime`.
