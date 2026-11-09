using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SFClientRecon
{
    // Places where the patched Assembly-CSharp (which every client and the
    // server run) differs from stock gameplay, put back. Found by diffing the
    // decompiled stock and patched assemblies (all numeric constants compared).
    //  - KIllAllOutOfRange.GO (the Halloween "Inverse" potion): stock kills every
    //    non-flying player whose torso is more than 2.2 from it; the patched
    //    build computes the distance and throws it away, so the potion did
    //    nothing. Here each client applies it to its OWN player (on this server
    //    every client decides its own player's death).
    public partial class Plugin
    {
        private static MethodInfo _hhTakeDamageM;
        private static Type _hhT, _torsoT, _ctrlStockT;
        private static FieldInfo _canFlyF;

        private void InstallStockPatchedDllFixes()
        {
            try
            {
                var t = CachedType("KIllAllOutOfRange");
                var go = RefOk(t) ? AccessTools.Method(t, "GO") : null;
                if (!RefOk(go)) { Log.LogWarning("[stock] KIllAllOutOfRange.GO not found."); return; }
                new Harmony(PluginGuid + ".stock-fixes").Patch(go, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(KillAllOutOfRange_GO_Prefix))));
                Log.LogInfo("[stock] KIllAllOutOfRange.GO restored (Inverse potion kills players outside it, as in stock).");
            }
            catch (Exception e) { Log.LogWarning($"[stock] fixes: {e.Message}"); }
        }

        internal static bool KillAllOutOfRange_GO_Prefix(object __instance)
        {
            try
            {
                var me = __instance as Component;
                if (me == null) return false;
                if (!RefOk(_hhT))
                {
                    _hhT = CachedType("HealthHandler"); _torsoT = CachedType("Torso"); _ctrlStockT = CachedType("Controller");
                    _canFlyF = AccessTools.Field(_ctrlStockT, "canFly");
                    foreach (var m in _hhT.GetMethods(BindingFlags.Instance | BindingFlags.Public))
                    {
                        var ps = m.GetParameters();
                        if (m.Name == "TakeDamage" && ps.Length >= 2 && ps[0].ParameterType == typeof(float) && ps[1].ParameterType == _ctrlStockT) { _hhTakeDamageM = m; break; }
                    }
                }
                if (!RefOk(_hhTakeDamageM)) return false;
                var ctlHasF = AccessTools.Field(_ctrlStockT, "mHasControl");
                foreach (var o in UnityEngine.Object.FindObjectsOfType(_hhT))
                {
                    var hh = o as Component;
                    if (hh == null) continue;
                    var ctl = hh.GetComponent(_ctrlStockT);
                    if (ctl == null || !(bool)ctlHasF.GetValue(ctl)) continue;            // own player only
                    if (RefOk(_canFlyF) && (bool)_canFlyF.GetValue(ctl)) continue;
                    var torso = hh.GetComponentInChildren(_torsoT) as Component;
                    if (torso == null || Vector3.Distance(me.transform.position, torso.transform.position) <= 2.2f) continue;
                    var ps = _hhTakeDamageM.GetParameters();
                    var args = new object[ps.Length];
                    args[0] = 1000f; args[1] = null;
                    for (int i = 2; i < ps.Length; i++) args[i] = ps[i].DefaultValue;
                    _hhTakeDamageM.Invoke(hh, args);
                    Log.LogInfo("[stock] Inverse potion: own player outside the bubble, killed (stock).");
                }
            }
            catch (Exception e) { Log.LogWarning($"[stock] KIllAllOutOfRange: {(e.InnerException ?? e).Message}"); }
            return false;
        }
    }
}
