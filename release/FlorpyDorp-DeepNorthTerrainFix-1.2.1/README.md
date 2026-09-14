# DeepNorthTerrainFix

Created by **FlorpyDorp**. MIT licensed.

Fixes the Valheim 1.0 Deep North bug where a zone's terraforming vanishes, players get stuck forever in a portal
or on the login screen while someone is standing in that zone, the terrain flickers between two shapes, and
hard seams appear along zone borders in the snow.

**Server owners: drop it on the server and you're done. Your players do not need to install anything.**
The server heals the world it already has, removes duplicate compilers and dead stroke objects the instant they
arrive (before any other player can see them), and refuses to let an unmodded game delete a zone's terraforming.
Vanilla clients keep connecting as normal; nothing about the network protocol changes. Players *may* also install
it on their own game for extras (a 25 s portal escape, prevention at the source, the snow-ridge fix for their own
strokes), but that is optional. One DLL works in both places.

> **Updating from 1.1.x?** Do it now. 1.1.x on a server alone could still lose a zone's terraforming to an
> unmodded player (see the changelog). Your existing config file is migrated on the first start:
> `PatchNetworkBudget` is reset to `false` once (the log says so). Nothing else to do.

## What it does

**Heals the save**
- On world load, every 2 minutes, and whenever a new terrain compiler arrives over the network, it finds
  zones that have more than one `_TerrainCompiler` object, merges their terrain data into the one with the
  most data, and removes the rest. The result is written with the next world save.
- Console commands to scan, fix, reconcile zone-border seams, or reset one zone's terrain (buildings kept).

**Protects the terraforming** (server)
- A duplicate that arrives is removed inside the receive path and is never forwarded to any other player.
- The server refuses a client's request to destroy the compiler that holds a zone's terraforming. An unmodded
  game sends exactly that request when a duplicate appears on its screen while it owns the real one; vanilla
  then deletes every stroke in the zone. The compiler is kept and re-sent to everyone instead.

**Prevents new damage** (client side, where terrain objects actually live)
- Reuses the zone's existing compiler instead of spawning a duplicate.
- When two compilers collide, keeps the one with data instead of whichever spawned first, and removes the
  other properly so the create/destroy loop cannot start.
- Hard 25 s teleport timeout so nobody is trapped in a portal.
- Stops a hoe/shovel stroke that crosses a zone line from being applied twice on the shared column (the
  one-vertex ridge along zone borders in snow).
- Optional: raises the Steam send-rate cap and ZDO budget (off by default), and throttles the per-piece
  snow-buildup sync that floods the network at large bases during snowfall.

**Cannot do:** bring back terraforming that vanilla already deleted. For that you need a backup of the world
folder from before it happened (the mod only stops it happening again; the repository has an offline tool that
can copy one zone's terrain from a backup into the current save).

## Install

1. Install [BepInExPack for Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) on the
   dedicated server (most hosting panels have a one-click BepInEx option).
2. Drop `DeepNorthTerrainFix.dll` into `BepInEx/plugins/`.
3. Restart the server. That's it. The log (`BepInEx/LogOutput.log`) shows `[heal on load]` lines for every
   zone it repaired, and the repaired world is written at the next autosave (every 30 minutes) or when an
   admin saves.

Players can optionally install the same DLL on their own game for the client-side extras.

Back up your world folder before the first run. The mod changes world data.

## No console access? (most hosting panels)

You don't need the console. Everything important is automatic: heal on load, removal of duplicates and dead
stroke objects as they arrive, protection of the real compiler, and a background sweep every two minutes. The
console commands below are diagnostics and manual tools.

If you do want them without console access, install the mod on your own game too and put your Steam ID in the
server's `adminlist.txt` (your host's file manager can edit it; the ID is the 17-digit number the server log
prints next to your name). Then, in game, press F5 and type the command. `dntf_fix`, `dntf_seams`,
`dntf_reset` and `dntf_purge` are forwarded to the server and executed there for admins; their output goes to
the server log. `dntf_scan`, `dntf_zone` and `dntf_zdos` run on your own game and print in your console.
Vanilla's `save` typed in F5 also saves the server world when you're an admin.

**Who needs it.** Each part of the fix runs where the problem lives:

| problem | server only | one modded client | every client |
|---|---|---|---|
| duplicate compilers deleting terraforming | stopped: duplicates removed on arrival, real compiler protected from client destroys | also healed in that player's area | also prevented at the source |
| dead stroke objects that trap players in portals | removed the moment they arrive, and on load | removed in that player's area | never created |
| portal hang escape (25 s timeout) | no | that player only | yes |
| double-applied border strokes (snow ridges) | no | that player's strokes only | yes |
| snow sync throttling | server half | that player's half | yes |

So: server alone stops the terraforming loss and the portal trap for everyone. Everyone installed is the
full fix.

## Console commands

Type in the dedicated server console, or in the F5 console as the host of a local game. A joined client can
type the write commands too: they are forwarded to the server, which only runs them for players on its
adminlist (results appear in the server console/log).

| command | what it does |
|---|---|
| `dntf_scan` | Lists zones with more than one terrain compiler. Read-only, local. |
| `dntf_zone` | Prints your current zone coordinates and its compilers. Local. |
| `dntf_fix [nomerge]` | Merges and removes duplicate compilers in the whole world. |
| `dntf_seams [heights\|paint\|both] [zoneX zoneY radius]` | Makes every shared border vertex agree across the zones that own it (one-sided edits are copied across, conflicting ones averaged, corners resolved once). Whole world unless a centre zone and radius are given. A host can also type `dntf_seams both 3` for 3 zones around their character. |
| `dntf_reset <zoneX> <zoneY>` | Wipes one zone's terrain modifications. Buildings stay. Terrain returns to world generation. |
| `dntf_zdos <zoneX> <zoneY> [radius]` | Lists every object type in a zone as the running game sees it: prefab name, count, how many are non-persistent (never saved), owners, and on a client how many are stuck as "created but dead" (the state that blocks portals). Local, read-only. |
| `dntf_purge <zoneX> <zoneY> <prefab> [radius]` | Removes non-persistent objects of one prefab (name or hash from `dntf_zdos`) from a zone. Never touches buildings or anything that is saved. |

