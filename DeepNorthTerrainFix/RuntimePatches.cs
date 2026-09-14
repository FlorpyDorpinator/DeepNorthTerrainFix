using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace DeepNorthTerrainFix
{
    // ------------------------------------------------------------------------------------------------
    // World load / ZDO arrival hooks feeding the healer
    // ------------------------------------------------------------------------------------------------
    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.LoadChunks))]
    internal static class ZDOMan_LoadChunks_Patch
    {
        private static void Postfix() => Plugin.OnWorldLoaded();
    }

    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.Load))]
    internal static class ZDOMan_Load_Patch
    {
        private static void Postfix() => Plugin.OnWorldLoaded();
    }

    /// <summary>
    /// A new ZDOMan is a new session on server and client, and ZDOID.Reset() there makes loaded ids (1:1..1:n)
    /// reusable across sessions. Drop every piece of per-session healer state keyed by ZDOID, and drop pending-destroy
    /// entries as soon as the routed destroy is actually handled so they never outlive the ZDO they refer to.
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), MethodType.Constructor, new[] { typeof(int) })]
    internal static class ZDOMan_Ctor_Patch
    {
        private static void Postfix(ZDOMan __instance)
        {
            TerrainComp_Awake_Patch.ResetSession();
            GhostOps.ResetCache();
            Plugin.OnSessionStart();
            __instance.m_onZDODestroyed += TerrainComp_Awake_Patch.OnZDODestroyed;
        }
    }

    /// <summary>Whenever a compiler ZDO arrives over the network, check its zone for duplicates (synchronously on the
    /// server, queued on clients); whenever a ghost terrain-op ZDO arrives, queue it for removal.</summary>
    [HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize))]
    internal static class ZDO_Deserialize_Patch
    {
        private static void Postfix(ZDO __instance)
        {
            int prefab = __instance.GetPrefab();
            if (prefab == Compilers.PrefabHash) Plugin.OnCompilerArrived(__instance);
            else if (Plugin.PurgeGhostTerrainOps.Value && GhostOps.IsTerrainOpPrefab(prefab)) Plugin.EnqueueGhostOp(__instance);
        }
    }

    /// <summary>
    /// Server: a ZDO the healer has already decided to destroy is never forwarded to a peer. ZDOMan.Update sends new
    /// ZDOs to peers before it processes its own destroy list, so without this every removed duplicate still reaches
    /// every other player for one frame, and their (unmodded) game runs the vanilla Awake fight against it.
    /// </summary>
    [HarmonyPatch]
    internal static class ZDOPeer_ShouldSend_Patch
    {
        private static MethodBase TargetMethod()
        {
            var peerType = AccessTools.Inner(typeof(ZDOMan), "ZDOPeer");
            return peerType == null ? null : AccessTools.Method(peerType, "ShouldSend", new[] { typeof(ZDO) });
        }

        private static bool Prefix(ZDO zdo, ref bool __result)
        {
            if (zdo == null || Compilers.SendSuppressed.Count == 0 || !Compilers.SendSuppressed.Contains(zdo.m_uid)) return true;
            __result = false;
            return false;
        }
    }

    /// <summary>
    /// Server: refuse a client's request to destroy the richest terrain compiler of a zone. An unmodded client sends
    /// exactly that when a duplicate compiler wakes up on its screen while it owns the original (TerrainComp.Awake
    /// destroys "the other one", and ZNetScene.Destroy makes it permanent for the owner). The ZDO is kept, its owner
    /// is cleared, and it is re-sent to every peer so the game that dropped it gets it straight back. Destroys of
    /// anything else, and of compilers that have an equal or richer twin in the zone, pass through untouched.
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), "RPC_DestroyZDO")]
    internal static class ZDOMan_RPC_DestroyZDO_Patch
    {
        private static readonly Dictionary<ZDOID, int> s_refused = new Dictionary<ZDOID, int>();

        internal static void ResetSession() => s_refused.Clear();

        private static bool Prefix(ZDOMan __instance, long sender, ref ZPackage pkg)
        {
            if (!Plugin.ProtectRichestCompiler.Value || !Compilers.IsServer || pkg == null) return true;
            if (sender == ZDOMan.GetSessionID()) return true; // our own healer / vanilla destroys
            int start = pkg.GetPos();
            int n;
            try { n = pkg.ReadInt(); }
            catch { pkg.SetPos(start); return true; }
            var keep = new List<ZDOID>(n);
            bool refusedAny = false;
            for (int i = 0; i < n; i++)
            {
                ZDOID id = pkg.ReadZDOID();
                ZDO zdo = __instance.GetZDO(id);
                if (zdo != null && zdo.GetPrefab() == Compilers.PrefabHash && Compilers.IsProtected(__instance, zdo))
                {
                    refusedAny = true;
                    try
                    {
                        zdo.SetOwner(0L);
                        __instance.ForceSendZDO(id);
                    }
                    catch (Exception e) { Plugin.Log.LogError($"Protecting compiler {id} failed: {e}"); }
                    s_refused.TryGetValue(id, out int count);
                    s_refused[id] = ++count;
                    if (count <= 5 || count % 50 == 0)
                    {
                        var zone = zdo.GetSector();
                        Plugin.Log.LogWarning($"[protect] refused destroy of terrain compiler {Compilers.Describe(zdo)} in zone {zone.x},{zone.y} requested by peer {sender} (x{count}); re-sent to all peers");
                    }
                }
                else keep.Add(id);
            }
            if (!refusedAny) { pkg.SetPos(start); return true; }
            var rebuilt = new ZPackage();
            rebuilt.Write(keep.Count);
            foreach (var id in keep) rebuilt.Write(id);
            rebuilt.SetPos(0);
            pkg = rebuilt;
            return true;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // 1. TerrainComp.Awake: resolve duplicate compilers safely (client side, where instances exist)
    // ------------------------------------------------------------------------------------------------
    [HarmonyPatch(typeof(TerrainComp), "Awake")]
    internal static class TerrainComp_Awake_Patch
    {
        internal static readonly AccessTools.FieldRef<TerrainComp, ZNetView> f_nview = AccessTools.FieldRefAccess<TerrainComp, ZNetView>("m_nview");
        internal static readonly AccessTools.FieldRef<TerrainComp, Heightmap> f_hmap = AccessTools.FieldRefAccess<TerrainComp, Heightmap>("m_hmap");
        private static readonly AccessTools.FieldRef<TerrainComp, bool> f_initialized = AccessTools.FieldRefAccess<TerrainComp, bool>("m_initialized");
        private static readonly AccessTools.FieldRef<TerrainComp, int> f_operations = AccessTools.FieldRefAccess<TerrainComp, int>("m_operations");
        private static readonly AccessTools.FieldRef<TerrainComp, bool[]> f_modifiedHeight = AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedHeight");
        private static readonly AccessTools.FieldRef<TerrainComp, float[]> f_levelDelta = AccessTools.FieldRefAccess<TerrainComp, float[]>("m_levelDelta");
        private static readonly AccessTools.FieldRef<TerrainComp, float[]> f_smoothDelta = AccessTools.FieldRefAccess<TerrainComp, float[]>("m_smoothDelta");
        private static readonly AccessTools.FieldRef<TerrainComp, bool[]> f_modifiedPaint = AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedPaint");
        private static readonly AccessTools.FieldRef<TerrainComp, Color[]> f_paintMask = AccessTools.FieldRefAccess<TerrainComp, Color[]>("m_paintMask");
        private static readonly FieldInfo fi_instances = AccessTools.Field(typeof(TerrainComp), "s_instances");
        private static readonly MethodInfo m_Initialize = AccessTools.Method(typeof(TerrainComp), "Initialize");
        private static readonly MethodInfo m_CheckLoad = AccessTools.Method(typeof(TerrainComp), "CheckLoad");
        private static readonly MethodInfo m_Save = AccessTools.Method(typeof(TerrainComp), "Save", new[] { typeof(bool) });
        private static readonly MethodInfo m_Rpc = AccessTools.Method(typeof(TerrainComp), "RPC_ApplyOperation");
        private static readonly AccessTools.FieldRef<TerrainComp, uint> f_lastDataRevision = AccessTools.FieldRefAccess<TerrainComp, uint>("m_lastDataRevision");
        /// <summary>ZDOs we told the network to destroy this session; if the scene re-instantiates one before the destroy lands, drop it silently.</summary>
        private static readonly HashSet<ZDOID> s_pendingDestroy = new HashSet<ZDOID>();

        internal static void ResetSession() => s_pendingDestroy.Clear();

        internal static void OnZDODestroyed(ZDO zdo)
        {
            if (zdo == null) return;
            s_pendingDestroy.Remove(zdo.m_uid);
            Compilers.SendSuppressed.Remove(zdo.m_uid);
        }

        private static bool Prefix(TerrainComp __instance)
        {
            if (!Plugin.FixDuplicateCompilers.Value) return true;
            if (fi_instances == null || m_Initialize == null || m_CheckLoad == null || m_Rpc == null)
            {
                Plugin.Log.LogError("TerrainComp reflection failed; falling back to vanilla Awake.");
                return true;
            }

            var nview = __instance.GetComponent<ZNetView>();
            f_nview(__instance) = nview;
            var hmap = Heightmap.FindHeightmap(__instance.transform.position);
            f_hmap(__instance) = hmap;
            if (hmap == null)
            {
                ZLog.LogWarning("Terrain compiler could not find hmap");
                return false;
            }

            var instances = (List<TerrainComp>)fi_instances.GetValue(null);
            TerrainComp other = TerrainComp.FindTerrainCompiler(__instance.transform.position);
            ZDO myZdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;

            if (myZdo != null && s_pendingDestroy.Contains(myZdo.m_uid))
            {
                RemoveDuplicate(nview, __instance.gameObject);
                return false;
            }

            if (other != null && other != __instance)
            {
                ZNetView otherView = f_nview(other);
                ZDO otherZdo = otherView != null && otherView.IsValid() ? otherView.GetZDO() : null;
                long mine = Compilers.Richness(myZdo);
                long theirs = Compilers.Richness(otherZdo);
                bool keepMine = mine > theirs || (mine == theirs && myZdo != null && otherZdo != null && Compilers.CompareIds(myZdo, otherZdo) < 0);

                Plugin.Log.LogWarning($"Duplicate terrain compiler at {__instance.transform.position}: new={Compilers.Describe(myZdo)} existing={Compilers.Describe(otherZdo)} -> keeping {(keepMine ? "NEW" : "EXISTING")}");

                if (!keepMine)
                {
                    if (Plugin.MergeOnDedupe.Value && myZdo != null && f_initialized(other))
                    {
                        m_Initialize.Invoke(__instance, null);
                        m_CheckLoad.Invoke(__instance, null);
                        MergeInto(other, __instance);
                    }
                    RemoveDuplicate(nview, __instance.gameObject);
                    return false;
                }
            }

            instances.Add(__instance);
            if (nview != null)
            {
                var del = (Action<long, ZPackage>)Delegate.CreateDelegate(typeof(Action<long, ZPackage>), __instance, m_Rpc);
                nview.Register<ZPackage>("RPC_ApplyOperation", del);
            }
            m_Initialize.Invoke(__instance, null);
            m_CheckLoad.Invoke(__instance, null);

            if (other != null && other != __instance)
            {
                if (Plugin.MergeOnDedupe.Value && f_initialized(other)) MergeInto(__instance, other);
                RemoveDuplicate(f_nview(other), other.gameObject);
            }
            return false;
        }

        private static void MergeInto(TerrainComp dest, TerrainComp src)
        {
            try
            {
                var dH = f_modifiedHeight(dest); var sH = f_modifiedHeight(src);
                var dL = f_levelDelta(dest); var sL = f_levelDelta(src);
                var dS = f_smoothDelta(dest); var sS = f_smoothDelta(src);
                var dP = f_modifiedPaint(dest); var sP = f_modifiedPaint(src);
                var dC = f_paintMask(dest); var sC = f_paintMask(src);
                if (dH == null || sH == null || dP == null || sP == null) return;
                int merged = 0;
                int n = Math.Min(dH.Length, sH.Length);
                for (int i = 0; i < n; i++)
                    if (sH[i] && !dH[i]) { dH[i] = true; dL[i] = sL[i]; dS[i] = sS[i]; merged++; }
                int m = Math.Min(dP.Length, sP.Length);
                for (int i = 0; i < m; i++)
                    if (sP[i] && !dP[i]) { dP[i] = true; dC[i] = sC[i]; merged++; }
                if (merged > 0)
                {
                    f_operations(dest) = Math.Max(f_operations(dest), f_operations(src)) + 1;
                    var dv = f_nview(dest);
                    if (dv != null && dv.IsValid())
                    {
                        if (!dv.IsOwner()) dv.ClaimOwnership();
                        m_Save?.Invoke(dest, new object[] { false });
                        // Jump the revision so a stroke the previous owner saved in the same round-trip cannot cancel the merge.
                        var z = dv.GetZDO();
                        if (z != null)
                        {
                            z.DataRevision += Compilers.RevisionJump;
                            f_lastDataRevision(dest) = z.DataRevision;
                        }
                        f_hmap(dest)?.Poke(1, false);
                    }
                    Plugin.Log.LogInfo($"Merged {merged} vertices from duplicate compiler into survivor {dv?.GetZDO()?.m_uid}");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"MergeInto failed: {e}");
            }
        }

        private static void RemoveDuplicate(ZNetView view, GameObject go)
        {
            try
            {
                if (view != null && view.IsValid())
                {
                    s_pendingDestroy.Add(view.GetZDO().m_uid);
                    if (!view.IsOwner()) view.ClaimOwnership();
                }
                if (ZNetScene.instance != null) ZNetScene.instance.Destroy(go);
                else UnityEngine.Object.Destroy(go);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"RemoveDuplicate failed: {e}");
            }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // 2. Heightmap.GetAndCreateTerrainCompiler: reuse an existing compiler ZDO for this zone
    // ------------------------------------------------------------------------------------------------
    [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.GetAndCreateTerrainCompiler))]
    internal static class Heightmap_GetAndCreateTerrainCompiler_Patch
    {
        private static readonly MethodInfo m_CreateObject = AccessTools.Method(typeof(ZNetScene), "CreateObject", new[] { typeof(ZDO) });

        private static bool Prefix(Heightmap __instance, ref TerrainComp __result)
        {
            if (!Plugin.ReuseExistingCompilerZdo.Value) return true;
            Vector3 pos = __instance.transform.position;
            TerrainComp existing = TerrainComp.FindTerrainCompiler(pos);
            if (existing != null) { __result = existing; return false; }
            if (ZDOMan.instance == null || ZNetScene.instance == null || m_CreateObject == null) return true;

            Vector2s zone = ZoneSystem.GetZone(pos);
            ZDO best = null;
            long bestScore = -1;
            foreach (ZDO zdo in Compilers.InZone(ZDOMan.instance, zone))
            {
                if (!__instance.IsPointInside(zdo.GetPosition(), 0f)) continue;
                long score = Compilers.Richness(zdo);
                if (score > bestScore) { bestScore = score; best = zdo; }
            }
            if (best == null) return true;

            ZNetView inst = ZNetScene.instance.FindInstance(best);
            GameObject go = inst != null ? inst.gameObject : (GameObject)m_CreateObject.Invoke(ZNetScene.instance, new object[] { best });
            TerrainComp comp = go != null ? go.GetComponent<TerrainComp>() : null;
            if (comp == null) return true;
            // Vanilla's Instantiate path yields a ZDO owned by this session, so the routed RPC_ApplyOperation lands here.
            // A reused ZDO that arrived unowned would route to everybody and be dropped by every peer's owner check.
            // Claim it if nobody owns it; if a peer owns it, let the RPC route to them.
            var cv = go.GetComponent<ZNetView>();
            if (cv != null && cv.IsValid() && !cv.HasOwner()) cv.ClaimOwnership();
            Plugin.Log.LogInfo($"Reused existing terrain compiler ZDO {best.m_uid} for zone {zone.x},{zone.y} instead of creating a duplicate.");
            __result = comp;
            return false;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // 3. TerrainComp.GetNeighbor: no spread into unowned compilers, no double-applied border strokes
    // ------------------------------------------------------------------------------------------------
    [HarmonyPatch(typeof(TerrainComp), "GetNeighbor")]
    internal static class TerrainComp_GetNeighbor_Patch
    {
        private static void Postfix(Vector3 worldPos, float radius, ref TerrainComp __result)
        {
            if (__result == null) return;
            if (Plugin.SkipUnownedNeighborSpread.Value && !__result.IsOwner())
            {
                __result = null;
                return;
            }
            if (Plugin.FixDoubleAppliedBorderStrokes.Value)
            {
                // If the neighbouring heightmap is itself inside the op's radius, TerrainOp.Awake already applies the
                // whole op to it; copying our edge value across as well makes it apply twice (visible as a ridge in
                // additive Deep North snow paint). Only spread when the neighbour would otherwise get nothing.
                var hm = TerrainComp_Awake_Patch.f_hmap(__result);
                if (hm != null && hm.IsPointInside(worldPos, radius + 0.5f)) __result = null;
            }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // 3b. Self-destructing terrain ops that carry a network view leave a "created but dead" ZDO behind:
    //     TerrainOp.Awake ends with UnityEngine.Object.Destroy(gameObject), and ZNetView.OnDestroy neither
    //     unregisters the instance nor destroys the ZDO. That ZDO is non-persistent (never saved), blocks
    //     ZNetScene.IsAreaReady for every arriving player, and is only removed when its owner leaves the area.
    //     Route such objects through ZNetScene.Destroy instead, which resets and destroys the ZDO properly.
    //     Both Awake orders are covered: whichever component wakes second sees the other one present.
    // ------------------------------------------------------------------------------------------------
    internal static class NetworkedTerrainOpCleanup
    {
        private static readonly AccessTools.FieldRef<ZNetView, bool> f_ghost = AccessTools.FieldRefAccess<ZNetView, bool>("m_ghost");

        internal static void TryCleanup(GameObject go, ZNetView view)
        {
            if (!Plugin.CleanupNetworkedTerrainOps.Value) return;
            if (TerrainOp.m_forceDisableTerrainOps) return;            // build-menu placement ghost
            if (view == null || !view.IsValid() || f_ghost(view)) return;
            if (ZNetScene.instance == null) return;
            var zdo = view.GetZDO();
            Plugin.Log.LogWarning($"Terrain op '{go.name}' carries a ZNetView (zdo {zdo.m_uid}, persistent={zdo.Persistent}); destroying it through ZNetScene so no dead ZDO is left behind.");
            ZNetScene.instance.Destroy(go);
        }
    }

    [HarmonyPatch(typeof(TerrainOp), "Awake")]
    internal static class TerrainOp_Awake_Patch
    {
        private static void Postfix(TerrainOp __instance)
        {
            var view = __instance.GetComponent<ZNetView>();
            if (view != null) NetworkedTerrainOpCleanup.TryCleanup(__instance.gameObject, view);
        }
    }

    [HarmonyPatch(typeof(ZNetView), "Awake")]
    internal static class ZNetView_Awake_Patch
    {
        private static void Postfix(ZNetView __instance)
        {
            if (__instance.GetComponent<TerrainOp>() != null) NetworkedTerrainOpCleanup.TryCleanup(__instance.gameObject, __instance);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // 4. Player.UpdateTeleport: hard timeout
    // ------------------------------------------------------------------------------------------------
    [HarmonyPatch(typeof(Player), "UpdateTeleport")]
    internal static class Player_UpdateTeleport_Patch
    {
        private static readonly AccessTools.FieldRef<Player, bool> f_teleporting = AccessTools.FieldRefAccess<Player, bool>("m_teleporting");
        private static readonly AccessTools.FieldRef<Player, float> f_timer = AccessTools.FieldRefAccess<Player, float>("m_teleportTimer");
        private static readonly AccessTools.FieldRef<Player, Vector3> f_target = AccessTools.FieldRefAccess<Player, Vector3>("m_teleportTargetPos");
        private static float s_lastLog;

        private static void Postfix(Player __instance)
        {
            if (!Plugin.TeleportHardTimeout.Value) return;
            if (!f_teleporting(__instance)) return;
            float t = f_timer(__instance);
            if (t < Plugin.TeleportHardTimeoutSeconds.Value)
            {
                if (t > 8f && Time.time - s_lastLog > 2f)
                {
                    s_lastLog = Time.time;
                    Plugin.Log.LogWarning($"Teleport waiting {t:0}s: destination area not ready (ZNetScene.IsAreaReady=false). Will force after {Plugin.TeleportHardTimeoutSeconds.Value:0}s.");
                }
                return;
            }
            Vector3 target = f_target(__instance);
            Vector3 p = target;
            float floor;
            if (ZoneSystem.instance != null && ZoneSystem.instance.FindFloor(target, out floor)) p.y = floor + 0.3f;
            else if (ZoneSystem.instance != null) p.y = ZoneSystem.instance.GetSolidHeight(target) + 0.5f;
            __instance.transform.position = p;
            f_timer(__instance) = 0f;
            f_teleporting(__instance) = false;
            __instance.ResetCloth();
            Plugin.Log.LogWarning($"Teleport forced to complete after {t:0}s at {p}.");
        }
    }

    // ------------------------------------------------------------------------------------------------
    // 5a. ZDOMan.SendZDOs: in-flight budget
    // ------------------------------------------------------------------------------------------------
    [HarmonyPatch(typeof(ZDOMan), "SendZDOs")]
    internal static class ZDOMan_SendZDOs_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int budget = Plugin.PatchNetworkBudget.Value ? Math.Max(10240, Plugin.ZdoInFlightBudgetBytes.Value) : 10240;
            int replaced = 0;
            foreach (var ci in instructions)
            {
                if (ci.opcode == OpCodes.Ldc_I4 && ci.operand is int v && v == 10240) { ci.operand = budget; replaced++; }
                yield return ci;
            }
            Plugin.Log.LogInfo($"ZDOMan.SendZDOs budget constants replaced: {replaced} -> {budget} bytes");
        }
    }

    // ------------------------------------------------------------------------------------------------
    // 5b. ZSteamSocket.RegisterGlobalCallbacks: Steam send rate
    // ------------------------------------------------------------------------------------------------
    [HarmonyPatch(typeof(ZSteamSocket), "RegisterGlobalCallbacks")]
    internal static class ZSteamSocket_RegisterGlobalCallbacks_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int rate = Plugin.PatchNetworkBudget.Value ? Math.Max(153600, Plugin.SteamSendRateBytesPerSec.Value) : 153600;
            int replaced = 0;
            foreach (var ci in instructions)
            {
                if (ci.opcode == OpCodes.Ldc_I4 && ci.operand is int v && v == 153600) { ci.operand = rate; replaced++; }
                yield return ci;
            }
            Plugin.Log.LogInfo($"ZSteamSocket send-rate constants replaced: {replaced} -> {rate} B/s");
        }
    }

    // ------------------------------------------------------------------------------------------------
    // 6. WearNTear.UpdateWear: throttle snow-buildup ZDO writes
    // ------------------------------------------------------------------------------------------------
    [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.UpdateWear))]
    internal static class WearNTear_UpdateWear_Patch
    {
        private static readonly ConditionalWeakTable<WearNTear, StrongBox<float>> s_last = new ConditionalWeakTable<WearNTear, StrongBox<float>>();

        public static void SetSnowThrottled(ZDO zdo, int hash, float value, WearNTear self)
        {
            if (zdo == null) return;
            if (!Plugin.ThrottleSnowWrites.Value) { zdo.Set(hash, value); return; }
            var box = s_last.GetValue(self, _ => new StrongBox<float>(float.NaN));
            // Baseline = the last value the network saw; captured before any local-only write below.
            if (float.IsNaN(box.Value)) box.Value = zdo.GetFloat(hash, 0f);
            float last = box.Value;
            if (value >= 1f || value <= 0f || Mathf.Abs(value - last) >= Plugin.SnowWriteStep.Value)
            {
                zdo.Set(hash, value);   // bumps DataRevision -> synced
                box.Value = value;
            }
            else
            {
                // Local store only: UpdateWear re-reads s_snow from the ZDO at the end of every pass for pieces with a
                // snow mesh, so the accumulated value must be visible locally or it is reset each call and snow never
                // builds up. ZDOExtraData.Set does not bump DataRevision, so nothing is sent.
                ZDOExtraData.Set(zdo.m_uid, hash, value);
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            var fSnow = AccessTools.Field(typeof(ZDOVars), "s_snow");
            var mSet = AccessTools.Method(typeof(ZDO), "Set", new[] { typeof(int), typeof(float) });
            var mThrottled = AccessTools.Method(typeof(WearNTear_UpdateWear_Patch), nameof(SetSnowThrottled));
            int patched = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode != OpCodes.Ldsfld || !Equals(list[i].operand, fSnow)) continue;
                for (int j = i + 1; j < Math.Min(list.Count, i + 6); j++)
                {
                    if ((list[j].opcode == OpCodes.Callvirt || list[j].opcode == OpCodes.Call) && Equals(list[j].operand, mSet))
                    {
                        list[j] = new CodeInstruction(OpCodes.Call, mThrottled).WithLabels(list[j].labels).WithBlocks(list[j].blocks);
                        list.Insert(j, new CodeInstruction(OpCodes.Ldarg_0));
                        patched++;
                        i = j + 1;
                        break;
                    }
                }
            }
            Plugin.Log.LogInfo($"WearNTear.UpdateWear snow writes throttled at {patched} call site(s)");
            return list;
        }
    }
}
