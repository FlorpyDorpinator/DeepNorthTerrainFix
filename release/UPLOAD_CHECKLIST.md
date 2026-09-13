# Thunderstore upload checklist for DeepNorthTerrainFix 1.1.1

Upload the zip `FlorpyDorp-DeepNorthTerrainFix-1.1.1.zip` at https://thunderstore.io/c/valheim/create/

| field | value |
|---|---|
| Team | **FlorpyDorp** (create the team first under your profile if it does not exist; the team name becomes the package author) |
| Community | Valheim |
| Categories | Mods, Server-side, Client-side, Utility, Tweaks (pick those that exist in the list) |
| NSFW | No |
| Package name (from manifest) | DeepNorthTerrainFix |
| Version (from manifest) | 1.1.1 |
| Dependency string it will get | `FlorpyDorp-DeepNorthTerrainFix-1.1.1` |

The zip must contain, at its root: `DeepNorthTerrainFix.dll`, `manifest.json`, `README.md`, `icon.png` (256x256),
`CHANGELOG.md`, `LICENSE`. Thunderstore validates `manifest.json` and the icon size on upload. The mod
manager (r2modman / Thunderstore Mod Manager) installs the DLL into `BepInEx/plugins/FlorpyDorp-DeepNorthTerrainFix/`.

Before uploading:

1. Put your repository or contact URL into `manifest.json` → `website_url` (currently empty, which is allowed).
2. Test once on your own server and one client: start the server, confirm the log shows
   `DeepNorthTerrainFix 1.1.1 loaded`, `Console commands registered`, and `[heal on load]` lines; type `dntf_scan`
   in the server console; portal into the base zone with a second player.
3. Bump `version_number` in `manifest.json`, `VERSION` in `Plugin.cs` and `<Version>` in the csproj together
   for every future release; Thunderstore rejects re-uploads of the same version.

Suggested Thunderstore description (already in manifest, 140 chars):

> Heals duplicate terrain compilers that delete Deep North terraforming, trap players in portals and cause snow seams. Server + client.

Manual install for people without a mod manager: extract the zip and copy `DeepNorthTerrainFix.dll` into
`BepInEx/plugins/` on the server and on every client (BepInExPack for Valheim required).
