using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SFClientRecon
{
    // Other players on our screen, smooth.
    //
    // Stock (NetworkPlayer): each PlayerUpdate sets a target (where the sender
    // says its body is), and the next physics step moves the copy 1/6 of the
    // way there (LerpLocalDummyForTime ends after one step: mTimeStartedLerping
    // is never set). Between updates there is no correction at all: the copy
    // walks on its own physics from the sender's inputs. So the copy gets one
    // nudge per update that lands, and over the internet updates land unevenly
    // (two in one frame, then none for three), which shows as stutter.
    //
    // Here: the same stock-strength correction (1/6 of the way), but every
    // physics step, toward the newest reported position carried forward by the
    // sender's velocity for the time since it arrived (at most 50 ms). Uneven
    // arrival no longer turns into uneven nudges. If updates stop (150 ms), it
    // stops correcting, as stock does between updates. Purely visual, the same
    // for every player. SF_REMOTE_INTERP=0 = stock.
    //
    // Measured (bot driven in a fixed pattern, physics-step traces of the
    // original and of another client's copy; "jitter" = mean change of the
    // per-step movement, relative to the original's): stock 5-6x at 40±10 ms
    // per leg, 9x at 60±25 ms, 1.8x on a LAN; this 1.3x, 2.0x, 1.2x, with half
    // the position error and less lag.
    //
    // Send times (client 0.7.8): every PlayerUpdate we send carries our clock
    // (3 bytes after the game's data, which the game's reader never looks at).
    // With it, velocity is measured over the sender's real spacing instead of
    // the uneven arrival times, and the newest position is carried forward by
    // the time since it was SENT (minus the path's lowest delay): an update
    // that arrived 20 ms late is projected 20 ms further, so arrival jitter
    // mostly cancels, with no added delay. It also helps players on slow PCs,
    // whose fewer updates per second are spaced by their frame times. Updates
    // without it (older clients) are handled as before. SF_REMOTE_STAMPS=0
    // ignores the send times (for comparing).
    //
    // "[smooth]" logs every 10 s how evenly each player moves on our screen
    // (jerk: mean change of the per-step movement; lower = smoother) and how
    // many updates arrived.
    public partial class Plugin
    {
        private static readonly bool RemoteInterp = Environment.GetEnvironmentVariable("SF_REMOTE_INTERP") != "0";
        private const float CorrectionPerStep = 1f / 6f;   // stock: Lerp(…, Time.deltaTime * 10) once per update, at 60 steps/s
        private const float StaleAfter = 0.15f;            // no update for this long: stop correcting
        private const float MaxProjection = 0.05f;         // carry the last position forward at most this long
        private const float MaxProjectionStamped = 0.07f;  // with send times: jitter + time since arrival
        private static readonly bool UseStamps = Environment.GetEnvironmentVariable("SF_REMOTE_STAMPS") != "0";
        private const byte StampMarker = 0x5F;
        private static readonly float StampGain = ParseGain(Environment.GetEnvironmentVariable("SF_REMOTE_STAMP_GAIN"));
        private static float ParseGain(string v) { float g; return float.TryParse(v, out g) && g >= 0f && g <= 1.5f ? g : 1f; }
        private static readonly System.Diagnostics.Stopwatch _stampClock = System.Diagnostics.Stopwatch.StartNew();

        private class RemoteTrack
        {
            public bool Have;                  // an update to correct toward
            public float LastArrival = -1f;
            public Vector2 LastP, Velocity;    // reported body position (y, z) and its velocity
            // The last few updates (arrival time, position): velocity is measured
            // across them, so one late or bunched update barely moves it.
            public readonly float[] HT = new float[6];
            public readonly Vector2[] HP = new Vector2[6];
            public int HN, HHead;
            public int Updates;                // this log window
            // Send times: the newest one (sender clock, s, counted from this
            // track's start), its 16-bit raw value, and the lowest arrival -
            // send seen (the path's own delay plus the clock difference).
            public bool Stamped, SaidStamped; public int LastMs = -1; public float SenderT, Offset;
            // smoothness stats
            public Vector3 PrevPos; public float PrevStep = -1f; public bool HavePrev;
            public double JerkSum, SpeedSum; public int Steps, Stalls;
        }
        private static readonly Dictionary<int, RemoteTrack> _tracks = new Dictionary<int, RemoteTrack>();
        private static System.Reflection.FieldInfo _npRigsF, _npDontSyncF, _npLerpingF, _npLocalF, _npSpawnIdF;
        private static float _smoothLogAt;

        // SF_SMOOTH_TRACE=1 (or a file BepInEx/smooth-trace.on): every physics
        // step's body position of every player ("S") and every received update
        // ("U") to BepInEx/smooth-trace.csv, for measuring smoothness offline.
        // Test runs only: flushing it can stall a frame on a slow disk.
        private static System.IO.StreamWriter _trace;
        private static float _traceFlushAt;
        private static void Trace(string kind, object sid, int local, float y, float z)
        {
            if (_trace == null) return;
            _trace.Write(((DateTime.UtcNow.Ticks / 10000L) % 100000000L).ToString());
            _trace.Write(',' + kind + ',' + sid + ',' + local + ',' + y.ToString("0.0000") + ',' + z.ToString("0.0000") + ',' + Time.fixedTime.ToString("0.0000") + '\n');
        }

        private void InstallRemoteSmoothing()
        {
            try
            {
                var npType = CachedType("NetworkPlayer");
                if ((object)npType == null) return;
                _npRigsF = AccessTools.Field(npType, "allMyRigs");
                _npDontSyncF = AccessTools.Field(npType, "mDontSyncForFrames");
                _npLerpingF = AccessTools.Field(npType, "mIsLerping");
                _npLocalF = AccessTools.Field(npType, "mHasLocalControl");
                _npSpawnIdF = AccessTools.Field(npType, "mNetworkSpawnID");
                var sync = AccessTools.Method(npType, "SyncClientState");
                var lerp = AccessTools.Method(npType, "LerpLocalDummyForTime");
                var fixedUpd = AccessTools.Method(npType, "FixedUpdate");
                if ((object)sync == null || (object)lerp == null || (object)_npRigsF == null || (object)_npLerpingF == null)
                { Log.LogWarning("[smooth] NetworkPlayer members not found: remote smoothing off."); return; }
                var h = new Harmony(PluginGuid + ".remote-smooth");
                h.Patch(sync, postfix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(SyncClientState_SmoothPostfix))));
                if (RemoteInterp)
                    h.Patch(lerp, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(LerpLocalDummy_SmoothPrefix))));
                var moved = AccessTools.Method(CachedType("MultiplayerManager"), "OnPlayerMoved");
                if ((object)moved != null)
                    h.Patch(moved, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(OnPlayerMoved_StampPrefix))));
                else Log.LogWarning("[smooth] MultiplayerManager.OnPlayerMoved not found: our updates go without send times.");
                if ((object)fixedUpd != null)
                    h.Patch(fixedUpd, postfix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(NetworkPlayerFixedUpdate_StatsPostfix))));
                if (Environment.GetEnvironmentVariable("SF_SMOOTH_TRACE") == "1"
                    || System.IO.File.Exists(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "smooth-trace.on")))
                    _trace = new System.IO.StreamWriter(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "smooth-trace.csv"), false);
                Log.LogInfo($"[smooth] remote players: {(RemoteInterp ? "corrected every physics step toward the newest update" : "stock")}.");
            }
            catch (Exception e) { Log.LogWarning($"[smooth] install: {e.Message}"); }
        }

        private static RemoteTrack TrackOf(object np)
        {
            var c = np as Component;
            if ((object)c == null) return null;
            int id = c.GetInstanceID();
            RemoteTrack t;
            if (!_tracks.TryGetValue(id, out t)) { t = new RemoteTrack(); _tracks[id] = t; }
            return t;
        }

        private static object SpawnIdOf(object np) { return (object)_npSpawnIdF != null ? _npSpawnIdF.GetValue(np) : "?"; }

        // Every received PlayerUpdate: remember the reported position, and the
        // velocity between the last two (for carrying it forward between them).
        internal static void SyncClientState_SmoothPostfix(object __instance, byte[] data)
        {
            try
            {
                if (data == null || data.Length < 4) return;
                var t = TrackOf(__instance);
                if (t == null) return;
                var p = new Vector2((short)(data[0] | (data[1] << 8)) / 100f, (short)(data[2] | (data[3] << 8)) / 100f);
                float now = Time.realtimeSinceStartup;
                int ms = UseStamps ? ReadStamp(data) : -1;
                // A respawn, a teleport (> 6 u in one update), a long gap or a
                // switch between stamped and unstamped updates: start over.
                bool restart = !t.Have || now - t.LastArrival > 0.25f || (p - t.LastP).sqrMagnitude > 36f || (ms >= 0) != t.Stamped;
                float at = now;    // this update's time on the track's clock: send time if stamped, else arrival
                if (ms >= 0)
                {
                    if (!restart && t.LastMs >= 0)
                    {
                        int d = (ms - t.LastMs) & 0xFFFF;
                        if (d >= 32768) return;       // older than the newest one (arrived out of order): skip it
                        at = t.SenderT + d / 1000f;
                    }
                    else { restart = true; at = 0f; }
                    float o = now - at;
                    if (restart || o < t.Offset) t.Offset = o;
                    else t.Offset += (o - t.Offset) * 0.005f;   // a path that got slower for good: follow it, slowly
                    t.LastMs = ms; t.SenderT = at;
                }
                else t.LastMs = -1;
                if (ms >= 0 && !t.Stamped && !t.SaidStamped) { t.SaidStamped = true; Log.LogInfo("[smooth] slot " + SpawnIdOf(__instance) + " sends send times: projecting by them."); }
                t.Stamped = ms >= 0;
                if (restart) t.HN = 0;
                t.HHead = (t.HHead + 1) % t.HT.Length;
                t.HT[t.HHead] = at; t.HP[t.HHead] = p;
                if (t.HN < t.HT.Length) t.HN++;
                // Velocity from the oldest kept update to this one (needs at least
                // 30 ms between them, else keep the previous estimate).
                t.Velocity = t.HN < 2 ? Vector2.zero : t.Velocity;
                if (t.HN >= 2)
                {
                    int oldest = (t.HHead - (t.HN - 1) + t.HT.Length) % t.HT.Length;
                    float span = at - t.HT[oldest];
                    if (span >= 0.03f)
                    {
                        Vector2 v = (p - t.HP[oldest]) / span;
                        if (v.sqrMagnitude < 900f) t.Velocity = v;
                    }
                }
                t.Have = true; t.LastArrival = now; t.LastP = p; t.Updates++;
                if (_trace != null) Trace("U", SpawnIdOf(__instance), 0, p.x, p.y);
            }
            catch { }
        }

        // The online PlayerUpdate: i16 y, i16 z, 3 x sbyte, 2 x byte, u16 shots,
        // 8 bytes per shot, 1 byte weapon = 12 + 8n written. The game allocates
        // 18 + 2n (its Size properties are off), so an update without shots
        // ends in 6 zero bytes: the send time goes there (u16 ms + marker),
        // else 3 bytes are added. Either way it starts right after the weapon byte.
        private static int PlayerUpdateLength(byte[] data)
        {
            if (data == null || data.Length < 12) return -1;
            int n = data[9] | (data[10] << 8);
            int len = 12 + 8 * n;
            return n > 64 || data.Length < len ? -1 : len;
        }

        private static int ReadStamp(byte[] data)
        {
            int end = PlayerUpdateLength(data);
            if (end < 0 || data.Length < end + 3 || data[end + 2] != StampMarker) return -1;
            return data[end] | (data[end + 1] << 8);
        }

        // MultiplayerManager.OnPlayerMoved: our own PlayerUpdate on its way out.
        internal static void OnPlayerMoved_StampPrefix(ref byte[] data)
        {
            try
            {
                int len = PlayerUpdateLength(data);
                if (len < 0) return;                                  // not the format we know: leave it alone
                int ms = (int)(_stampClock.ElapsedMilliseconds & 0xFFFF);
                byte[] b = data;
                if (data.Length >= len + 3)
                {
                    for (int i = len; i < data.Length; i++) if (data[i] != 0) return;   // not padding: leave it alone
                }
                else
                {
                    b = new byte[len + 3];
                    Buffer.BlockCopy(data, 0, b, 0, len);
                }
                b[len] = (byte)ms; b[len + 1] = (byte)(ms >> 8); b[len + 2] = StampMarker;
                data = b;
                if (!_saidStamping) { _saidStamping = true; Log.LogInfo("[smooth] our updates carry send times (" + data.Length + " bytes)."); }
            }
            catch { }
        }
        private static bool _saidStamping;

        // Replaces the stock one-step lerp for other players (see the top).
        internal static bool LerpLocalDummy_SmoothPrefix(object __instance)
        {
            try
            {
                if ((object)_npLocalF != null && (bool)_npLocalF.GetValue(__instance)) return true;
                var t = TrackOf(__instance);
                if (t == null || !t.Have) return true;
                _npLerpingF.SetValue(__instance, true);    // keep FixedUpdate calling us every step
                if ((object)_npDontSyncF != null)
                {
                    int wait = (int)_npDontSyncF.GetValue(__instance);
                    if (wait > 0) { _npDontSyncF.SetValue(__instance, wait - 1); return false; }
                }
                float age = Time.realtimeSinceStartup - t.LastArrival;
                if (age > StaleAfter) return false;
                // Stamped: time since it was sent, less the path's lowest delay.
                // (= time since arrival + how late it arrived; StampGain scales the second part)
                float proj = t.Stamped ? Mathf.Clamp(age + StampGain * Mathf.Max(0f, t.LastArrival - t.Offset - t.SenderT), 0f, MaxProjectionStamped)
                                       : Mathf.Min(age, MaxProjection);
                Vector2 target = t.LastP + t.Velocity * proj;
                var rigs = _npRigsF.GetValue(__instance) as Rigidbody[];
                if (rigs == null || rigs.Length == 0) return false;
                Vector3 avg = Vector3.zero;
                for (int i = 0; i < rigs.Length; i++) avg += rigs[i].transform.position;
                avg /= rigs.Length;
                var tr = ((Component)__instance).transform;
                Vector3 corr = new Vector3(0f, target.x - avg.y, target.y - avg.z);
                tr.position = tr.position + corr * CorrectionPerStep;
                return false;
            }
            catch { return true; }
        }

        // Smoothness stats for every player (ours included, as the reference).
        internal static void NetworkPlayerFixedUpdate_StatsPostfix(object __instance)
        {
            try
            {
                var t = TrackOf(__instance);
                var rigs = _npRigsF.GetValue(__instance) as Rigidbody[];
                if (t == null || rigs == null || rigs.Length == 0) return;
                Vector3 avg = Vector3.zero;
                for (int i = 0; i < rigs.Length; i++) avg += rigs[i].transform.position;
                avg /= rigs.Length;
                if (t.HavePrev)
                {
                    float step = new Vector2(avg.y - t.PrevPos.y, avg.z - t.PrevPos.z).magnitude;
                    if (step < 0.5f)   // ignore teleports / respawns
                    {
                        if (t.PrevStep >= 0f) { t.JerkSum += Mathf.Abs(step - t.PrevStep); t.SpeedSum += step; t.Steps++; }
                        if (step < 0.0005f && t.PrevStep > 0.01f) t.Stalls++;
                        t.PrevStep = step;
                    }
                    else t.PrevStep = -1f;
                }
                t.PrevPos = avg; t.HavePrev = true;
                if (_trace != null)
                    Trace("S", SpawnIdOf(__instance), (object)_npLocalF != null && (bool)_npLocalF.GetValue(__instance) ? 1 : 0, avg.y, avg.z);
            }
            catch { }
        }

        private static readonly List<int> _deadTracks = new List<int>();
        private void TickSmoothLog()
        {
            if (_trace != null && Time.realtimeSinceStartup > _traceFlushAt) { _traceFlushAt = Time.realtimeSinceStartup + 1f; try { _trace.Flush(); } catch { } }
            if (Time.realtimeSinceStartup < _smoothLogAt) return;
            float span = 10f + Mathf.Max(0f, Time.realtimeSinceStartup - _smoothLogAt);
            _smoothLogAt = Time.realtimeSinceStartup + 10f;
            var npType = CachedType("NetworkPlayer");
            if ((object)npType == null) return;
            var sb = new System.Text.StringBuilder();
            var live = new HashSet<int>();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(npType))
            {
                var c = o as Component;
                RemoteTrack t;
                if ((object)c == null) continue;
                live.Add(c.GetInstanceID());
                if (!_tracks.TryGetValue(c.GetInstanceID(), out t)) continue;
                int updates = t.Updates; t.Updates = 0;
                if (t.Steps < 60) continue;
                bool local = (object)_npLocalF != null && (bool)_npLocalF.GetValue(o);
                double speed = t.SpeedSum / t.Steps;
                sb.Append(" | slot ").Append(SpawnIdOf(o)).Append(local ? " (us)" : "").Append(": speed ").Append((speed * 60).ToString("0.0"))
                  .Append(" u/s, jerk ").Append((t.JerkSum / t.Steps).ToString("0.0000")).Append(" (").Append(speed > 1e-4 ? (t.JerkSum / t.Steps / speed).ToString("0.00") : "-")
                  .Append(" of speed), stalls ").Append(t.Stalls);
                if (!local) sb.Append(", ").Append((updates / span).ToString("0")).Append(" updates/s");
                t.JerkSum = t.SpeedSum = 0; t.Steps = 0; t.Stalls = 0;
            }
            // Players that are gone (left, scene change): forget them.
            _deadTracks.Clear();
            foreach (var id in _tracks.Keys) if (!live.Contains(id)) _deadTracks.Add(id);
            foreach (var id in _deadTracks) _tracks.Remove(id);
            if (sb.Length > 0) Log.LogInfo("[smooth]" + (RemoteInterp ? " continuous" : " stock") + sb);
        }
    }
}
