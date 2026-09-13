using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using HarmonyLib;
using UnityEngine;

namespace DeepNorthTerrainFix
{
    /// <summary>
    /// In-memory form of a _TerrainCompiler's "TCData" byte array (TerrainComp.Save / TerrainComp.Load format):
    /// gzip( int version(1), int operations, Vector3 lastOpPoint, float lastOpRadius,
    ///       int n, n x [bool modified, (float levelDelta, float smoothDelta)],
    ///       int m, m x [bool modified, (float r, g, b, a)] )
    /// For a 64 m zone n = m = 65 * 65 = 4225. In the Deep North the paint green channel is the snow depth.
    /// </summary>
    internal sealed class TcBlock
    {
        public const int DefaultVertices = 65 * 65;

        public int Version = 1;
        public int Ops;
        public Vector3 LastOp;
        public float LastRadius;
        public bool[] ModH;
        public float[] Lvl;
        public float[] Smo;
        public bool[] ModP;
        public Color[] Paint;

        public int Pitch => (int)Math.Round(Math.Sqrt(ModH.Length));

        public int ModifiedCount
        {
            get
            {
                int c = 0;
                for (int i = 0; i < ModH.Length; i++) if (ModH[i]) c++;
                for (int i = 0; i < ModP.Length; i++) if (ModP[i]) c++;
                return c;
            }
        }

        public static TcBlock Empty(int n = DefaultVertices)
        {
            return new TcBlock
            {
                ModH = new bool[n], Lvl = new float[n], Smo = new float[n],
                ModP = new bool[n], Paint = new Color[n],
            };
        }

        public static TcBlock Parse(byte[] raw)
        {
            if (raw == null || raw.Length == 0) return null;
            var pkg = new ZPackage(Utils.Decompress(raw));
            var b = new TcBlock();
            b.Version = pkg.ReadInt();
            b.Ops = pkg.ReadInt();
            b.LastOp = pkg.ReadVector3();
            b.LastRadius = pkg.ReadSingle();
            int n = pkg.ReadInt();
            if (n < 0 || n > 1 << 20) throw new InvalidDataException("bad height count " + n);
            b.ModH = new bool[n]; b.Lvl = new float[n]; b.Smo = new float[n];
            for (int i = 0; i < n; i++)
            {
                b.ModH[i] = pkg.ReadBool();
                if (b.ModH[i]) { b.Lvl[i] = pkg.ReadSingle(); b.Smo[i] = pkg.ReadSingle(); }
            }
            int m = pkg.ReadInt();
            if (m < 0 || m > 1 << 20) throw new InvalidDataException("bad paint count " + m);
            b.ModP = new bool[m]; b.Paint = new Color[m];
            for (int i = 0; i < m; i++)
            {
                b.ModP[i] = pkg.ReadBool();
                if (b.ModP[i])
                {
                    Color c;
                    c.r = pkg.ReadSingle(); c.g = pkg.ReadSingle(); c.b = pkg.ReadSingle(); c.a = pkg.ReadSingle();
                    b.Paint[i] = c;
                }
            }
            // Pre-resize saves store paint at (pitch-1)^2 with pitch-1 columns per row while heights are pitch^2.
            // Remap exactly as TerrainComp.Load does (TerrainComp.cs:200-222) so every block shares one layout
            // before it is merged, reconciled or written back (the game reads the pitch^2 form unchanged).
            int pitch = (int)Math.Round(Math.Sqrt(n));
            int w = pitch - 1;
            if (pitch * pitch == n && w > 0 && m == w * w)
            {
                bool[] oldMod = b.ModP; Color[] oldPaint = b.Paint;
                b.ModP = new bool[n]; b.Paint = new Color[n];
                for (int k = 0; k < n; k++)
                {
                    int row = k / pitch;
                    int rowNext = (k + 1) / pitch;
                    int src = k - row;
                    if (row == w) src -= w;
                    if (k > 0 && (k - row) % w == 0 && (k + 1 - rowNext) % w == 0) src--;
                    if (src < 0 || src >= oldMod.Length) continue;
                    b.ModP[k] = oldMod[src];
                    b.Paint[k] = oldPaint[src];
                }
            }
            return b;
        }

