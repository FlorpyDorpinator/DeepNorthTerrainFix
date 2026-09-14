using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace DeepNorthTerrainFix
{
    /// <summary>
    /// DeepNorthTerrainFix
    ///  Heals:   duplicate _TerrainCompiler ZDOs inside the loaded world (on load, on arrival, periodically, on command).
    ///  Protects: the server refuses a client's destroy of the richest compiler of a zone and never forwards a
    ///            duplicate it is about to remove, so an unmodded player can no longer delete real terraforming.
    ///  Prevents: new duplicates being created, endless create/destroy loops, portal hangs, double-applied border
    ///            strokes, snow-buildup network spam (client side).
    /// Install on the server (healing + protection) and optionally on clients (prevention where terrain objects are
    /// actually instantiated). One DLL for both.
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "FlorpyDorp.DeepNorthTerrainFix";
        public const string NAME = "DeepNorthTerrainFix";
        public const string VERSION = "1.2.1";
        public const string AUTHOR = "FlorpyDorp";

        internal static ManualLogSource Log;

        // Heal
        internal static ConfigEntry<bool> HealOnWorldLoad;
        internal static ConfigEntry<bool> HealOnArrival;
        internal static ConfigEntry<bool> HealPeriodically;
        internal static ConfigEntry<float> HealIntervalSeconds;
        internal static ConfigEntry<bool> MergeDuplicateData;
        internal static ConfigEntry<bool> ClientMayHeal;
        internal static ConfigEntry<bool> ProtectRichestCompiler;

        // Terrain
        internal static ConfigEntry<bool> FixDuplicateCompilers;
        internal static ConfigEntry<bool> MergeOnDedupe;
        internal static ConfigEntry<bool> ReuseExistingCompilerZdo;
        internal static ConfigEntry<bool> SkipUnownedNeighborSpread;
        internal static ConfigEntry<bool> FixDoubleAppliedBorderStrokes;
        internal static ConfigEntry<bool> CleanupNetworkedTerrainOps;
        internal static ConfigEntry<bool> PurgeGhostTerrainOps;

        // Teleport
        internal static ConfigEntry<bool> TeleportHardTimeout;
        internal static ConfigEntry<float> TeleportHardTimeoutSeconds;

        // Network
        internal static ConfigEntry<bool> PatchNetworkBudget;
        internal static ConfigEntry<int> SteamSendRateBytesPerSec;
        internal static ConfigEntry<int> ZdoInFlightBudgetBytes;

        // Snow
        internal static ConfigEntry<bool> ThrottleSnowWrites;
        internal static ConfigEntry<float> SnowWriteStep;

        private Harmony _harmony;
        private float _healTimer;
        private static readonly HashSet<long> s_pendingZoneKeys = new HashSet<long>();
        private static readonly Queue<long> s_pendingZones = new Queue<long>();
        private static readonly HashSet<ZDOID> s_knownCompilers = new HashSet<ZDOID>();

        private void Awake()
        {
            Log = Logger;

            HealOnWorldLoad = Config.Bind("Heal", "HealOnWorldLoad", true,
                "Server / host: right after the world is loaded, merge and remove duplicate terrain compilers in every zone. The result is saved with the next world save.");
            HealOnArrival = Config.Bind("Heal", "HealOnArrival", true,
                "When a terrain compiler ZDO arrives over the network for a zone that already has one, merge it into the existing one and remove it. On the server this happens the instant it arrives, before it can be forwarded to any other player.");
            HealPeriodically = Config.Bind("Heal", "HealPeriodically", true,
                "Server / host: scan the whole world for duplicate compilers every HealIntervalSeconds.");
            HealIntervalSeconds = Config.Bind("Heal", "HealIntervalSeconds", 120f,
                new ConfigDescription("Seconds between periodic scans.", new AcceptableValueRange<float>(15f, 3600f)));
            MergeDuplicateData = Config.Bind("Heal", "MergeDuplicateData", true,
                "When removing a duplicate, copy any terrain vertices it modified that the survivor did not into the survivor.");
            ClientMayHeal = Config.Bind("Heal", "ClientMayHeal", true,
                "Allow a non-host client to run the arrival heal too (it claims ownership of the duplicate to remove it). Harmless if the server also runs this mod; useful if it does not.");
            ProtectRichestCompiler = Config.Bind("Heal", "ProtectRichestCompiler", true,
                "Server: refuse a client's request to destroy the terrain compiler that holds a zone's terraforming when no other compiler in that zone has as much data. An unmodded game sends exactly that request when a duplicate compiler appears on its screen while it owns the real one; vanilla then deletes every hoe and shovel stroke in the zone. The compiler is kept and re-sent to everyone instead. Keep this on.");

            FixDuplicateCompilers = Config.Bind("Terrain", "FixDuplicateCompilers", true,
                "Client: when two terrain compiler objects exist for one zone, keep the one with the most terrain data and remove the other (claiming ownership so the removal propagates). Vanilla removes whichever was created first, which destroys real terraforming and loops create/destroy on non-owning clients.");
            MergeOnDedupe = Config.Bind("Terrain", "MergeOnDedupe", true,
                "Client: before removing a duplicate compiler object, copy its modified vertices into the survivor.");
            ReuseExistingCompilerZdo = Config.Bind("Terrain", "ReuseExistingCompilerZdo", true,
                "Client: before spawning a brand-new terrain compiler for a zone, look for the zone's existing compiler ZDO that just has not been instantiated yet and use that. This is where duplicates are born.");
            SkipUnownedNeighborSpread = Config.Bind("Terrain", "SkipUnownedNeighborSpread", true,
                "Client: do not write edge-spread paint into a neighbouring zone's compiler unless this client owns it (vanilla writes it locally and silently fails to save it, so the seam differs per player until it snaps back).");
            FixDoubleAppliedBorderStrokes = Config.Bind("Terrain", "FixDoubleAppliedBorderStrokes", true,
                "Client: a hoe/shovel stroke that overlaps two zones is applied to both zones AND copied across the border, so the shared border column gets the stroke twice (a ridge/trench along the zone line in Deep North snow). Skip the copy when the neighbour zone receives the stroke itself.");

            CleanupNetworkedTerrainOps = Config.Bind("Terrain", "CleanupNetworkedTerrainOps", true,
                "Client: if a terrain-op prefab (hoe/shovel stroke object) carries a network view, destroy it through the scene so it does not leave a dead, non-persistent ZDO behind. Such ZDOs block 'area ready' for every arriving player until their owner leaves the zone, and never appear in saves.");

            PurgeGhostTerrainOps = Config.Bind("Heal", "PurgeGhostTerrainOps", true,
                "Server / host (and clients with ClientMayHeal): remove any ZDO whose prefab is a self-destructing terrain op (hoe/shovel stroke object). Such ZDOs are always garbage: the stroke was already applied, and every client that spawns the ZDO re-applies it and is left with a dead instance that blocks 'area ready'. Removed on world load, the moment one arrives over the network, and during the periodic sweep.");

            TeleportHardTimeout = Config.Bind("Teleport", "HardTimeout", true,
                "Client: finish a teleport after HardTimeoutSeconds even if the destination never reports 'ready'. Vanilla waits forever if any object around the target cannot be instantiated.");
            TeleportHardTimeoutSeconds = Config.Bind("Teleport", "HardTimeoutSeconds", 25f,
                new ConfigDescription("Seconds after which a teleport is forced to complete.", new AcceptableValueRange<float>(10f, 120f)));

            PatchNetworkBudget = Config.Bind("Network", "PatchNetworkBudget", false,
                "Raise the Steam socket send-rate cap and the per-peer ZDO in-flight budget. Off by default since 1.2.0: on a server it pushes more data at every player, and a player with a weak upload or a congested link then sees delayed hits and rubber-banding. Only enable it if every player has plenty of bandwidth.");
            SteamSendRateBytesPerSec = Config.Bind("Network", "SteamSendRateBytesPerSec", 512000,
                new ConfigDescription("Steam networking SendRateMin/Max (vanilla 153600 = 150 KB/s).", new AcceptableValueRange<int>(153600, 2000000)));
            ZdoInFlightBudgetBytes = Config.Bind("Network", "ZdoInFlightBudgetBytes", 30720,
                new ConfigDescription("ZDOMan.SendZDOs in-flight byte budget per peer (vanilla 10240). One Deep North terrain block can be 10-15 KB.", new AcceptableValueRange<int>(10240, 262144)));

            ThrottleSnowWrites = Config.Bind("Snow", "ThrottleSnowWrites", true,
                "Only sync a building piece's snow-buildup value when it changed by at least SnowWriteStep. Vanilla syncs every piece about once per second while snow accumulates.");
            SnowWriteStep = Config.Bind("Snow", "SnowWriteStep", 0.05f,
                new ConfigDescription("Minimum change in snow buildup (0..1) before it is synced. Visuals only change at 0.25.", new AcceptableValueRange<float>(0.01f, 0.25f)));

            ConfigVersion = Config.Bind("Internal", "ConfigVersion", 0,
                "Used to migrate settings when the mod updates. Do not edit.");
            MigrateConfig();

            _harmony = new Harmony(GUID);
            _harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"{NAME} {VERSION} loaded ({_harmony.GetPatchedMethods().CountItems()} methods patched).");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        // ---------------------------------------------------------------- config migration

        internal static ConfigEntry<int> ConfigVersion;

        /// <summary>Bump when an existing config value must change meaning or default for everyone.</summary>
        private const int CurrentConfigVersion = 2;

        /// <summary>
        /// BepInEx keeps whatever is already in the .cfg, so a changed default never reaches an existing install.
        /// This runs once per config-version step and rewrites the entries that must change, then records the version
        /// so a value the admin sets afterwards is respected.
        /// </summary>
        private void MigrateConfig()
        {
            int have = ConfigVersion.Value;
            if (have >= CurrentConfigVersion) return;
            bool changed = false;
            if (have < 2 && PatchNetworkBudget.Value)
            {
                // 1.1.x default was true. It raises the send rate for every player and can delay hits / rubber-band
                // anyone with a weak upload, so 1.2.0 turned it off by default. Reset it once; set it back if you want it.
                PatchNetworkBudget.Value = false;
                changed = true;
                Log.LogWarning("Config migrated from a pre-1.2.0 file: Network.PatchNetworkBudget reset to false (it can delay hits for players with weak uploads). Set it to true in the config file if you really want it; this reset only happens once.");
            }
            ConfigVersion.Value = CurrentConfigVersion;
            Config.Save();
            if (!changed) Log.LogInfo($"Config version set to {CurrentConfigVersion}.");
        }

        // ---------------------------------------------------------------- healing entry points

        private static bool IsAuthority()
        {
            return ZNet.instance != null && ZNet.instance.IsServer();
        }

        /// <summary>Called for every new ZDOMan (server and client): loaded ZDO ids restart, so drop id-keyed state.</summary>
        internal static void OnSessionStart()
        {
            s_knownCompilers.Clear();
            s_pendingZoneKeys.Clear();
            s_pendingZones.Clear();
            s_sweepCursor = -1;
            Compilers.ResetSession();
            ZDOMan_RPC_DestroyZDO_Patch.ResetSession();
        }

        internal static void OnWorldLoaded()
        {
            OnSessionStart();
            if (ZDOMan.instance == null) return;
            if (HealOnWorldLoad.Value)
            {
                try
                {
                    var r = SaveHealer.DedupeAll(ZDOMan.instance, MergeDuplicateData.Value);
                    foreach (var line in r.Lines) Log.LogWarning("[heal on load] " + line);
                    Log.LogInfo(r.Summary("[heal on load]"));
                    if (r.Removed > 0) Log.LogWarning("Duplicate terrain compilers were removed; the fix is persisted with the next world save.");
                }
                catch (Exception e)
                {
                    Log.LogError("Heal on load failed: " + e);
                }
            }
            if (PurgeGhostTerrainOps.Value)
            {
                try
                {
                    int n = GhostOps.PurgeAll(ZDOMan.instance);
                    if (n > 0) Log.LogWarning($"[heal on load] removed {n} ghost terrain-op ZDO(s) from the save");
                }
                catch (Exception e)
                {
                    Log.LogError("Ghost terrain-op purge on load failed: " + e);
                }
            }
        }

        /// <summary>
        /// A compiler ZDO was deserialized from the network. The first time we see each one, check its zone.
        /// On the server this must happen right here, synchronously: ZDOMan.Update forwards fresh ZDOs to every
        /// other peer before it runs its destroy list, so a duplicate handled one frame later has already reached
        /// every player (and an unmodded owner of the real compiler reacts to it by destroying the real one).
        /// Clients queue the check and run it from Update, because their removal needs an ownership claim anyway.
        /// </summary>
        internal static void OnCompilerArrived(ZDO compiler)
        {
            if (!HealOnArrival.Value || compiler == null) return;
            if (!s_knownCompilers.Add(compiler.m_uid)) return; // already seen this compiler
            if (IsAuthority())
            {
                if (ZDOMan.instance == null) return;
                try
                {
                    var r = SaveHealer.DedupeZone(ZDOMan.instance, compiler.GetSector(), MergeDuplicateData.Value);
                    foreach (var line in r.Lines) Log.LogWarning("[heal on arrival] " + line);
                }
                catch (Exception e)
                {
                    Log.LogError("Heal on arrival failed: " + e);
                }
                return;
            }
            long key = Compilers.ZoneKey(compiler.GetSector());
            if (s_pendingZoneKeys.Add(key)) s_pendingZones.Enqueue(key);
        }

        private static readonly Queue<ZDO> s_pendingGhostOps = new Queue<ZDO>();

        internal static void EnqueueGhostOp(ZDO zdo)
        {
            if (s_pendingGhostOps.Count < 4096) s_pendingGhostOps.Enqueue(zdo);
        }

        private void Update()
        {
            if (ZDOMan.instance == null || ZNet.instance == null) return;
            bool authority = IsAuthority();

            // Arrival checks queued by clients: a few zones per frame, from the sector lists (cheap).
            if (s_pendingZones.Count > 0 && (authority || ClientMayHeal.Value))
            {
                int budget = 4;
                while (budget-- > 0 && s_pendingZones.Count > 0)
                {
                    long key = s_pendingZones.Dequeue();
                    s_pendingZoneKeys.Remove(key);
                    var zone = Compilers.ZoneFromKey(key);
                    try
                    {
                        var r = SaveHealer.DedupeZone(ZDOMan.instance, zone, MergeDuplicateData.Value);
                        foreach (var line in r.Lines) Log.LogWarning("[heal on arrival] " + line);
                    }
                    catch (Exception e)
                    {
                        Log.LogError("Heal on arrival failed: " + e);
                    }
                }
            }
            else if (s_pendingZones.Count > 0)
            {
                s_pendingZones.Clear();
                s_pendingZoneKeys.Clear();
            }

            // Ghost terrain ops that just arrived: remove them before any client spawns them.
            if (s_pendingGhostOps.Count > 0)
            {
                if (authority || ClientMayHeal.Value)
                {
                    int removed = 0;
                    while (s_pendingGhostOps.Count > 0 && removed < 256)
                    {
                        var zdo = s_pendingGhostOps.Dequeue();
                        if (zdo == null || !zdo.IsValid()) continue;
                        try { Compilers.Remove(zdo); removed++; }
                        catch (Exception e) { Log.LogError("Ghost terrain-op removal failed: " + e); }
                    }
                    if (removed > 0) Log.LogInfo($"[heal on arrival] removed {removed} ghost terrain-op ZDO(s)");
                }
                else s_pendingGhostOps.Clear();
            }

            // Periodic scan on the authority only, sliced over the sector lists so a big world never hitches.
            if (!authority || !HealPeriodically.Value) return;
            if (s_sweepCursor < 0)
            {
                _healTimer += Time.deltaTime;
                if (_healTimer < HealIntervalSeconds.Value) return;
                _healTimer = 0f;
                s_sweepCursor = 0;
                s_sweepFound = 0;
            }
            try
            {
                var sectors = Compilers.SectorLists(ZDOMan.instance);
                if (sectors == null) { s_sweepCursor = -1; return; }
                int end = Math.Min(sectors.Length, s_sweepCursor + SweepSlice);
                for (int i = s_sweepCursor; i < end; i++)
                {
                    var list = sectors[i];
                    if (list == null || list.Count == 0) continue;
                    if (PurgeGhostTerrainOps.Value)
                    {
                        int g = GhostOps.PurgeList(list);
                        if (g > 0) { s_sweepGhosts += g; }
                    }
                    if (list.Count < 2) continue;
                    int compilers = 0;
                    for (int k = 0; k < list.Count; k++) if (list[k].GetPrefab() == Compilers.PrefabHash) compilers++;
                    if (compilers < 2) continue;
                    var r = SaveHealer.DedupeZone(ZDOMan.instance, ZoneSystem.IndexToSector((uint)i), MergeDuplicateData.Value);
                    foreach (var line in r.Lines) Log.LogWarning("[periodic heal] " + line);
                    s_sweepFound += r.ZonesWithDuplicates;
                }
                s_sweepCursor = end;
                if (s_sweepCursor >= sectors.Length)
                {
                    if (s_sweepFound > 0 || s_sweepGhosts > 0) Log.LogInfo($"[periodic heal] sweep done, {s_sweepFound} zone(s) repaired, {s_sweepGhosts} ghost terrain-op ZDO(s) removed");
                    s_sweepCursor = -1;
                    s_sweepGhosts = 0;
                }
            }
            catch (Exception e)
            {
                Log.LogError("Periodic heal failed: " + e);
                s_sweepCursor = -1;
            }
        }

        private const int SweepSlice = 8192;
        private static int s_sweepCursor = -1;
        private static int s_sweepFound;
        private static int s_sweepGhosts;
    }

    internal static class EnumerableExt
    {
        public static int CountItems<T>(this IEnumerable<T> src)
        {
            int n = 0;
            foreach (var _ in src) n++;
            return n;
        }
    }
}