Zone coordinates: zone X = round(worldX / 64), zone Y = round(worldZ / 64). `dntf_zone` does it for you.

## Config (`BepInEx/config/FlorpyDorp.DeepNorthTerrainFix.cfg`)

| section | key | default | notes |
|---|---|---|---|
| Heal | HealOnWorldLoad | true | server/host: dedupe whole world after load |
| Heal | HealOnArrival | true | dedupe a zone the moment a new compiler ZDO arrives (server: synchronously, before it is forwarded) |
| Heal | HealPeriodically / HealIntervalSeconds | true / 120 | server/host full scan |
| Heal | MergeDuplicateData | true | union the duplicate's vertices into the survivor before removal |
| Heal | ClientMayHeal | true | let joined clients run the arrival heal and ghost purge too |
| Heal | PurgeGhostTerrainOps | true | remove ZDOs whose prefab is a self-destructing terrain op (on load, on arrival, periodic sweep) |
| Heal | ProtectRichestCompiler | true | server: refuse a client's destroy of the compiler holding a zone's terraforming; keep this on |
| Terrain | FixDuplicateCompilers, MergeOnDedupe, ReuseExistingCompilerZdo | true | client-side prevention |
| Terrain | SkipUnownedNeighborSpread, FixDoubleAppliedBorderStrokes | true | seam prevention |
| Terrain | CleanupNetworkedTerrainOps | true | a stroke object that carries a network view is destroyed properly instead of leaving a dead ZDO that blocks "area ready" |
| Teleport | HardTimeout / HardTimeoutSeconds | true / 25 | portal hang escape |
| Network | PatchNetworkBudget, SteamSendRateBytesPerSec, ZdoInFlightBudgetBytes | **false** / 512000 / 30720 | vanilla 153600 / 10240; off by default since 1.2.0 (can hurt players with weak uploads) |
| Snow | ThrottleSnowWrites / SnowWriteStep | true / 0.05 | piece snow-buildup sync |

## Why this happens (plain language)

Every 64 m square of the map has one invisible "terrain notebook" (the game calls it a terrain compiler).
Every hoe, pickaxe and snow-shovel stroke in that square is written into it. There should be exactly one per
square.

When you shovel or hoe near the edge of a square, or when someone portals in and starts working before their
game has finished spawning that square's objects, the game looks for the notebook only among objects already
spawned on that player's screen. It doesn't find it, so it creates a brand-new blank one. Now the square has
two notebooks.

When the game later has both on screen it always destroys the one it spawned first, without looking at which
one holds the real data. If the player doing this "owns" that notebook (the nearest player owns the objects
around them), it is deleted from the world for everyone: all terraforming in that square snaps back to how
the world generated it. If they don't own it, the game cannot delete it, so it re-spawns it next tick, which
destroys the other one, and so on thirty times a second. While that loop runs the square never counts as
"ready", and the portal and login screens wait for "ready" with no timeout. That's the friend stuck in the
portal until the owner walks out of the square and ownership moves to them.

**What the server can and cannot do about it.** The server never spawns terrain objects, so it cannot stop a
player's game from *asking* to delete the notebook. What it can do, and what 1.2.0 does, is say no: a request
to destroy the notebook that holds the square's terraforming is refused and the notebook is sent straight back.
It also never lets a duplicate reach other players in the first place.

**Snow.** In the Deep North the snow depth is stored in that same notebook, as one number per metre of
ground, and drawn as up to two metres of height. Each square's grid is separate; nothing blends across the
line between two squares. So any time two neighbouring notebooks disagree about their shared edge you get a
dead-straight step along the zone line: one notebook was deleted (above), one square got a stroke and the
other didn't (dropped network message), or, a genuine code bug, a stroke that overlaps two squares is applied
to both squares *and* copied across the border, so the shared column gets it twice. Old-style paint was a
blend that barely moved when applied twice; snow paint is additive, so the second application shows as a
ridge.

**Why the border seams were never fixed.** They were always there, just faint: each square computes its
lighting from its own triangles only, and any edit that reaches one side of a line but not the other leaves a
small step. Before 1.0 that was a lighting line and the occasional crack. The Deep North turned the paint
layer into geometry, so an invisible paint mismatch became a wall.

## Notes

- Built against Valheim 1.0.12 from decompiled code and verified against it line by line. Please report issues
  with your `BepInEx/LogOutput.log`.
- When the server heals a zone that a player is actively terraforming, that player's copy reloads the merged
  block (a brief terrain rebuild) and at most one in-flight stroke can be lost. The player keeps ownership of
  the zone throughout.
- When the server refuses a destroy, the player whose game sent it sees the zone's terrain rebuild once as the
  compiler comes back. Their own strokes since then are merged, not lost.
- Snow-buildup throttling keeps the accumulated value locally between syncs, so snow still piles up and
  heavy-snow damage still triggers; only the network traffic is reduced.
- Source, the full bug analysis and a Python tool for offline save repair: see the repository.
- Author: FlorpyDorp. Bug reports and feedback are welcome on the Thunderstore page or the repository.
