using System;
using HarmonyLib;
using UnityEngine;

namespace SFClientRecon
{
    // Players showing the wrong color (both blue). The patched game's
    // MultiplayerManager.SpawnPlayerDummy takes the SHARED material
    // m_Colors[0], sets it to the dummy's color and gives it to the dummy's
    // outline renderers: spawning player 1's copy (blue) repainted color 0
    // blue for everything that uses it, and player 0, spawned after, read
    // "default color 0" as blue. Stock used m_Colors[i] and never changed it.
    // Fix: remember color 0, give the dummy its own outline material in its
    // own color, put color 0 back.
    public partial class Plugin
    {
        private static System.Reflection.FieldInfo _mmColorsF;
        private static System.Reflection.FieldInfo _scwdStartMatF;

        private void InstallColorFix()
        {
            try
            {
                var mmType = CachedType("MultiplayerManager");
                var spawn = (object)mmType != null ? AccessTools.Method(mmType, "SpawnPlayerDummy") : null;
                _mmColorsF = (object)mmType != null ? AccessTools.Field(mmType, "m_Colors") : null;
                if ((object)spawn == null || (object)_mmColorsF == null) { Log.LogWarning("[colors] SpawnPlayerDummy/m_Colors not found: color fix off."); return; }
                new Harmony(PluginGuid + ".colors").Patch(spawn,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(SpawnPlayerDummy_ColorPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(SpawnPlayerDummy_ColorPostfix))));
                Log.LogInfo("[colors] Patched SpawnPlayerDummy (each player copy keeps its own color).");
            }
            catch (Exception e) { Log.LogWarning($"[colors] install: {e.Message}"); }
        }

        internal static void SpawnPlayerDummy_ColorPrefix(object __instance, out Color __state)
        {
            __state = new Color(-1f, 0f, 0f, 0f);
            try
            {
                var colors = _mmColorsF.GetValue(__instance) as Material[];
                if (colors != null && colors.Length > 0 && colors[0] != null) __state = colors[0].color;
            }
            catch { }
        }

        internal static void SpawnPlayerDummy_ColorPostfix(object __instance, byte i, GameObject __result, Color __state)
        {
            try
            {
                var colors = _mmColorsF.GetValue(__instance) as Material[];
                if (colors == null || colors.Length == 0 || colors[0] == null || __state.r < 0f) return;
                int num = i % colors.Length;
                Color own = num == 0 ? __state : colors[num].color;
                colors[0].color = __state;   // color 0 back for everyone else
                if (__result == null) return;
                var mat = new Material(colors[0]);
                mat.color = own;
                foreach (var lr in __result.GetComponentsInChildren<LineRenderer>()) lr.sharedMaterial = mat;
                // By reflection, like the rest of the plugin: a direct reference
                // would tie this plugin to one build's assembly name.
                var scwd = CachedType("SetColorWhenDamaged");
                if ((object)_scwdStartMatF == null && (object)scwd != null) _scwdStartMatF = AccessTools.Field(scwd, "startMat");
                if ((object)scwd == null || (object)_scwdStartMatF == null) return;
                foreach (var d in __result.GetComponentsInChildren(scwd))
                    if (_scwdStartMatF.GetValue(d) as Material == colors[0]) _scwdStartMatF.SetValue(d, mat);
            }
            catch (Exception e) { Log.LogWarning($"[colors] {e.Message}"); }
        }
    }
}
