using System;
using HarmonyLib;
using UnityEngine;

namespace SFClientRecon
{
    // Diagnostic: black holes were reported to pull nothing on the server.
    // Logs each black hole's start and, about once a second while it lives, its
    // size (localScale.x drives the pull), whether it's enabled, and how far our
    // own player is. A handful of lines per black hole.
    public partial class Plugin
    {
        private static float _bhLogAt;

        private void InstallBlackHoleDiag()
        {
            try
            {
                var t = CachedType("BlackHole");
                if ((object)t == null) return;
                var h = new Harmony(PluginGuid + ".blackhole-diag");
                var start = AccessTools.Method(t, "Start");
                var fixedUpd = AccessTools.Method(t, "FixedUpdate");
                var ended = AccessTools.Method(t, "OnMatchEnded");
                if ((object)start != null) h.Patch(start, postfix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(BlackHoleStart_Postfix))));
                if ((object)fixedUpd != null) h.Patch(fixedUpd, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(BlackHoleFixed_Prefix))));
                if ((object)ended != null) h.Patch(ended, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(BlackHoleEnded_Prefix))));
                var mmT = CachedType("MultiplayerManager");
                var onSpawned = (object)mmT != null ? AccessTools.Method(mmT, "OnObjectSpawned") : null;
                var spawnObj = (object)mmT != null ? AccessTools.Method(mmT, "SpawnObject", new[] { typeof(GameObject), typeof(Vector3), typeof(Vector3), typeof(bool) }) : null;
                if ((object)onSpawned != null) h.Patch(onSpawned, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(OnObjectSpawned_DiagPrefix))));
                if ((object)spawnObj != null) h.Patch(spawnObj, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(SpawnObject_DiagPrefix))));
            }
            catch (Exception e) { Log.LogWarning("[blackhole] diag: " + e.Message); }
        }

        internal static void BlackHoleStart_Postfix(object __instance)
        {
            try
            {
                var c = __instance as Component;
                if (c == null) return;
                Log.LogInfo("[blackhole] spawned at " + c.transform.position + " parent " + (c.transform.parent != null ? c.transform.parent.name : "-")
                    + ", local player " + LocalPlayerDistance(c) + " away, timeScale " + Time.timeScale);
            }
            catch (Exception e) { Log.LogInfo("[blackhole] start: " + e.Message); }
        }

        internal static void BlackHoleFixed_Prefix(object __instance)
        {
            try
            {
                if (Time.realtimeSinceStartup < _bhLogAt) return;
                _bhLogAt = Time.realtimeSinceStartup + 1f;
                var c = __instance as Behaviour;
                if (c == null) return;
                Log.LogInfo("[blackhole] size " + c.transform.localScale.x.ToString("0.00") + " enabled " + c.enabled
                    + " at " + (c.transform.parent != null ? c.transform.parent.position : c.transform.position) + ", local player " + LocalPlayerDistance(c) + " away");
            }
            catch { }
        }

        private static int _spawnDiagN;
        internal static void OnObjectSpawned_DiagPrefix(byte[] data)
        {
            if (_spawnDiagN++ > 30 || data == null || data.Length < 2) return;
            Log.LogInfo("[spawn] ObjectSpawned received: object #" + (data[0] | (data[1] << 8)) + " (" + data.Length + " bytes).");
        }
        internal static void SpawnObject_DiagPrefix(GameObject objectToSpawn, Vector3 position)
        {
            if (_spawnDiagN++ > 30) return;
            Log.LogInfo("[spawn] we spawn '" + (objectToSpawn != null ? objectToSpawn.name : "null") + "' at " + position + " (sent to the server).");
        }

        internal static void BlackHoleEnded_Prefix() { Log.LogInfo("[blackhole] OnMatchEnded: the black hole removes itself now."); }

        private static string LocalPlayerDistance(Component bh)
        {
            var ctlT = CachedType("Controller");
            var hasF = (object)ctlT != null ? AccessTools.Field(ctlT, "mHasControl") : null;
            if ((object)hasF == null) return "?";
            Vector3 at = bh.transform.parent != null ? bh.transform.parent.position : bh.transform.position;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(ctlT))
            {
                var ctl = o as Component;
                if (ctl == null || !(bool)hasF.GetValue(o)) continue;
                var rb = ctl.GetComponentInChildren<Rigidbody>();
                Vector3 p = rb != null ? rb.position : ctl.transform.position;
                return Vector3.Distance(at, p).ToString("0.0") + (rb != null ? (rb.isKinematic ? " (kinematic)" : "") : " (no rb)");
            }
            return "none";
        }
    }
}
