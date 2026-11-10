using System;
using UnityEngine;

namespace SFClientRecon
{
    // "[perf]" every 10 s in the log: the game's frame rate (and its slowest
    // frame) and what this mod's own per-frame work costs, so a report of
    // "laggy" from a player's log tells their frame rate apart from the network.
    public partial class Plugin
    {
        private readonly System.Diagnostics.Stopwatch _perfSw = new System.Diagnostics.Stopwatch();
        private double _perfUpdateMs, _perfFixedMs;
        private int _perfFrames;
        private float _perfWorst, _perfAt = -1f;
        // Where this mod's Update time goes, by section (logged when it is
        // more than 1.5 ms per frame).
        private static readonly string[] PerfNames = { "pings", "players-left", "viewer-link", "auto-connect", "rejoin", "rx-timing",
            "send-rate", "smooth-log", "debug-console", "channel-fill", "snapshots", "init-from-server", "smooth-targets",
            "crate-push-relay", "fast-combat-input" };
        private readonly long[] _perfSec = new long[16];
        private long _perfLast;
        private void PerfMarkStart() { _perfLast = System.Diagnostics.Stopwatch.GetTimestamp(); }
        private void PerfMark(int i)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            _perfSec[i] += now - _perfLast; _perfLast = now;
        }

        private void TickPerfLog()
        {
            float now = Time.realtimeSinceStartup;
            _perfFrames++;
            if (Time.unscaledDeltaTime > _perfWorst) _perfWorst = Time.unscaledDeltaTime;
            if (_perfAt < 0f) { _perfAt = now + 10f; _perfFrames = 0; _perfWorst = 0f; _perfUpdateMs = _perfFixedMs = 0; Array.Clear(_perfSec, 0, _perfSec.Length); return; }
            if (now < _perfAt) return;
            float span = 10f + (now - _perfAt);
            _perfAt = now + 10f;
            if (_perfFrames > 0)
                Log.LogInfo("[perf] " + (_perfFrames / span).ToString("0") + " fps (slowest frame " + (_perfWorst * 1000f).ToString("0") + " ms); this mod: "
                    + ((_perfUpdateMs + _perfFixedMs) / _perfFrames).ToString("0.00") + " ms per frame (Update " + (_perfUpdateMs / _perfFrames).ToString("0.00")
                    + ", physics " + (_perfFixedMs / _perfFrames).ToString("0.00") + ")");
            if (_perfFrames > 0 && (_perfUpdateMs + _perfFixedMs) / _perfFrames > 1.5)
            {
                var idx = new int[PerfNames.Length];
                for (int i = 0; i < idx.Length; i++) idx[i] = i;
                Array.Sort(idx, (a, b) => _perfSec[b].CompareTo(_perfSec[a]));
                var sb = new System.Text.StringBuilder("[perf] most of it:");
                for (int k = 0; k < 3; k++)
                    sb.Append(' ').Append(PerfNames[idx[k]]).Append(' ')
                      .Append((_perfSec[idx[k]] * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _perfFrames).ToString("0.00")).Append(" ms");
                Log.LogInfo(sb.ToString());
            }
            Array.Clear(_perfSec, 0, _perfSec.Length);
            _perfFrames = 0; _perfWorst = 0f; _perfUpdateMs = _perfFixedMs = 0;
        }
    }
}
