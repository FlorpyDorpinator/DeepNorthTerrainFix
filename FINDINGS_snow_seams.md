# Deep North: why the snow terrain has hard seams and steps

Line numbers refer to a decompile of Valheim 1.0.12's `assembly_valheim.dll` (not included in this repository).
Every claim below was checked by an independent refutation pass against that source. Prefab and shader
contents are not in the decompile; where a statement depends on them it says so.

## 1. What the seams are made of

In the Deep North the terrain paint mask's green channel *is* the snow depth. It lives in a 65x65 per-zone
texture (`_ClearedMaskTex`, Heightmap.cs:257-261, one per 64 m zone, clamp wrap). The mesh you collide with is
built from heights only (Heightmap.cs:445-451, 527-579; the render mesh copies it, 621-624). The snow surface
the game reasons about is height + `lerp(0.1 m, 2.1 m, g)` (Heightmap.cs:857-878) sampled at **one texel with
no interpolation** (`GetCultivationMask`, Heightmap.cs:832-838), and the terrain shader renders that same
channel as surface height. So any two adjacent texels that disagree, or a disagreement between zone A's
column 64 and zone B's column 0 (the same world vertices), is a hard step of up to 2 m. There is no code that
blends neighbouring zones' masks.

The base snow mask from world generation is continuous across zone borders (HeightmapBuilder.cs:106-168,
WorldGenerator.cs:1301-1329, verified). Every real seam comes from **edit data existing on one side of a line
and not the other**, or from the near/LOD hand-off.

## 2. Causes, ranked

1. **A zone whose terrain compiler was replaced or lost, next to zones that kept theirs.** This is exactly
   zone 9/132 on the world this was investigated on: its compiler was replaced by a near-empty duplicate (see
   BUG_REPORT.md), so its snow depth and heights reverted to worldgen, while 9/133 and 8/132 still carry every
   shovel stroke and level op right up to the border. Result: a perfectly straight step along the 64 m zone
   line for the whole length of the edited area. The same happens in multiplayer when a per-zone op RPC is
   dropped because the neighbour's compiler owner has no instance (ZRoutedRpc.cs:178-201 silently drops it;
   TerrainComp.cs:298-303).
2. **Edge of the loaded area.** Distant-LOD heightmaps never apply compiler data (Heightmap.cs:394), use a
   10 m mesh and a 10 m mask texel (TerrainLod.cs:21-49, HeightmapBuilder.cs:142-146). Every slab and every
   snow edit stops dead on that zone-aligned line, always present, most visible on flat snow. That is the
   candidate for a straight step running to the horizon.
3. **Double-applied strokes along zone borders (real code bug, single player too).** A shovel/hoe op that
   overlaps two zones is applied to both (TerrainOp.cs:15-20). When you own both compilers the RPC runs
   synchronously (ZRoutedRpc.cs:101-104), so zone A paints first, and its edge handler `spread`
   (TerrainComp.cs:623-658, 944-963) copies the finished edge value into zone B and pokes B. B's own pass then
   reads that already-modified value as `from` (TerrainComp.cs:890-897, because B was poked) and applies the
   stroke again. In the Deep North the snow paint is additive (`Clamp01(from + value)`, TerrainComp.cs:934)
   rather than a lerp, so the shared border column gets twice the depth change, and B's spread writes the
   doubled value back into A. Net effect: a one-vertex ridge or trench exactly along every zone border inside
   shovelled snow. Invisible when a full-strength stroke already saturates to 0 or 1; visible for partial
   strokes and low weights. The old lerp paints hid this because a lerp applied twice barely moves.
4. **Sharp rims around edits are by design but amplified by snow.** Level ops are exact flat squares with no
   falloff (TerrainModifier default `m_square`), the paint falloff `(1 - d/r)^0.1` is near-full to the rim
   then drops to zero within one vertex, and additive snow paint saturates to a plateau. With the mask drawn
   as up to 2 m of height, that rim is a cliff instead of a colour change.
5. **Shading seams.** Normals are recalculated per zone (Heightmap.cs:629), so lighting differs along every
   border on smooth bright snow even where geometry matches.

Two smaller inconsistencies found on the way, both real:

- **Paint and read are half a vertex apart.** Painting subtracts 0.5 m before mapping to a texel
  (TerrainComp.cs:498-507), but `GetCultivationMask`, `IsCultivated` and `GetPaintMask(Vector3)` do not
  (Heightmap.cs:832-838, 947-952), while `IsCleared` and `GetVegetationMask` do. Movement slowdown, camera
  snow avoidance and the deep-snow build check therefore read snow depth up to one vertex away from where the
  shovel painted it.
- **Old-world remap.** Compiler data saved before the 65x65 mask (pre-May-2024 worlds) is stretched by
  duplicating column/row 63 into 64 on load (TerrainComp.cs:200-223), which creates a one-texel seam on the
  +x/+z borders of such zones. Not relevant to zones first edited in 1.0.

Not causes (verified): worldgen base heights/mask, the legacy `Heightmap.PaintCleared` / `LevelTerrain` paths,
`WorldToVertex` vs `WorldToVertexMask` (identical for width 64), `SetSnowMask` (dead code, and it passes y
instead of z), clutter, material setup. `UpdatePaintMask` / `UpdateTerrainAlpha` index a 65-pitch array with
a 64 pitch (diagonal shear, alpha only) but are reachable only from the `optterrain` console command and are
a no-op in the Deep North because alpha is 0 there.

## 3. What the mod does about it

- Duplicate compilers (cause 1) are prevented on clients and healed in the save on the server.
- The border copy is skipped when the neighbouring zone receives the same stroke itself (cause 3).
- `dntf_seams` makes every shared border vertex agree across the zones that own it, which removes cracks and
  one-column steps from existing worlds. It cannot restore data a zone has lost (cause 1); that needs a backup.
- Causes 2, 4 and 5 are engine behaviour and are left alone.
