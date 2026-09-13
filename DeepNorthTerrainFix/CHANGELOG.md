# Changelog

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
