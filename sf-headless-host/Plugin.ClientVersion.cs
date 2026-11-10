using System;
using System.Net;
using UnityEngine;

namespace SFHeadlessHost
{
    // Client versions. The client plugin (0.7.0+) reports its version every
    // 5 s (msgType 48, v26: [u64 steamId][u8 n][n ASCII bytes]). A client that
    // is older than SF_CLIENT_MIN_VERSION (default below), or that reports
    // nothing within 15 s of joining (pre-0.7.0), is told to restart through
    // the "Stick Fight Online" shortcut, which updates it. In COMP it is then
    // removed (a fair match needs everyone on the same client); elsewhere it
    // may keep playing.
    public partial class Plugin
    {
        private const byte PktClientVersion = 48;
        private const string DefaultMinClientVersion = "0.7.0";
        private static string _minClientVersion;
        private float _versionCheckAt;

        private static string MinClientVersion
        {
            get
            {
                if (_minClientVersion == null)
                {
                    var v = Environment.GetEnvironmentVariable("SF_CLIENT_MIN_VERSION");
                    _minClientVersion = string.IsNullOrEmpty(v) ? DefaultMinClientVersion : v.Trim();
                }
                return _minClientVersion;
            }
        }

        private void HandleClientVersion(byte[] data, int off, int len, IPEndPoint from)
        {
            if (len < 10) return;
            ulong sid = 0;
            for (int i = 0; i < 8; i++) sid |= (ulong)data[off + i] << (8 * i);
            int n = data[off + 8];
            if (n <= 0 || n > 32 || len < 9 + n) return;
            string v = System.Text.Encoding.ASCII.GetString(data, off + 9, n);
            if (len >= 9 + n + 4) BindV26ByToken(sid, BitConverter.ToUInt32(data, off + 9 + n), from);   // 0.7.2+: session token
            foreach (var kv in _sfClients)
            {
                var c = kv.Value;
                if (c.SteamID != sid || !c.Addr.Address.Equals(from.Address)) continue;
                if (c.ClientVersion != v) Log.LogInfo($"[version] slot {c.Slot} runs client {v}.");
                c.ClientVersion = v;
            }
        }

        // a.b.c >= min.
        private static bool VersionAtLeast(string v, string min)
        {
            var a = (v ?? "0").Split('.');
            var b = (min ?? "0").Split('.');
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                int x = 0, y = 0;
                if (i < a.Length) int.TryParse(a[i], out x);
                if (i < b.Length) int.TryParse(b[i], out y);
                if (x != y) return x > y;
            }
            return true;
        }

        private void TickClientVersions()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _versionCheckAt) return;
            _versionCheckAt = now + 2f;
            foreach (var kv in _sfClients)
            {
                var c = kv.Value;
                if (!c.Initialized || ReplayMode) continue;
                if (c.KickAt > 0f && now >= c.KickAt) { c.KickAt = -1f; KickClient(c, "client out of date"); continue; }
                if (c.VersionHandled) continue;
                bool known = c.ClientVersion != null;
                if (!known && now - c.InitializedAt < 15f) continue;
                c.VersionHandled = true;
                if (known && VersionAtLeast(c.ClientVersion, MinClientVersion)) continue;
                string msg = $"Your sf-multiplayer client is out of date ({(known ? c.ClientVersion : "before 0.7.0")}; this server needs {MinClientVersion}). Start the game with the \"Stick Fight Online\" shortcut: it updates itself.";
                SendChatToPlayer(c, msg);
                SendBannerTo(c, CompMode ? "COMP needs the current client: restart with the \"Stick Fight Online\" shortcut to update." : msg);
                Log.LogInfo($"[version] slot {c.Slot}: client {(known ? c.ClientVersion : "unknown")} < {MinClientVersion}{(CompMode ? " → removed from COMP in 8 s" : " (warned)")}.");
                if (CompMode) c.KickAt = now + 8f;
            }
        }

        private void SendBannerTo(SfClient c, string text)
        {
            byte[] body = System.Text.Encoding.UTF8.GetBytes(text.Length > 200 ? text.Substring(0, 200) : text);
            IPEndPoint v26Ep;
            if (!_slotV26Endpoint.TryGetValue(c.Slot, out v26Ep))
                v26Ep = new IPEndPoint(c.Addr.Address, V26_CLIENT_PORT);
            SendSfPacket(v26Ep, PktV26Announce, body, 0, 0);
        }

        // The patched client disconnects on ANY KickPlayer it receives (the body
        // is a stock KickResponse, not a slot), so a kick goes to its target
        // only. (Broadcasting it, as /kick and the anti-cheat used to, threw
        // every player out of the lobby.)
        private void KickClient(SfClient c, string why)
        {
            if (c == null || (object)c.Addr == null) return;
            SendSfPacket(c.Addr, PktKickPlayer, new byte[] { 0 }, 0uL, 0);
            Log.LogInfo($"[kick] slot {c.Slot} ({c.Addr}): {why}");
        }

        private SfClient ClientInSlot(int slot)
        {
            foreach (var kv in _sfClients) if (kv.Value.Slot == slot && kv.Value.Initialized) return kv.Value;
            return null;
        }
    }
}
