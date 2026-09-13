# DeepNorthTerrainFix

By **FlorpyDorp**. MIT licensed. BepInEx mod plus the investigation behind it.

Valheim 1.0's Deep North can end up with two "terrain compiler" objects in one 64 m zone. When that
happens, the game deletes whichever spawned first (often the one holding all your terraforming), loops
create/destroy on other players' screens, traps anyone portalling or logging into that zone, and leaves
hard seams in the snow along zone borders. This repository holds:

| path | what |
|---|---|
| [DeepNorthTerrainFix/](DeepNorthTerrainFix/) | the mod: source, Thunderstore README, manifest, icon, changelog |
| [release/](release/) | the packaged Thunderstore zip and an upload checklist |
| [tools/](tools/) | Python tools: a byte-exact `.chunk` / terrain-block parser and an offline save repair script |
| [BUG_REPORT.md](BUG_REPORT.md) | the developer-facing bug report, every claim verified against the decompiled 1.0.12 code |
| [FINDINGS_snow_seams.md](FINDINGS_snow_seams.md) | why Deep North snow terrain has seams and steps, and what the mod does about it |

The decompiled game source the analysis cites (`assembly_valheim/...`) is **not** in this repository, since
it's Iron Gate's code. Decompile `assembly_valheim.dll` from your own install with ILSpy or dnSpy into an
`assembly_valheim/` folder next to this README and the `file:line` references resolve. The world save the
numbers below come from is also not included.

**Install the mod:** grab the zip from [Releases](../../releases) or Thunderstore, drop `DeepNorthTerrainFix.dll`
into `BepInEx/plugins/` on the server and on every client. Details in
[DeepNorthTerrainFix/README.md](DeepNorthTerrainFix/README.md).

**Build:** `dotnet build -c Release` inside `DeepNorthTerrainFix/` (.NET SDK 8+; set `-p:ValheimDir=...` if
Valheim is not in the default Steam folder). NuGet pulls BepInEx and HarmonyX from the BepInEx feed.

---

## The investigation

World: `TheCapitolCorrupt` (chunked save format, world version 41 "DeepNorth").
Problem zone: **9/132** (world x 544..608, z 8416..8480; base at ~557, 8470). Stored in
`30_20__1_75.chunk` together with zones x 0..15, y 128..143.

## What is actually wrong

The terrain block for 9/132 is **not corrupted**. Every value in it is in range (no NaN, deltas within
±8 m, paint mask within 0..1, 1.6 KB compressed). What the save shows instead:

| zone | terrain ops | edited vertices | painted vertices | TCData size |
|------|------------:|----------------:|-----------------:|------------:|
| 9/132 (base) | 1,188 | 130 | 422 | 1.6 KB |
| 9/133 (north neighbour) | 43,445 | 1,419 | 3,819 | 13.5 KB |
| 8/132 (west neighbour) | 7,861 | 369 | 3,911 | 10.3 KB |
| 3/141 | 21 separate compilers stacked in one zone | | | |
| 5/142 | 3 separate compilers stacked in one zone | | | |
| 17/137 | 2 separate compilers stacked in one zone | | | |

The base's terraforming in 9/132 is gone from the file. The compiler that survives there only holds
work done after the revert. Zones next to it carry the heavy shovelling. Three other zones in the same
world have *stacked duplicate* `_TerrainCompiler` objects, which is the signature of the bug below.

### Root cause chain (decompiled `assembly_valheim`)