        public byte[] Build()
        {
            var pkg = new ZPackage();
            pkg.Write(Version);
            pkg.Write(Ops);
            pkg.Write(LastOp);
            pkg.Write(LastRadius);
            pkg.Write(ModH.Length);
            for (int i = 0; i < ModH.Length; i++)
            {
                pkg.Write(ModH[i]);
                if (ModH[i]) { pkg.Write(Lvl[i]); pkg.Write(Smo[i]); }
            }
            pkg.Write(ModP.Length);
            for (int i = 0; i < ModP.Length; i++)
            {
                pkg.Write(ModP[i]);
                if (ModP[i]) { pkg.Write(Paint[i].r); pkg.Write(Paint[i].g); pkg.Write(Paint[i].b); pkg.Write(Paint[i].a); }
            }
            return Utils.Compress(pkg.GetArray());
        }

        /// <summary>Union: keep this block's values, fill in vertices only <paramref name="other"/> modified. Returns vertices copied.</summary>
        public int MergeFrom(TcBlock other)
        {
            if (other == null) return 0;
            if (other.ModH.Length != ModH.Length || other.ModP.Length != ModP.Length)
            {
                Plugin.Log.LogWarning($"TCData layout mismatch ({ModH.Length}/{ModP.Length} vs {other.ModH.Length}/{other.ModP.Length}); not merging");
                return 0;
            }
            int merged = 0;
            int n = ModH.Length;
            for (int i = 0; i < n; i++)
            {
                if (other.ModH[i] && !ModH[i]) { ModH[i] = true; Lvl[i] = other.Lvl[i]; Smo[i] = other.Smo[i]; merged++; }
            }
            int m = ModP.Length;
            for (int i = 0; i < m; i++)
            {
                if (other.ModP[i] && !ModP[i]) { ModP[i] = true; Paint[i] = other.Paint[i]; merged++; }
            }
            if (merged > 0) Ops = Math.Max(Ops, other.Ops) + 1;
            return merged;
        }
    }

    /// <summary>Helpers for finding and editing _TerrainCompiler ZDOs directly in ZDOMan.</summary>
    internal static class Compilers
    {
        public static readonly int PrefabHash = "_TerrainCompiler".GetStableHashCode();

        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZDOID, ZDO>> f_objectsByID =
            AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID");
        private static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>[]> f_objectsBySector =
            AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");

        public static long ZoneKey(Vector2s z) => ((long)z.x << 32) | (uint)(int)z.y;
        public static Vector2s ZoneFromKey(long key) => new Vector2s((int)(key >> 32), (int)(uint)(key & 0xFFFFFFFF));

        /// <summary>All compiler ZDOs in the world grouped by zone. Iterates every ZDO once.</summary>
        public static Dictionary<long, List<ZDO>> AllByZone(ZDOMan man)
        {
            var result = new Dictionary<long, List<ZDO>>();
            var dict = f_objectsByID(man);
            if (dict == null) return result;
            foreach (var zdo in dict.Values)
            {
                if (zdo.GetPrefab() != PrefabHash) continue;
                long key = ZoneKey(zdo.GetSector());
                if (!result.TryGetValue(key, out var list)) result[key] = list = new List<ZDO>(1);
                list.Add(zdo);
            }
            return result;
        }

        /// <summary>Compiler ZDOs in one zone, from the sector list (cheap).</summary>
        public static List<ZDO> InZone(ZDOMan man, Vector2s zone)
        {
            var result = new List<ZDO>(1);
            var arr = f_objectsBySector(man);
            if (arr == null) return result;
            var idx = ZoneSystem.SectorToIndex(zone);
            if (idx.Sector >= arr.Length) return result;
            var list = arr[idx.Sector];
            if (list == null) return result;
            foreach (var zdo in list)
            {
                if (zdo.GetPrefab() == PrefabHash) result.Add(zdo);
            }
            return result;
        }

        /// <summary>Score used to decide which duplicate survives: operations count, then payload size.</summary>
        public static long Richness(ZDO zdo)
        {
            if (zdo == null) return -1;
            byte[] raw = zdo.GetByteArray(ZDOVars.s_TCData, null);
            if (raw == null || raw.Length == 0) return 0;
            int ops = 0;
            try
            {
                using (var gz = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress))
                {
                    var hdr = new byte[8];
                    int read = 0;
                    while (read < 8)
                    {
                        int n = gz.Read(hdr, read, 8 - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read == 8) ops = BitConverter.ToInt32(hdr, 4);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"TCData header read failed for {zdo.m_uid}: {e.Message}");
            }
            return ((long)Math.Max(ops, 0) << 32) | (uint)raw.Length;
        }

