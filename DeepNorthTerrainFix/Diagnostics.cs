using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeepNorthTerrainFix
{
    /// <summary>Live-world diagnostics: what is actually in a zone right now, including objects that never reach the save.</summary>
    internal static class Diagnostics
    {
        private sealed class Row
        {
            public int Hash;
            public string Name;
            public int Count, Persistent, NonPersistent, Distant, Unowned, DeadInstance, NoInstance;
            public readonly HashSet<long> Owners = new HashSet<long>();
        }

        public static string PrefabName(int hash)
        {
            try
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(hash) : null;
                return go != null ? go.name : "?";
            }
            catch { return "?"; }
        }

        private static IEnumerable<ZDO> ZdosIn(ZDOMan man, Vector2s center, int radius)
        {
            var sectors = Compilers.SectorLists(man);
            if (sectors == null) yield break;
            for (int y = center.y - radius; y <= center.y + radius; y++)
            {
                for (int x = center.x - radius; x <= center.x + radius; x++)
                {
                    var idx = ZoneSystem.SectorToIndex(new Vector2s(x, y));
                    if (idx.Sector >= sectors.Length) continue;
                    var list = sectors[idx.Sector];
                    if (list == null) continue;
                    foreach (var zdo in list) yield return zdo;
                }
            }
        }

        public static List<string> ZoneCensus(ZDOMan man, Vector2s center, int radius, int maxRows = 60)
        {
            var rows = new Dictionary<int, Row>();
            int total = 0, nonPersistent = 0, dead = 0;
            bool haveScene = ZNetScene.instance != null;
            bool clientView = haveScene && Player.m_localPlayer != null; // a dedicated server has no instances at all
            foreach (var zdo in ZdosIn(man, center, radius))
            {
                total++;
                int h = zdo.GetPrefab();
                if (!rows.TryGetValue(h, out var row)) rows[h] = row = new Row { Hash = h, Name = PrefabName(h) };
                row.Count++;
                if (zdo.Persistent) row.Persistent++; else { row.NonPersistent++; nonPersistent++; }
                if (zdo.Distant) row.Distant++;
                if (!zdo.HasOwner()) row.Unowned++; else row.Owners.Add(zdo.GetOwner());
                if (clientView)
                {
                    var view = ZNetScene.instance.FindInstance(zdo);
                    if (view == null)
                    {
                        if (zdo.Created) { row.DeadInstance++; dead++; } else row.NoInstance++;
                    }
                }
            }
            var lines = new List<string>();
            string where = radius > 0 ? $"zones {center.x - radius}..{center.x + radius},{center.y - radius}..{center.y + radius}" : $"zone {center.x},{center.y}";
            lines.Add($"{where}: {total} objects, {nonPersistent} non-persistent (never saved){(clientView ? $", {dead} created-but-dead instances (block 'area ready')" : "")}");
            var sorted = new List<Row>(rows.Values);
            sorted.Sort((a, b) => b.Count.CompareTo(a.Count));
            int shown = 0;
            foreach (var r in sorted)
            {
                if (shown++ >= maxRows) { lines.Add($"... {sorted.Count - maxRows} more prefab types"); break; }
                string flags = "";
                if (r.NonPersistent > 0) flags += $" nonpersistent={r.NonPersistent}";
                if (r.Distant > 0) flags += $" distant={r.Distant}";
                if (r.Unowned > 0) flags += $" unowned={r.Unowned}";
                if (r.Owners.Count > 0) flags += $" owners={r.Owners.Count}";
                if (r.DeadInstance > 0) flags += $" DEAD={r.DeadInstance}";
                if (clientView && r.NoInstance > 0) flags += $" notSpawned={r.NoInstance}";
                lines.Add($"  {r.Count,6} x {r.Name} (hash {r.Hash}){flags}");
            }
            return lines;
        }

        /// <summary>Destroys non-persistent ZDOs of one prefab in the zone(s). Persistent objects are never touched.</summary>
        public static List<string> PurgeNonPersistent(ZDOMan man, Vector2s center, int radius, string prefabNameOrHash)
        {
            var lines = new List<string>();
            int hash;
            if (!int.TryParse(prefabNameOrHash, out hash)) hash = prefabNameOrHash.GetStableHashCode();
            var victims = new List<ZDO>();
            int persistentSkipped = 0;
            foreach (var zdo in ZdosIn(man, center, radius))
            {
                if (zdo.GetPrefab() != hash) continue;
                if (zdo.Persistent) { persistentSkipped++; continue; }
                victims.Add(zdo);
            }
            foreach (var zdo in victims) Compilers.Remove(zdo);
            lines.Add($"purged {victims.Count} non-persistent '{PrefabName(hash)}' (hash {hash}) object(s); skipped {persistentSkipped} persistent one(s). They disappear for everyone within a tick.");
            return lines;
        }
    }
}