1. **Duplicate compilers get created.** `Heightmap.GetAndCreateTerrainCompiler()`
   ([Heightmap.cs:1277](assembly_valheim/Heightmap.cs#L1277)) only looks at compilers that are *instantiated on
   this client* (`TerrainComp.FindTerrainCompiler`, [TerrainComp.cs:228](assembly_valheim/TerrainComp.cs#L228)).
   If the zone's compiler ZDO exists but hasn't been instantiated yet (you just arrived, a friend just
   portalled in, object creation is throttled to 10/frame and ordered by type), it instantiates a **new**
   compiler, which is a brand-new persistent ZDO. Callers: every hoe/shovel op
   ([TerrainOp.cs:19](assembly_valheim/TerrainOp.cs#L19)) and, new in 1.0, the snow-paint edge "spread"
   into neighbouring zones ([TerrainComp.cs g__spread](assembly_valheim/TerrainComp.cs#L944)) which fires for
   every shovel stroke within one vertex of a zone edge. Your base sits 10 m from the 9/133 edge and
   13 m from the 8/132 edge.
2. **Duplicates fight, and the original loses.** `TerrainComp.Awake`
   ([TerrainComp.cs:10-27](assembly_valheim/TerrainComp.cs#L10-L27)) destroys *whichever compiler was already
   there* when a second one appears. If the client owns that ZDO the real terraforming is deleted from
   the world (your "terrain reverted to original"). If it does not own it, the destroyed object is
   re-created next tick (`ZNetScene.CreateDestroyObjects` every 33 ms), which destroys the newcomer,
   and so on forever. Each swap re-runs a full heightmap rebuild
   ([TerrainComp.cs:131-150](assembly_valheim/TerrainComp.cs#L131-L150)) = terrain visibly flipping /
   rubber-banding.
3. **The fight jams the portal.** `Player.UpdateTeleport`
   ([Player.cs:5726-5736](assembly_valheim/Player.cs#L5726-L5736)) only finishes when
   `ZNetScene.IsAreaReady` is true, and the 15 s fallback is *inside* that condition. `IsAreaReady`
   ([ZNetScene.cs:151-170](assembly_valheim/ZNetScene.cs#L151-L170)) requires every ZDO in the 3x3 zones
   around the target to have an instance. During the fight one compiler is always missing its
   instance, so the friend waits forever. When you leave the zone the server hands ownership to the
   friend, the next destroy actually goes through, the loop ends and they load in, with whichever
   compiler happened to win.
4. **Why you can't see each other's changes at the seam.** Edge spread writes directly into the
   neighbour compiler's arrays and calls `Save()`, which silently does nothing unless you own that
   compiler ([TerrainComp.cs:82-90](assembly_valheim/TerrainComp.cs#L82-L90)). Your client shows the change,
   nobody else does, and it snaps back when the owner next saves.
5. **Network pressure.** Every shovel stroke re-serialises the whole compiler block (13.5 KB for 9/133)
   and the per-peer in-flight budget is 10 KB per 50 ms
   ([ZDOMan.cs:981-989](assembly_valheim/ZDOMan.cs#L981-L989)). The new piece snow system writes a float
   to every building piece's ZDO on every wear tick while snow accumulates
   ([WearNTear.cs:393-395](assembly_valheim/WearNTear.cs#L393-L395)), for hundreds of pieces at once.
   Steam send rate is capped at 153,600 B/s ([ZSteamSocket.cs:58-63](assembly_valheim/ZSteamSocket.cs#L58-L63)),
   not 60 KB/s.

## Repaired save

`TheCapitolCorrupt_repaired/` is a full copy of the save with the 23 stacked duplicate compilers
merged into one per zone. Everything else is byte-identical. Install it by replacing the world folder
on the server (stop the server first, keep a copy of the old folder).

The original terraforming of 9/132 is **not in this save anymore**, so it cannot be reconstructed from
it. To bring it back, use the pre-corruption backup:

```
python tools/fix_save.py --in TheCapitolCorrupt --out TheCapitolCorrupt_restored --dedupe-tc ^
    --restore-tc-from "<path to backup world folder>" --zones "9,132" --merge
```

`--merge` keeps anything edited since the backup where the backup has nothing; drop it for an exact
copy of the backup's terrain block. Add `;9,133;8,132` to `--zones` if those look wrong too.
`--reset-tc "9,132"` wipes the terrain block of a zone while leaving every building untouched
(the thing `zones_reset` could not do).

## Runtime fix (BepInEx plugin)

`DeepNorthTerrainFix/` builds `DeepNorthTerrainFix.dll` (`dotnet build -c Release`; a copy is in
`dist/`). Install BepInExPack for Valheim on **every client and the dedicated server**, drop the DLL in
`BepInEx/plugins/`. All patches are individually switchable in
`BepInEx/config/FlorpyDorp.DeepNorthTerrainFix.cfg`.

Version 1.1.0 adds save healing: on world load, every 2 minutes, and whenever a compiler ZDO arrives over the
network, the server (or local host) merges and removes duplicate compilers at the ZDO level, plus console
commands `dntf_scan`, `dntf_fix`, `dntf_seams`, `dntf_reset`, `dntf_zone`. See
[DeepNorthTerrainFix/README.md](DeepNorthTerrainFix/README.md) for the community-facing documentation.

| patch | what it changes |
|-------|-----------------|
| `ZDOMan.LoadChunks` / `ZDO.Deserialize` (postfix) | Feed the healer: dedupe the whole world after load, and any zone a new compiler arrives in. |
| `TerrainComp.GetNeighbor` | Also skips the edge copy when the neighbour zone receives the same stroke itself (fixes double-applied border strokes). |
| `TerrainComp.Awake` | On a duplicate, keep the compiler with the most data, merge the other's vertices into it, claim ownership, remove it once. No more original-loses, no more create/destroy loop. |
| `Heightmap.GetAndCreateTerrainCompiler` | Look up the zone's existing compiler ZDO and instantiate *that* instead of creating a new one. Stops duplicates being born. |
| `TerrainComp.GetNeighbor` | Never spread paint into a compiler this client doesn't own (the owner applies the same op itself). |
| `Player.UpdateTeleport` | Hard 25 s timeout so nobody is stuck in a portal forever; logs why it's waiting. |
| `ZDOMan.SendZDOs` | In-flight budget 10 KB → 30 KB (configurable). |
| `ZSteamSocket.RegisterGlobalCallbacks` | Steam send rate 150 KB/s → 500 KB/s (configurable). |
| `WearNTear.UpdateWear` | Only sync a piece's snow buildup when it changed by ≥ 0.05. |

## Tools

* `tools/vhchunk.py` – parser/writer for `.chunk` files and the terrain block (`TCData`), byte-exact round trip.
* `tools/fix_save.py` – dedupe / reset / restore terrain compilers, rewrites the `.chunks` index.