        /// <summary>Deterministic tie-break: lower id wins.</summary>
        public static int CompareIds(ZDO a, ZDO b)
        {
            int c = a.m_uid.UserID.CompareTo(b.m_uid.UserID);
            return c != 0 ? c : a.m_uid.ID.CompareTo(b.m_uid.ID);
        }

        /// <summary>Ranks compilers: richest first, ties by id. Never returns null; entries may be null if the list is empty.</summary>
        public static List<ZDO> Rank(List<ZDO> list)
        {
            var copy = new List<ZDO>(list);
            copy.Sort((a, b) =>
            {
                long ra = Richness(a), rb = Richness(b);
                if (ra != rb) return rb.CompareTo(ra);
                return CompareIds(a, b);
            });
            return copy;
        }

        public static TcBlock Load(ZDO zdo)
        {
            try { return TcBlock.Parse(zdo.GetByteArray(ZDOVars.s_TCData, null)); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"TCData parse failed for {zdo.m_uid}: {e.Message}");
                return null;
            }
        }

        /// <summary>Revision jump applied after a healer write so it always out-ranks a stroke a peer saved in the same round-trip.</summary>
        public const uint RevisionJump = 1000;

        public static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

        /// <summary>True when a connected peer (not us) owns the ZDO.</summary>
        public static bool OwnedByConnectedPeer(ZDO zdo)
        {
            long owner = zdo.GetOwner();
            return owner != 0L && owner != ZDOMan.GetSessionID() && ZNet.instance != null && ZNet.instance.GetPeer(owner) != null;
        }

        public static void EnsureOwned(ZDO zdo)
        {
            if (!zdo.IsOwner()) zdo.SetOwner(ZDOMan.GetSessionID());
        }

        /// <summary>
        /// Writes a block into the ZDO. Propagation needs only the DataRevision bump (ZDO.Set has no owner check and
        /// peers accept on DataRevision), so on the server ownership is left with the terraforming peer: stealing it
        /// would route that player's next strokes to a server that has no terrain instance and drop them for ~2 s.
        /// A client must own the ZDO for its change to be queued to the server. The revision is then jumped far ahead
        /// so a stroke the owner saved in the same round-trip (which would land on the same +1) cannot cancel the write.
        /// </summary>
        public static void Store(ZDO zdo, TcBlock block)
        {
            bool peerOwned = OwnedByConnectedPeer(zdo);
            if (!IsServer) EnsureOwned(zdo);
            else if (!peerOwned) EnsureOwned(zdo);
            zdo.Set(ZDOVars.s_TCData, block.Build());
            zdo.DataRevision += RevisionJump;
            if (peerOwned) Plugin.Log.LogInfo($"{zdo.m_uid}: terrain block written while owned by connected peer {zdo.GetOwner()}; one in-flight stroke may be discarded.");
        }

        /// <summary>
        /// Removes a compiler ZDO. ZDOMan.DestroyZDO only checks the local owner flag. On the server the ZDO is gone in
        /// the same Update, so the flag is set without an OwnerRevision bump; a client needs a real claim so the server
        /// accepts the destroy.
        /// </summary>
        public static void Remove(ZDO zdo)
        {
            if (!zdo.IsOwner())
            {
                if (IsServer) zdo.SetOwnerInternal(ZDOMan.GetSessionID());
                else zdo.SetOwner(ZDOMan.GetSessionID());
            }
            ZDOMan.instance.DestroyZDO(zdo);
        }

        /// <summary>Raw access to the per-sector ZDO lists (for sliced sweeps).</summary>
        public static List<ZDO>[] SectorLists(ZDOMan man) => f_objectsBySector(man);

        public static string Describe(ZDO zdo)
        {
            if (zdo == null) return "(none)";
            long r = Richness(zdo);
            return $"{zdo.m_uid} ops={r >> 32} bytes={(uint)r} owner={zdo.GetOwner()}";
        }
    }
}
