using System;
using HarmonyLib;
using UnityEngine;

namespace SFClientRecon
{
    // Diagnostic: "a kill shows ~0.3 s late". Logs when our killing blow on
    // another player goes out (HealthHandler.TakeDamage on their copy brings it
    // to 0) and when their death plays on our screen (HealthHandler.Die on that
    // copy), with the gap in ms. A few lines per kill.
    public partial class Plugin
    {
        private static readonly System.Diagnostics.Stopwatch _killClock = System.Diagnostics.Stopwatch.StartNew();
        private static readonly System.Collections.Generic.Dictionary<int, long> _killSentAt = new System.Collections.Generic.Dictionary<int, long>();
        private static System.Reflection.FieldInfo _hhHealthF, _hhCtrlF, _ctrlHasCtrlF2;
        private static int _killLogN;

        private void InstallKillTiming()
        {
            try
            {
                var hh = CachedType("HealthHandler");
                if ((object)hh == null) return;
                _hhHealthF = AccessTools.Field(hh, "health");
                _hhCtrlF = AccessTools.Field(hh, "controller");
                var ctl = CachedType("Controller");
                if ((object)ctl != null) _ctrlHasCtrlF2 = AccessTools.Field(ctl, "mHasControl");
                var h = new Harmony(PluginGuid + ".kill-timing");
                foreach (var m in hh.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    var ps = m.GetParameters();
                    if (m.Name == "TakeDamage" && ps.Length >= 2 && ps[1].ParameterType == ctl)
                        h.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(KillTiming_TakeDamagePostfix))));
                }
                var die = AccessTools.Method(hh, "Die");
                if ((object)die != null) h.Patch(die, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(KillTiming_DiePrefix))));
            }
            catch (Exception e) { Log.LogWarning("[kill] timing diag: " + e.Message); }
        }

        private static bool IsOurPlayer(object hh)
        {
            try { var c = _hhCtrlF.GetValue(hh); return c != null && (bool)_ctrlHasCtrlF2.GetValue(c); } catch { return true; }
        }

        internal static void KillTiming_TakeDamagePostfix(object __instance)
        {
            try
            {
                if (IsOurPlayer(__instance)) return;
                var comp = __instance as Component;
                if (comp == null || (float)_hhHealthF.GetValue(__instance) > 0f) return;
                int id = comp.GetInstanceID();
                if (!_killSentAt.ContainsKey(id)) _killSentAt[id] = _killClock.ElapsedMilliseconds;
            }
            catch { }
        }

        internal static void KillTiming_DiePrefix(object __instance)
        {
            try
            {
                var comp = __instance as Component;
                if (comp == null || _killLogN > 200) return;
                int id = comp.GetInstanceID();
                long sent;
                if (IsOurPlayer(__instance)) { _killLogN++; Log.LogInfo("[kill] we died."); return; }
                if (_killSentAt.TryGetValue(id, out sent))
                {
                    _killSentAt.Remove(id);
                    _killLogN++;
                    Log.LogInfo("[kill] our kill: their death played " + (_killClock.ElapsedMilliseconds - sent) + " ms after our killing blow went out.");
                }
                else { _killLogN++; Log.LogInfo("[kill] another player died on our screen (not our killing blow)."); }
            }
            catch { }
        }
    }
}
