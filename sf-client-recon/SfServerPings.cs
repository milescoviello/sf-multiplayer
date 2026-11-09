using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SFClientRecon
{
    // "Display ping" (a player's own Game Settings option, on in DSF comp). Stock
    // shows "Name : N ms" for each remote player in the player list
    // (OnlinePlayerUI → ConnectedClientData.Ping, from mPingInMs). Stock measures
    // that peer to peer; on our server players only talk to the server, so the
    // server sends every player's RTT to it every 2 s (msgType 45: [u8 count] +
    // count x [u8 slot][u16 ms]) and we write it into mPingInMs. The stock UI and
    // the player's own setting do the rest.
    public partial class Plugin
    {
        private const byte PktPlayerPings = 45;
        private static volatile Dictionary<int, int> _serverPings;   // slot -> ms (set on the RX thread)
        private static Type _mmTypePg, _ccdTypePg;
        private static FieldInfo _ccArrField, _pingMsField;
        private static UnityEngine.Object _mmPg;
        private static bool _pingReflTried;
        private static int _pingLogs;

        // RX thread: parse only, no Unity calls.
        private static void HandlePlayerPingsPacket(byte[] pkt)
        {
            int off = 5, len = pkt.Length - 14;
            if (len < 1) return;
            int count = pkt[off];
            if (count <= 0 || count > 4 || len < 1 + count * 3) return;
            var map = new Dictionary<int, int>();
            for (int i = 0; i < count; i++)
            {
                int b = off + 1 + i * 3;
                map[pkt[b]] = pkt[b + 1] | (pkt[b + 2] << 8);
            }
            _serverPings = map;
        }

        // Main thread (Update).
        private void ApplyServerPings()
        {
            var map = _serverPings;
            if (map == null) return;
            _serverPings = null;
            if (!_pingReflTried)
            {
                _pingReflTried = true;
                _mmTypePg = CachedType("MultiplayerManager");
                _ccdTypePg = CachedType("ConnectedClientData");
                if (RefOk(_mmTypePg)) _ccArrField = AccessTools.Field(_mmTypePg, "mConnectedClients");
                if (RefOk(_ccdTypePg)) _pingMsField = AccessTools.Field(_ccdTypePg, "mPingInMs");
                if (!RefOk(_ccArrField) || !RefOk(_pingMsField)) Log.LogWarning("[ping] MultiplayerManager.mConnectedClients / mPingInMs not found; ping display off.");
            }
            if (!RefOk(_ccArrField) || !RefOk(_pingMsField)) return;
            if (_mmPg == null) _mmPg = UnityEngine.Object.FindObjectOfType(_mmTypePg);
            if (_mmPg == null) return;
            var arr = _ccArrField.GetValue(_mmPg) as Array;
            if (arr == null) return;
            foreach (var kv in map)
            {
                if (kv.Key < 0 || kv.Key >= arr.Length) continue;
                var ccd = arr.GetValue(kv.Key);
                if (ccd == null) continue;
                _pingMsField.SetValue(ccd, (float)kv.Value);
            }
            if (_pingLogs++ < 3) Log.LogInfo("[ping] server pings applied: " + string.Join(", ", FormatPings(map)));
        }

        private static string[] FormatPings(Dictionary<int, int> map)
        {
            var parts = new List<string>();
            foreach (var kv in map) parts.Add("slot " + kv.Key + "=" + kv.Value + "ms");
            return parts.ToArray();
        }
    }
}
