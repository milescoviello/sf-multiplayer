using System;
using System.Diagnostics;
using System.Threading;

namespace SFClientRecon
{
    // Arrival timing of other players' PlayerUpdates (msgType 10, one channel
    // per player), measured on the receive thread: remote players move only as
    // evenly as these arrive (stock eases toward each new update). Logged every
    // 10 s as "[rx-timing]": mean gap (stock sends every 20 ms), late gaps
    // (> 33 / > 66 ms: the player visibly stalls), bunched ones (< 5 ms: two
    // updates in one frame, the first is lost to the eye), and the worst gap.
    internal static class RxTiming
    {
        private static readonly object _lock = new object();
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static readonly long[] _last = new long[64], _n = new long[64], _sum = new long[64], _max = new long[64], _late33 = new long[64], _late66 = new long[64], _bunched = new long[64];
        private static long _nextLogAt = 10000;

        internal static void OnPacket(byte[] pkt, int n)
        {
            if (n < 14 || pkt[4] != 10) return;
            int ch = pkt[n - 1];
            if (ch >= 64) return;
            long now = _clock.ElapsedMilliseconds;
            Monitor.Enter(_lock);
            try
            {
                long last = _last[ch];
                _last[ch] = now;
                if (last == 0) return;
                long gap = now - last;
                if (gap > 1000) return;   // a new round / respawn, not jitter
                _n[ch]++; _sum[ch] += gap;
                if (gap > _max[ch]) _max[ch] = gap;
                if (gap > 33) _late33[ch]++;
                if (gap > 66) _late66[ch]++;
                if (gap < 5) _bunched[ch]++;
            }
            finally { Monitor.Exit(_lock); }
        }

        // Main thread.
        internal static void Tick()
        {
            long now = _clock.ElapsedMilliseconds;
            if (now < _nextLogAt) return;
            _nextLogAt = now + 10000;
            var sb = new System.Text.StringBuilder();
            Monitor.Enter(_lock);
            try
            {
                for (int ch = 0; ch < 64; ch++)
                {
                    if (_n[ch] < 20) continue;
                    sb.Append(" | ch").Append(ch).Append(": ").Append(_n[ch]).Append(" updates, mean gap ")
                      .Append((_sum[ch] / (double)_n[ch]).ToString("0.0")).Append(" ms, >33ms ").Append(_late33[ch])
                      .Append(", >66ms ").Append(_late66[ch]).Append(", bunched ").Append(_bunched[ch]).Append(", max ").Append(_max[ch]).Append(" ms");
                    _n[ch] = _sum[ch] = _max[ch] = _late33[ch] = _late66[ch] = _bunched[ch] = 0;
                }
            }
            finally { Monitor.Exit(_lock); }
            if (sb.Length > 0) Plugin.Log.LogInfo("[rx-timing]" + sb);
        }
    }
}
