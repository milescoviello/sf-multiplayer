using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SFClientRecon
{
    // Kills show on the killer's screen right away ("death animation should
    // happen sooner, hard to tell when you kill someone"; "deaths ~0.3 s late").
    //
    // Stock damage is shooter-decided: our hit on another player's copy runs
    // HealthHandler.TakeDamage here, and its killing blow goes out as
    // PlayerTookDamage(666.666). In an online match that local call never plays
    // the death itself: the copy dies when the host sends our own message back
    // (TakeDamage(float, byte, bool) -> Die). Against a dedicated server every
    // player is a client, so every kill waited a full round trip to the server
    // plus its frame, and in COMP anything the ping equalizer was holding.
    //
    // Now: when our killing blow on another player goes out, their copy dies
    // here at once (the same private Die the echo would run, so the echo finds
    // it dead and only adds its hit reaction). Only for kills credited to one of
    // our own players. Every client applies a killing blow unconditionally, and
    // the server only drops damage for an absurd reach, so the kill can't be
    // refused afterwards. Once their copy is dead the game reads and discards
    // later damage messages for it, so the confirmation is watched where our
    // ReadPacket replacement hands packets out: on the victim's event channel the
    // first killing blow back is the server relaying ours, the next is the
    // victim's own death report (their Die sends one at once). No victim report
    // within 3 s = the screens disagree: logged and reported (DIAG). Not our round to end: on a client KillPlayer only
    // counts stats, the server still decides the round.
    // SF_KILL_PREDICT=0 turns the early death off (timing logs stay).
    public partial class Plugin
    {
        private static readonly bool KillPredict = Environment.GetEnvironmentVariable("SF_KILL_PREDICT") != "0";
        private static readonly System.Diagnostics.Stopwatch _killClock = System.Diagnostics.Stopwatch.StartNew();
        private static readonly Dictionary<int, long> _killSentAt = new Dictionary<int, long>();
        private sealed class PredictedKill { public long At; public int Echoes; public long RelayMs = -1; }
        private static readonly Dictionary<int, PredictedKill> _killPredicted = new Dictionary<int, PredictedKill>();   // by victim event channel
        private static System.Reflection.FieldInfo _hhHealthF, _hhCtrlF, _ctrlHasCtrlF2, _hhInfoF, _infoDeadF, _hhNetPlayerF, _npEventChF;
        private static System.Reflection.MethodInfo _hhDieM;
        private static System.Reflection.PropertyInfo _isNetMatchP;
        private static bool _inPredictedDie;
        private static int _killLogN, _killPredictN;

        private void InstallKillTiming()
        {
            try
            {
                var hh = CachedType("HealthHandler");
                if ((object)hh == null) return;
                _hhHealthF = AccessTools.Field(hh, "health");
                _hhCtrlF = AccessTools.Field(hh, "controller");
                _hhInfoF = AccessTools.Field(hh, "info");
                _hhDieM = AccessTools.Method(hh, "Die");
                _hhNetPlayerF = AccessTools.Field(hh, "mNetworkPlayer");
                var np = CachedType("NetworkPlayer");
                if ((object)np != null) _npEventChF = AccessTools.Field(np, "mEventChannel");
                var info = CachedType("CharacterInformation");
                if ((object)info != null) _infoDeadF = AccessTools.Field(info, "isDead");
                var mh = CachedType("MatchmakingHandler");
                if ((object)mh != null) _isNetMatchP = AccessTools.Property(mh, "IsNetworkMatch");
                var ctl = CachedType("Controller");
                if ((object)ctl != null) _ctrlHasCtrlF2 = AccessTools.Field(ctl, "mHasControl");
                var h = new Harmony(PluginGuid + ".kill-timing");
                foreach (var m in hh.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    if (m.Name != "TakeDamage") continue;
                    var ps = m.GetParameters();
                    if (ps.Length >= 2 && ps[1].ParameterType == ctl && m.ReturnType == typeof(bool))
                        h.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(KillTiming_TakeDamagePostfix))));
                }
                if ((object)_hhDieM != null) h.Patch(_hhDieM, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(KillTiming_DiePrefix))));
                Log.LogInfo("[kill] " + (KillPredict && (object)_hhDieM != null && (object)_isNetMatchP != null
                    ? "our kills play at once on our screen (SF_KILL_PREDICT=0 waits for the server)."
                    : "kills wait for the server's echo."));
            }
            catch (Exception e) { Log.LogWarning("[kill] timing diag: " + e.Message); }
        }

        private static bool IsOurPlayer(object hh)
        {
            try { var c = _hhCtrlF.GetValue(hh); return c != null && (bool)_ctrlHasCtrlF2.GetValue(c); } catch { return true; }
        }

        private static bool CopyIsDead(object hh)
        {
            try
            {
                var inf = (object)_hhInfoF != null ? _hhInfoF.GetValue(hh) : null;
                return inf != null && (object)_infoDeadF != null && (bool)_infoDeadF.GetValue(inf);
            }
            catch { return false; }
        }

        // Local hit on someone's copy: TakeDamage(float damage, Controller damager, ...).
        // __result true = the damage applied (and, at 0 health, the killing blow went out).
        internal static void KillTiming_TakeDamagePostfix(object __instance, bool __result, object damager)
        {
            try
            {
                if (!__result || IsOurPlayer(__instance)) return;
                var comp = __instance as Component;
                if (comp == null || (float)_hhHealthF.GetValue(__instance) > 0f) return;
                int id = comp.GetInstanceID();
                if (!_killSentAt.ContainsKey(id)) _killSentAt[id] = _killClock.ElapsedMilliseconds;

                if (!KillPredict || (object)_hhDieM == null || (object)_isNetMatchP == null) return;
                if (damager == null || !(bool)_ctrlHasCtrlF2.GetValue(damager)) return;   // only kills by our own players
                if (!(bool)_isNetMatchP.GetValue(null, null) || CopyIsDead(__instance)) return;
                _inPredictedDie = true;
                try { _hhDieM.Invoke(__instance, null); }
                finally { _inPredictedDie = false; }
                if (CopyIsDead(__instance))
                {
                    _killSentAt.Remove(id);
                    int ch = -1;
                    try { var npi = (object)_hhNetPlayerF != null ? _hhNetPlayerF.GetValue(__instance) : null; if (npi != null && (object)_npEventChF != null) ch = (int)_npEventChF.GetValue(npi); } catch { }
                    if (ch >= 0) _killPredicted[ch] = new PredictedKill { At = _killClock.ElapsedMilliseconds };
                    if (_killPredictN++ < 50) Log.LogInfo("[kill] our kill: their death played now, on our killing blow.");
                }
            }
            catch (Exception e) { if (_killPredictN++ < 50) Log.LogWarning("[kill] early death: " + e.Message); }
        }

        // Called from our ReadPacket replacement (main thread) for every packet the
        // game reads. [u32 ts][type][body: u8 attacker, f32 damage, ...][u64 id][u8 ch]
        internal static void KillTiming_NotePacket(int channel, byte[] pkt)
        {
            try
            {
                PredictedKill k;
                if (pkt == null || pkt.Length < 10 || pkt[4] != 11 || !_killPredicted.TryGetValue(channel, out k)) return;
                if (Math.Abs(BitConverter.ToSingle(pkt, 6) - 666.666f) > 0.01f) return;
                long ms = _killClock.ElapsedMilliseconds - k.At;
                k.Echoes++;
                if (k.Echoes == 1) { k.RelayMs = ms; return; }
                _killPredicted.Remove(channel);
                if (_killLogN++ < 200)
                    Log.LogInfo("[kill] confirmed: the server relayed our killing blow " + k.RelayMs + " ms later (the old death time on our screen), their own death report came " + ms + " ms later.");
            }
            catch { }
        }

        internal static void KillTiming_DiePrefix(object __instance)
        {
            try
            {
                if (_inPredictedDie) return;
                var comp = __instance as Component;
                if (comp == null || _killLogN > 200 || CopyIsDead(__instance)) return;
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

        // Main thread: a predicted kill the server never echoed means the screens
        // disagree (we show them dead). Should not happen; report it if it does.
        private static float _nextKillCheckAt;
        internal static void TickKillPredict()
        {
            if (_killPredicted.Count == 0 || Time.realtimeSinceStartup < _nextKillCheckAt) return;
            _nextKillCheckAt = Time.realtimeSinceStartup + 0.5f;
            long now = _killClock.ElapsedMilliseconds;
            List<int> late = null;
            foreach (var kv in _killPredicted)
                if (now - kv.Value.At > 3000) (late ?? (late = new List<int>())).Add(kv.Key);
            if (late == null) return;
            foreach (int ch in late)
            {
                var k = _killPredicted[ch];
                _killPredicted.Remove(ch);
                string what = k.Echoes == 0 ? "the server never relayed our killing blow" : "the victim never reported their death";
                Log.LogWarning("[kill] a kill we showed 3 s ago wasn't confirmed: " + what + " (screens may disagree until the next round).");
                QueueDiag("kill shown early not confirmed in 3 s: " + what);
            }
        }
    }
}
