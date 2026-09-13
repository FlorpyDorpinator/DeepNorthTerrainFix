using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeepNorthTerrainFix
{
    /// <summary>
    /// ZDO-level repairs that work on the loaded world (server, single-player host, or a client with ownership):
    ///  - Dedupe: zones with more than one _TerrainCompiler keep the richest, merge the rest in, remove the rest.
    ///  - Seams: make the shared border vertices of adjacent zones' compilers agree.
    ///  - Reset: wipe a zone's terrain modifications (buildings untouched).
    /// </summary>
    internal static class SaveHealer
    {
        public sealed class Result
        {
            public int ZonesScanned;
            public int ZonesWithDuplicates;
            public int Removed;
            public int MergedVertices;
            public int SeamFixes;
            public int BlocksWritten;
            public readonly List<string> Lines = new List<string>();

            public string Summary(string what)
            {
                return $"{what}: zones={ZonesScanned} duplicateZones={ZonesWithDuplicates} removed={Removed} mergedVertices={MergedVertices} seamFixes={SeamFixes} blocksWritten={BlocksWritten}";
            }
        }

        // ---------------------------------------------------------------- dedupe

        public static Result DedupeAll(ZDOMan man, bool merge)
        {
            var groups = Compilers.AllByZone(man);
            var r = new Result { ZonesScanned = groups.Count };
            foreach (var kv in groups)
            {
                if (kv.Value.Count > 1) DedupeGroup(Compilers.ZoneFromKey(kv.Key), kv.Value, merge, r);
            }
            return r;
        }

        public static Result DedupeZone(ZDOMan man, Vector2s zone, bool merge)
        {
            var list = Compilers.InZone(man, zone);
            var r = new Result { ZonesScanned = 1 };
            if (list.Count > 1) DedupeGroup(zone, list, merge, r);
            return r;
        }

        private static void DedupeGroup(Vector2s zone, List<ZDO> list, bool merge, Result r)
        {
            r.ZonesWithDuplicates++;
            var ranked = Compilers.Rank(list);
            ZDO survivor = ranked[0];
            TcBlock merged = null;
            int mergedHere = 0;
            if (merge)
            {
                merged = Compilers.Load(survivor);
                for (int i = 1; i < ranked.Count; i++)
                {
                    var ob = Compilers.Load(ranked[i]);
                    if (ob == null) continue;
                    if (merged == null) { merged = ob; mergedHere += ob.ModifiedCount; continue; }
                    mergedHere += merged.MergeFrom(ob);
                }
            }
            for (int i = 1; i < ranked.Count; i++)
            {
                Compilers.Remove(ranked[i]);
                r.Removed++;
            }
            if (merge && merged != null && mergedHere > 0)
            {
                Compilers.Store(survivor, merged);
                r.BlocksWritten++;
                r.MergedVertices += mergedHere;
            }
            r.Lines.Add($"zone {zone.x},{zone.y}: {list.Count} compilers -> kept {Compilers.Describe(survivor)}, removed {ranked.Count - 1}, merged {mergedHere} vertices");
        }

        // ---------------------------------------------------------------- seams

        /// <summary>
        /// For every pair of horizontally/vertically adjacent zones that both have a compiler, make the shared
        /// border vertices agree: if only one side modified a vertex, copy it across; if both did and they differ,
        /// average them. Removes cracks in the mesh (heights) and one-column snow steps (paint).
        /// </summary>
        public static Result ReconcileSeams(ZDOMan man, Func<Vector2s, bool> zoneFilter, bool heights, bool paint)
        {
            var groups = Compilers.AllByZone(man);
            var blocks = new Dictionary<long, Entry>();
            foreach (var kv in groups)
            {
                var zone = Compilers.ZoneFromKey(kv.Key);
                if (zoneFilter != null && !zoneFilter(zone)) continue;
                var zdo = Compilers.Rank(kv.Value)[0];
                var block = Compilers.Load(zdo);
                if (block == null || block.ModH.Length != block.ModP.Length) continue;
                blocks[kv.Key] = new Entry { Zdo = zdo, Block = block };
            }
            var r = new Result { ZonesScanned = blocks.Count };
            // Group every border vertex by the world vertex it represents (edges: 2 zones, corners: up to 4), then
            // resolve each group once. Pairwise east/north passes leave corners order-dependent and cracked.
            var shared = new Dictionary<(int pitch, int wx, int wy), List<(Entry e, int idx)>>();
            foreach (var kv in blocks)
            {
                var zone = Compilers.ZoneFromKey(kv.Key);
                var e = kv.Value;
                int pitch = e.Block.Pitch, last = pitch - 1;
                for (int y = 0; y <= last; y++)
                {
                    for (int x = 0; x <= last; x++)
                    {
                        if (x != 0 && x != last && y != 0 && y != last) continue;
                        var key = (pitch, zone.x * last + x, zone.y * last + y);
                        if (!shared.TryGetValue(key, out var list)) shared[key] = list = new List<(Entry, int)>(4);
                        list.Add((e, y * pitch + x));
                    }
                }
            }
            foreach (var g in shared.Values)
            {
                if (g.Count > 1) ReconcileGroup(g, heights, paint, r);
            }
            foreach (var kv in blocks)
            {
                if (!kv.Value.Dirty) continue;
                Compilers.Store(kv.Value.Zdo, kv.Value.Block);
                r.BlocksWritten++;
                var zone = Compilers.ZoneFromKey(kv.Key);
                r.Lines.Add($"zone {zone.x},{zone.y}: border vertices updated");
            }
            return r;
        }

        private sealed class Entry
        {
            public ZDO Zdo;
            public TcBlock Block;
            public bool Dirty;
        }

        /// <summary>All members share one world vertex. Modified members are averaged (a single modified member is
        /// copied) and every member is set to that value, so the shared vertex is identical in every zone that owns it.</summary>
        private static void ReconcileGroup(List<(Entry e, int idx)> g, bool heights, bool paint, Result r)
        {
            if (heights)
            {
                int n = 0; float l = 0f, s = 0f;
                foreach (var (e, i) in g) if (e.Block.ModH[i]) { n++; l += e.Block.Lvl[i]; s += e.Block.Smo[i]; }
                if (n > 0)
                {
                    l /= n; s /= n;
                    bool changed = false;
                    foreach (var (e, i) in g)
                    {
                        if (e.Block.ModH[i] && Near(e.Block.Lvl[i], l) && Near(e.Block.Smo[i], s)) continue;
                        e.Block.ModH[i] = true; e.Block.Lvl[i] = l; e.Block.Smo[i] = s;
                        e.Dirty = true; changed = true;
                    }
                    if (changed) r.SeamFixes++;
                }
            }
            if (paint)
            {
                int n = 0; float cr = 0f, cg = 0f, cb = 0f, ca = 0f;
                foreach (var (e, i) in g) if (e.Block.ModP[i]) { n++; var c = e.Block.Paint[i]; cr += c.r; cg += c.g; cb += c.b; ca += c.a; }
                if (n > 0)
                {
                    var avg = new Color(cr / n, cg / n, cb / n, ca / n);
                    bool changed = false;
                    foreach (var (e, i) in g)
                    {
                        var c = e.Block.Paint[i];
                        if (e.Block.ModP[i] && Near(c.r, avg.r) && Near(c.g, avg.g) && Near(c.b, avg.b) && Near(c.a, avg.a)) continue;
                        e.Block.ModP[i] = true; e.Block.Paint[i] = avg;
                        e.Dirty = true; changed = true;
                    }
                    if (changed) r.SeamFixes++;
                }
            }
        }

        private static bool Near(float x, float y) => Mathf.Abs(x - y) < 0.0005f;

        // ---------------------------------------------------------------- reset / report

        public static int ResetZone(ZDOMan man, Vector2s zone)
        {
            int n = 0;
            foreach (var zdo in Compilers.InZone(man, zone))
            {
                var old = Compilers.Load(zdo);
                Compilers.Store(zdo, TcBlock.Empty(old != null ? old.ModH.Length : TcBlock.DefaultVertices));
                n++;
            }
            return n;
        }

        public static List<string> Report(ZDOMan man, int maxLines = 40)
        {
            var groups = Compilers.AllByZone(man);
            var lines = new List<string>();
            int total = 0, dupZones = 0, extra = 0;
            foreach (var kv in groups)
            {
                total += kv.Value.Count;
                if (kv.Value.Count > 1)
                {
                    dupZones++; extra += kv.Value.Count - 1;
                    if (lines.Count < maxLines)
                    {
                        var z = Compilers.ZoneFromKey(kv.Key);
                        var ranked = Compilers.Rank(kv.Value);
                        lines.Add($"zone {z.x},{z.y}: {kv.Value.Count} compilers, richest {Compilers.Describe(ranked[0])}");
                    }
                }
            }
            lines.Insert(0, $"terrain compilers: {total} in {groups.Count} zones; zones with duplicates: {dupZones} ({extra} extra compilers)");
            return lines;
        }
    }
}
