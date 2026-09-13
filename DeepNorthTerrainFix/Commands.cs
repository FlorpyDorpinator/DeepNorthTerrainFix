using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DeepNorthTerrainFix
{
    /// <summary>
    /// Console commands. Read-only ones run locally. World-changing ones run on the server: typed in the dedicated
    /// server console or by the host of a local game they run directly; typed by a joined client they are forwarded
    /// to the server, which only accepts them from players on its adminlist (vanilla RPC_RemoteCommand check).
    /// </summary>
    [HarmonyPatch(typeof(Terminal), "InitTerminal")]
    internal static class Commands
    {
        private static bool s_registered;

        private static void Postfix()
        {
            if (s_registered) return;
            s_registered = true;

            new Terminal.ConsoleCommand("dntf_scan", "DeepNorthTerrainFix: list zones that have more than one terrain compiler (read-only, local view)",
                delegate (Terminal.ConsoleEventArgs args)
                {
                    if (!Ready(args)) return;
                    foreach (var line in SaveHealer.Report(ZDOMan.instance)) Print(args, line);
                }, isCheat: false, isNetwork: true);

            new Terminal.ConsoleCommand("dntf_zone", "DeepNorthTerrainFix: print your current zone coordinates and its terrain compilers",
                delegate (Terminal.ConsoleEventArgs args)
                {
                    if (!Ready(args)) return;
                    if (Player.m_localPlayer == null) { Print(args, "no local player here (use dntf_scan)"); return; }
                    Vector3 p = Player.m_localPlayer.transform.position;
                    Vector2s z = ZoneSystem.GetZone(p);
                    Print(args, $"position {p.x:0},{p.z:0} -> zone {z.x},{z.y}");
                    foreach (var zdo in Compilers.InZone(ZDOMan.instance, z)) Print(args, "  compiler " + Compilers.Describe(zdo));
                }, isCheat: false, isNetwork: true);

            new Terminal.ConsoleCommand("dntf_fix", "[nomerge] - DeepNorthTerrainFix: merge and remove duplicate terrain compilers in the whole world (runs on the server; admins only when sent from a client)",
                delegate (Terminal.ConsoleEventArgs args)
                {
                    if (!Ready(args) || !Allowed(args)) return;
                    bool merge = !(args.Length > 1 && args[1].Equals("nomerge", StringComparison.OrdinalIgnoreCase));
                    var r = SaveHealer.DedupeAll(ZDOMan.instance, merge);
                    foreach (var line in r.Lines) Print(args, line);
                    Print(args, r.Summary("dntf_fix"));
                    Print(args, "Changes are in memory; they are written on the next world save (type 'save' to force one).");
                }, isCheat: false, isNetwork: true, onlyServer: true, remoteCommand: true);

            new Terminal.ConsoleCommand("dntf_seams", "[heights|paint|both] [zoneX zoneY radius] - DeepNorthTerrainFix: make shared border vertices of adjacent zones agree; whole world unless a centre zone and radius are given (runs on the server)",
                delegate (Terminal.ConsoleEventArgs args)
                {
                    if (!Ready(args) || !Allowed(args)) return;
                    string mode = args.Length > 1 ? args[1].ToLowerInvariant() : "both";
                    bool heights = mode == "heights" || mode == "both";
                    bool paint = mode == "paint" || mode == "both";
                    if (!heights && !paint) { Print(args, "usage: dntf_seams [heights|paint|both] [zoneX zoneY radius]"); return; }
                    Func<Vector2s, bool> filter = null;
                    if (args.Length >= 5)
                    {
                        if (!int.TryParse(args[2], out int cx) || !int.TryParse(args[3], out int cy) || !int.TryParse(args[4], out int radius) || radius < 0)
                        {
                            Print(args, "usage: dntf_seams [heights|paint|both] [zoneX zoneY radius]");
                            return;
                        }
                        filter = z => Math.Abs(z.x - cx) <= radius && Math.Abs(z.y - cy) <= radius;
                    }
                    else if (args.Length == 3)
                    {
                        // Host convenience: radius around the local character.
                        if (!int.TryParse(args[2], out int radius) || radius < 0 || Player.m_localPlayer == null)
                        {
                            Print(args, "usage: dntf_seams [heights|paint|both] [zoneX zoneY radius]   (dntf_zone prints your zone)");
                            return;
                        }
                        Vector2s c = ZoneSystem.GetZone(Player.m_localPlayer.transform.position);
                        filter = z => Math.Abs(z.x - c.x) <= radius && Math.Abs(z.y - c.y) <= radius;
                    }
                    else if (args.Length != 1 && args.Length != 2)
                    {
                        Print(args, "usage: dntf_seams [heights|paint|both] [zoneX zoneY radius]");
                        return;
                    }
                    var r = SaveHealer.ReconcileSeams(ZDOMan.instance, filter, heights, paint);
                    foreach (var line in r.Lines) Print(args, line);
                    Print(args, r.Summary("dntf_seams"));
                }, isCheat: false, isNetwork: true, onlyServer: true, remoteCommand: true);

            new Terminal.ConsoleCommand("dntf_reset", "<zoneX> <zoneY> - DeepNorthTerrainFix: wipe all terrain modifications of one zone (buildings are kept; runs on the server)",
                delegate (Terminal.ConsoleEventArgs args)
                {
                    if (!Ready(args) || !Allowed(args)) return;
                    if (args.Length < 3 || !int.TryParse(args[1], out int zx) || !int.TryParse(args[2], out int zy))
                    {
                        Print(args, "usage: dntf_reset <zoneX> <zoneY>   (dntf_zone prints your current zone)");
                        return;
                    }
                    int n = SaveHealer.ResetZone(ZDOMan.instance, new Vector2s(zx, zy));
                    Print(args, n > 0 ? $"reset {n} compiler(s) in zone {zx},{zy}; terrain there returns to world generation" : $"zone {zx},{zy} has no terrain compiler");
                }, isCheat: false, isNetwork: true, onlyServer: true, remoteCommand: true);

            new Terminal.ConsoleCommand("dntf_zdos", "<zoneX> <zoneY> [radius] - DeepNorthTerrainFix: list every object type in a zone as the running game sees it (includes non-persistent objects that never reach the save)",
                delegate (Terminal.ConsoleEventArgs args)
                {
                    if (!Ready(args)) return;
                    if (args.Length < 3 || !int.TryParse(args[1], out int zx) || !int.TryParse(args[2], out int zy))
                    {
                        Print(args, "usage: dntf_zdos <zoneX> <zoneY> [radius]   (dntf_zone prints your current zone)");
                        return;
                    }
                    int radius = args.Length > 3 && int.TryParse(args[3], out int r) ? Math.Max(0, r) : 0;
                    foreach (var line in Diagnostics.ZoneCensus(ZDOMan.instance, new Vector2s(zx, zy), radius)) Print(args, line);
                }, isCheat: false, isNetwork: true);

            new Terminal.ConsoleCommand("dntf_purge", "<zoneX> <zoneY> <prefabName|hash> [radius] - DeepNorthTerrainFix: remove NON-persistent objects of one prefab from a zone (never touches buildings or anything saved; runs on the server)",
                delegate (Terminal.ConsoleEventArgs args)
                {
                    if (!Ready(args) || !Allowed(args)) return;
                    if (args.Length < 4 || !int.TryParse(args[1], out int zx) || !int.TryParse(args[2], out int zy))
                    {
                        Print(args, "usage: dntf_purge <zoneX> <zoneY> <prefabName|hash> [radius]");
                        return;
                    }
                    int radius = args.Length > 4 && int.TryParse(args[4], out int r) ? Math.Max(0, r) : 0;
                    foreach (var line in Diagnostics.PurgeNonPersistent(ZDOMan.instance, new Vector2s(zx, zy), radius, args[3])) Print(args, line);
                }, isCheat: false, isNetwork: true, onlyServer: true, remoteCommand: true);

            Plugin.Log.LogInfo("Console commands registered: dntf_scan, dntf_zone, dntf_zdos, dntf_fix, dntf_seams, dntf_reset, dntf_purge");
        }

        private static bool Ready(Terminal.ConsoleEventArgs args)
        {
            if (ZDOMan.instance == null || ZNet.instance == null)
            {
                Print(args, "world not loaded");
                return false;
            }
            return true;
        }

        /// <summary>Defense in depth: write commands only ever execute where the world is authoritative.</summary>
        private static bool Allowed(Terminal.ConsoleEventArgs args)
        {
            bool ok = ZNet.instance.IsServer();
            if (!ok) Print(args, "this command runs on the server; joined clients must be on the server's adminlist");
            return ok;
        }

        private static void Print(Terminal.ConsoleEventArgs args, string line)
        {
            ZLog.Log("[DNTF] " + line);
            if (args != null && args.Context != null) args.Context.AddString(line);
        }
    }
}
