using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SFClientRecon
{
    // Spectators and replay viewers have no body. Two things follow:
    //  - The server learns where to send our v26 traffic (world snapshots,
    //    banners, pings) from PlayerInput, which a bodyless client never sends,
    //    and behind the router its fallback address is wrong. So every 2 s we
    //    send msgType 46 "viewer hello" from this socket: [u64 steamId][u8 op][u8 arg].
    //  - Chat lives on the player's own character, so there is no chat box.
    //    In the REPLAY lobby the same message carries the controls:
    //    1-9 = watch replay n, R = restart it, L = list (the server answers
    //    with the top banner). Ops: 0 hello, 1 list, 2 play n, 3 restart.
    public partial class Plugin
    {
        private const byte PktViewerHello = 46;
        private float _nextViewerHelloAt;
        private static ulong _ownSteamId;
        private static float _steamIdNextTry;   // retry while 0: Steam may not be up on the first call

        // msgType 48: tell the server our version every 5 s, so it can ask an
        // outdated client to update (COMP requires the current one).
        private const byte PktClientVersion = 48;
        private float _nextVersionAt;
        private void TickClientVersion()
        {
            if (_socket == null || _serverEp == null || Time.unscaledTime < _nextVersionAt) return;
            uint tok = _sessionToken;
            _nextVersionAt = Time.unscaledTime + (tok != 0 ? 2f : 5f);
            if (!InitializedFromServer()) return;
            ulong sid = OwnSteamId();
            if (sid == 0) return;
            byte[] v = System.Text.Encoding.ASCII.GetBytes(PluginVersion);
            byte[] pkt = new byte[5 + 9 + v.Length + (tok != 0 ? 4 : 0) + 9];
            if (tok != 0) for (int i = 0; i < 4; i++) pkt[14 + v.Length + i] = (byte)(tok >> (8 * i));   // binds this socket to our slot
            WriteU32LE(pkt, 0, (uint)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds);
            pkt[4] = PktClientVersion;
            for (int i = 0; i < 8; i++) pkt[5 + i] = (byte)(sid >> (8 * i));
            pkt[13] = (byte)v.Length;
            Buffer.BlockCopy(v, 0, pkt, 14, v.Length);
            try { _socket.Send(pkt, pkt.Length, _serverEp); } catch { }
        }

        private void TickViewerLink()
        {
            TickClientVersion();
            if (_socket == null || _serverEp == null) return;
            byte op = 0, arg = 0;
            for (int n = 1; n <= 9; n++)
                if (Input.GetKeyDown(KeyCode.Alpha0 + n) || Input.GetKeyDown(KeyCode.Keypad0 + n)) { op = 2; arg = (byte)n; }
            if (Input.GetKeyDown(KeyCode.R)) op = 3;
            if (Input.GetKeyDown(KeyCode.L)) op = 1;
            if (op == 0 && Time.unscaledTime < _nextViewerHelloAt) return;
            _nextViewerHelloAt = Time.unscaledTime + 2f;
            if (!InitializedFromServer()) return;
            if (HasLocalBody()) return;   // a player: PlayerInput tells the server, and chat works
            ulong sid = OwnSteamId();
            if (sid == 0) return;
            byte[] pkt = new byte[5 + 10 + 9];
            uint ts = (uint)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            WriteU32LE(pkt, 0, ts);
            pkt[4] = PktViewerHello;
            for (int i = 0; i < 8; i++) pkt[5 + i] = (byte)(sid >> (8 * i));
            pkt[13] = op; pkt[14] = arg;
            try { _socket.Send(pkt, pkt.Length, _serverEp); }
            catch (Exception e) { Log.LogWarning($"[viewer] send: {e.Message}"); }
        }

        // A spectator / viewer knows its slot (the patched DLL sets
        // mLocalPlayerIndex) but has no character: no NetworkPlayer with
        // mHasLocalControl. Checked at most every 2 s or on a key press.
        private static FieldInfo _npLocalCtlF;
        private static bool HasLocalBody()
        {
            var npT = CachedType("NetworkPlayer");
            if (!RefOk(npT)) return false;
            if (!RefOk(_npLocalCtlF)) _npLocalCtlF = AccessTools.Field(npT, "mHasLocalControl");
            if (!RefOk(_npLocalCtlF)) return false;
            foreach (var np in UnityEngine.Object.FindObjectsOfType(npT))
            {
                try
                {
                    if (!(bool)_npLocalCtlF.GetValue(np)) continue;
                    LogLocalControlOnce(np as Component);
                    return true;
                }
                catch { }
            }
            return false;
        }

        // Diagnostic: stock gates local-only effects (a snake's bite, some
        // breakables) on the victim's Controller.HasControl; log it once per body.
        private static int _loggedCtlFor = -1;
        private static void LogLocalControlOnce(Component np)
        {
            if (np == null || np.GetInstanceID() == _loggedCtlFor) return;
            _loggedCtlFor = np.GetInstanceID();
            var ctlT = CachedType("Controller");
            var ctl = RefOk(ctlT) ? np.GetComponent(ctlT) : null;
            var f = RefOk(ctlT) ? AccessTools.Field(ctlT, "mHasControl") : null;
            Log.LogInfo($"[diag] local body: Controller.mHasControl={(RefOk(ctl) && RefOk(f) ? f.GetValue(ctl) : "?")}");
        }

        private static bool InitializedFromServer()
        {
            try
            {
                if (!RefOk(_mmType)) _mmType = CachedType("MultiplayerManager");
                if (!RefOk(_mmType)) return false;
                if (_mmCached == null) _mmCached = UnityEngine.Object.FindObjectOfType(_mmType);
                if (_mmCached == null) return false;
                if (!RefOk(_initFromServerF)) _initFromServerF = AccessTools.Field(_mmType, "mHasBeenInitializedFromServer");
                return RefOk(_initFromServerF) && (bool)_initFromServerF.GetValue(_mmCached);
            }
            catch { return false; }
        }

        private static ulong OwnSteamId()
        {
            if (_ownSteamId != 0 || Time.realtimeSinceStartup < _steamIdNextTry) return _ownSteamId;
            _steamIdNextTry = Time.realtimeSinceStartup + 5f;
            try
            {
                var su = CachedType("Steamworks.SteamUser");
                var get = RefOk(su) ? AccessTools.Method(su, "GetSteamID") : null;
                object id = RefOk(get) ? get.Invoke(null, null) : null;
                var f = RefOk(id) ? AccessTools.Field(id.GetType(), "m_SteamID") : null;
                if (RefOk(f)) _ownSteamId = (ulong)f.GetValue(id);
            }
            catch (Exception e) { Log.LogWarning($"[viewer] SteamID: {e.Message}"); }
            return _ownSteamId;
        }
    }
}
