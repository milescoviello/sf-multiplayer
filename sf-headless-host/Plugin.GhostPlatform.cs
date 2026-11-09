using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SFHeadlessHost
{
    // Ghost platforms (the ones that fade out and back in) on the server.
    // The server decides when they switch and clients follow it (MapInfoSync),
    // but on the headless server stock GhostPlatform broke twice:
    //  - Start reads rends[0] before anything else and the server has no
    //    renderers for them: it threw before saving the start position, so
    //    a platform that starts off never turned off;
    //  - FadeOut/FadeIn call GhostPlatformSound.Instance.PlaySound() first,
    //    and the server has no GhostPlatformSound: every fade threw before
    //    its colliders were switched.
    // So on the server the platforms stayed solid while every client saw them
    // vanish: crates and players' server copies stood on nothing.
    // Fix: a silent GhostPlatformSound instance, and Start without renderers.
    public partial class Plugin
    {
        private static FieldInfo _gpAnimF, _gpCollidersF, _gpRendsF, _gpStartPosF, _gpIsOnF, _gpStartOnF, _csaDontPlayF, _csaState1F;
        private static MethodInfo _gpTurnOffM, _gpTurnOnM;
        private static int _gpFixed;

        private void InstallGhostPlatformFixes(Harmony harmony)
        {
            var gpT = CachedType("GhostPlatform");
            var gsT = CachedType("GhostPlatformSound");
            TryPatch(harmony, "GhostPlatformSound.PlaySound (prefix → skip: no audio on the server)",
                (object)gsT != null ? AccessTools.Method(gsT, "PlaySound") : null,
                prefix: nameof(SkipOnServerPrefix));
            TryPatch(harmony, "GhostPlatform.Start (prefix → works without renderers)",
                (object)gpT != null ? AccessTools.Method(gpT, "Start") : null,
                prefix: nameof(GhostPlatformStartPrefix));
            TryPatch(harmony, "GhostPlatform.FadeOut (prefix → null-safe fade, stock timing)",
                (object)gpT != null ? AccessTools.Method(gpT, "FadeOut") : null,
                prefix: nameof(GhostFadeOutPrefix));
            TryPatch(harmony, "GhostPlatform.FadeIn (prefix → null-safe fade, stock timing)",
                (object)gpT != null ? AccessTools.Method(gpT, "FadeIn") : null,
                prefix: nameof(GhostFadeInPrefix));
        }

        internal static bool GhostFadeOutPrefix(object __instance, ref System.Collections.IEnumerator __result)
        { __result = new GhostFade(__instance, false); return false; }
        internal static bool GhostFadeInPrefix(object __instance, ref System.Collections.IEnumerator __result)
        { __result = new GhostFade(__instance, true); return false; }

        // Stock FadeOut: wait 1 s, sound, animation off, wait 2 s, TurnOff,
        // wait 0.1 s, colliders off. FadeIn: sound, animation on, wait 2 s,
        // TurnOn, colliders on. Same steps without the sound, and without
        // throwing when the animation component is missing.
        private class GhostFade : System.Collections.IEnumerator
        {
            private readonly object _gp; private readonly bool _in; private int _step; private object _cur;
            public GhostFade(object gp, bool fadeIn) { _gp = gp; _in = fadeIn; }
            public object Current { get { return _cur; } }
            public void Reset() { _step = 0; }
            public bool MoveNext()
            {
                try
                {
                    var c = _gp as Component;
                    if (c == null) return false;   // destroyed (map unloaded)
                    if (!_in)
                    {
                        switch (_step++)
                        {
                            case 0: _cur = new WaitForSeconds(1f); return true;
                            case 1: SetAnim(false); _cur = new WaitForSeconds(2f); return true;
                            case 2: Call(_gpTurnOffM); _cur = new WaitForSeconds(0.1f); return true;
                            case 3: SetColliders(false); return false;
                        }
                        return false;
                    }
                    switch (_step++)
                    {
                        case 0: SetAnim(true); _cur = new WaitForSeconds(2f); return true;
                        case 1: Call(_gpTurnOnM); SetColliders(true); return false;
                    }
                    return false;
                }
                catch (Exception e) { if (_gpFadeErrors++ < 3) Log.LogWarning($"[ghost-platform] fade: {e.Message}"); return false; }
            }
            private void SetAnim(bool on)
            {
                var a = (object)_gpAnimF != null ? _gpAnimF.GetValue(_gp) : null;
                if (a != null && (object)_csaState1F != null) _csaState1F.SetValue(a, on);
                else if (_gpNoAnimLogged++ < 1) Log.LogInfo("[ghost-platform] no CodeStateAnimation on the server copy (stock fade threw here).");
            }
            private void Call(MethodInfo m) { if ((object)m != null) m.Invoke(_gp, null); }
            private void SetColliders(bool on)
            {
                var cols = (object)_gpCollidersF != null ? _gpCollidersF.GetValue(_gp) as Collider[] : null;
                if (cols == null) return;
                foreach (var col in cols) if (col != null) col.enabled = on;
            }
        }
        private static int _gpFadeErrors, _gpNoAnimLogged;

        private static void EnsureGhostPlatformSound()
        {
            var gsT = CachedType("GhostPlatformSound");
            if ((object)gsT == null) return;
            var inst = AccessTools.Property(gsT, "Instance");
            if ((object)inst != null && (inst.GetValue(null, null) as UnityEngine.Object) != null) return;
            var go = new GameObject("sf-ghost-platform-sound");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent(gsT);   // its Awake sets the static instance; PlaySound is skipped on the server
            Log.LogInfo("[ghost-platform] server GhostPlatformSound created (silent).");
        }

        internal static bool GhostPlatformStartPrefix(object __instance)
        {
            try
            {
                var c = __instance as Component;
                if ((object)c == null) return true;
                EnsureGhostPlatformSound();
                var t = __instance.GetType();
                if ((object)_gpAnimF == null)
                {
                    _gpAnimF = AccessTools.Field(t, "anim"); _gpCollidersF = AccessTools.Field(t, "colliders");
                    _gpRendsF = AccessTools.Field(t, "rends"); _gpStartPosF = AccessTools.Field(t, "startPosition");
                    _gpIsOnF = AccessTools.Field(t, "isOn"); _gpStartOnF = AccessTools.Field(t, "startOn");
                    _gpTurnOffM = AccessTools.Method(t, "TurnOff"); _gpTurnOnM = AccessTools.Method(t, "TurnOn");
                    var csaT = CachedType("CodeStateAnimation");
                    if ((object)csaT != null) { _csaDontPlayF = AccessTools.Field(csaT, "dontPlayFor"); _csaState1F = AccessTools.Field(csaT, "state1"); }
                }
                var rends = c.GetComponentsInChildren<Renderer>();
                if (rends != null && rends.Length > 0) return true;   // stock Start works (fields above serve the fades)
                if ((object)_gpStartPosF == null || (object)_gpCollidersF == null || (object)_gpRendsF == null) return true;
                var csa = (object)_gpAnimF != null ? c.GetComponent(_gpAnimF.FieldType) : null;
                if ((object)_gpAnimF != null) _gpAnimF.SetValue(__instance, csa);
                var cols = c.GetComponentsInChildren<Collider>();
                _gpCollidersF.SetValue(__instance, cols);
                _gpRendsF.SetValue(__instance, new Renderer[0]);
                _gpStartPosF.SetValue(__instance, c.transform.localPosition);
                bool startOn = (object)_gpStartOnF == null || (bool)_gpStartOnF.GetValue(__instance);
                if ((object)csa != null)
                {
                    if ((object)_csaDontPlayF != null) _csaDontPlayF.SetValue(csa, 1f);
                    if ((object)_csaState1F != null) _csaState1F.SetValue(csa, startOn);
                }
                if (!startOn)
                {
                    if ((object)_gpTurnOffM != null) _gpTurnOffM.Invoke(__instance, null);
                    foreach (var col in cols) col.enabled = false;
                    if ((object)_gpIsOnF != null) _gpIsOnF.SetValue(__instance, false);
                }
                else if ((object)_gpTurnOnM != null) _gpTurnOnM.Invoke(__instance, null);
                if (_gpFixed++ < 3) Log.LogInfo($"[ghost-platform] '{c.name}' started without renderers (startOn={startOn}).");
                return false;
            }
            catch (Exception e) { Log.LogWarning($"[ghost-platform] Start: {e.Message}"); return true; }
        }
    }
}
