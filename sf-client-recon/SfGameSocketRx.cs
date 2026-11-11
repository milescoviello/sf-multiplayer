using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace SFClientRecon
{
    // Three fixes to how the game client tracks other players' packets and
    // bodies.
    //
    // 1. The game socket's receive loop (patched Assembly-CSharp,
    //    P2PPackageHandler.UDPReceive) re-arms BeginReceiveFrom into the same
    //    buffer BEFORE copying the datagram out, then adds the copy to a List
    //    the main thread reads without a lock. Two datagrams arriving back to
    //    back (the server sends ClientJoined + ClientSpawned, snapshots, damage
    //    in bursts) overwrite each other: seen live as a 9-byte ClientJoined
    //    turned into a "ClientSpawned" with the next packet's header, the real
    //    ClientJoined lost, and the other player's slot left broken. Init()
    //    also starts a new chain every time it runs (each online-box entry).
    //    Here: one thread per socket, copy first, queue under a lock.
    //
    // 2. A player who leaves (or crashes and rejoins) left their body on
    //    everyone else's screen: the server never said they had gone (stock
    //    learns it from the Steam lobby). The server now sends msgType 47
    //    [u8 slot][u64 steamId]; we do what stock RemoveClientFromList does
    //    for that player (KillPlayer, drop from the player list, destroy the
    //    body).
    //
    // 3. OnPlayerSpawned always creates a new body. A second ClientSpawned for
    //    a slot that already has one (rejoin, or a roster dummy followed by the
    //    spawn) left two bodies for one player. The old one is removed first.
    public partial class Plugin
    {
        private const byte PktPlayerLeft = 47;
        private static FieldInfo _p2pSocketF, _p2pChannelsF, _p2pPauseF, _p2pSentRecvF;
        private static readonly object _gameRxLock = new object();
        private static Socket _gameRxSocket;
        private static Thread _gameRxThread;
        private static int _gameRxErrors, _gameRxBadLen;

        private void InstallGameSocketFixes()
        {
            try
            {
                var t = CachedType("P2PPackageHandler");
                if (!RefOk(t)) return;
                var udpRecv = AccessTools.Method(t, "UDPReceive");
                var isAvail = AccessTools.Method(t, "IsPacketAvailable");
                var readPkt = AccessTools.Method(t, "ReadPacket");
                _p2pSocketF = AccessTools.Field(t, "_socket");
                _p2pChannelsF = AccessTools.Field(t, "channels");
                _p2pPauseF = AccessTools.Field(t, "mPauseTraffic");
                _p2pSentRecvF = AccessTools.Field(t, "mHasSentOrReceived");
                var h = new Harmony(PluginGuid + ".game-socket-rx");
                if (RefOk(udpRecv) && RefOk(isAvail) && RefOk(readPkt) && RefOk(_p2pSocketF) && RefOk(_p2pChannelsF))
                {
                    h.Patch(udpRecv, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(UDPReceive_Prefix))));
                    h.Patch(isAvail, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(IsPacketAvailable_Prefix))));
                    h.Patch(readPkt, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(ReadPacket_Prefix))));
                    Log.LogInfo("[game-rx] Game socket receive replaced (copy before re-arm, locked queue).");
                }
                else Log.LogWarning("[game-rx] P2PPackageHandler receive members not found; stock receive kept.");

                var mmT = CachedType("MultiplayerManager");
                var onSpawned = RefOk(mmT) ? AccessTools.Method(mmT, "OnPlayerSpawned") : null;
                if (RefOk(onSpawned))
                {
                    h.Patch(onSpawned, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(OnPlayerSpawned_DedupPrefix))));
                    Log.LogInfo("[players] Patched OnPlayerSpawned (one body per player).");
                }
            }
            catch (Exception e) { Log.LogWarning($"[game-rx] install failed: {e.Message}"); }
        }

        internal static bool UDPReceive_Prefix(object __instance)
        {
            try
            {
                if (RefOk(_p2pPauseF) && (bool)_p2pPauseF.GetValue(__instance)) return true;   // stock returns at once
                var sock = _p2pSocketF.GetValue(__instance) as Socket;
                if (sock == null || !RefOk(_p2pChannelsF.GetValue(__instance))) return true;
                if (ReferenceEquals(sock, _gameRxSocket) && _gameRxThread != null && _gameRxThread.IsAlive) return false;   // already receiving
                _gameRxSocket = sock;
                var th = new Thread(new ParameterizedThreadStart(GameRxLoop));
                th.IsBackground = true;
                th.Name = "SF-game-rx";
                th.Start(new object[] { sock, __instance });
                _gameRxThread = th;
                Log.LogInfo("[game-rx] receive thread started.");
                return false;
            }
            catch (Exception e) { Log.LogWarning($"[game-rx] start: {e.Message}"); return true; }
        }

        private static void GameRxLoop(object arg)
        {
            var a = (object[])arg;
            var sock = (Socket)a[0];
            var handler = a[1];
            var buf = new byte[8192];
            while (true)
            {
                int n;
                try { n = sock.Receive(buf); }
                catch (ObjectDisposedException) { return; }
                catch (Exception e)
                {
                    // An ICMP port-unreachable (server restarting) surfaces as a
                    // SocketException on Windows; keep listening.
                    if (!ReferenceEquals(sock, _gameRxSocket)) return;
                    if (_gameRxErrors++ < 3) Log.LogWarning($"[game-rx] {e.Message} (continuing)");
                    Thread.Sleep(5);
                    continue;
                }
                if (n < 14) { _gameRxBadLen++; continue; }   // 5-byte header + 9-byte trailer minimum
                var pkt = new byte[n];
                Buffer.BlockCopy(buf, 0, pkt, 0, n);
                RxTiming.OnPacket(pkt, n);
                NoteSnakeTargetPacket(pkt, n);
                int ch = pkt[n - 1];
                List<byte[]>[] channels;
                try { channels = _p2pChannelsF.GetValue(handler) as List<byte[]>[]; } catch { continue; }
                if (channels == null || ch >= channels.Length) continue;
                Monitor.Enter(_gameRxLock);
                try
                {
                    var list = channels[ch];
                    if (list != null) list.Add(pkt);
                }
                finally { Monitor.Exit(_gameRxLock); }
            }
        }

        internal static bool IsPacketAvailable_Prefix(object __instance, int channel, ref bool __result)
        {
            var channels = _p2pChannelsF.GetValue(__instance) as List<byte[]>[];
            if (channels == null || channel < 0 || channel >= channels.Length || channels[channel] == null) { __result = false; return false; }
            Monitor.Enter(_gameRxLock);
            try { __result = channels[channel].Count != 0; }
            finally { Monitor.Exit(_gameRxLock); }
            return false;
        }

        internal static bool ReadPacket_Prefix(object __instance, int channel, ref byte[] __result)
        {
            var channels = _p2pChannelsF.GetValue(__instance) as List<byte[]>[];
            __result = null;
            if (channels != null && channel >= 0 && channel < channels.Length && channels[channel] != null)
            {
                Monitor.Enter(_gameRxLock);
                try
                {
                    var list = channels[channel];
                    if (list.Count > 0) { __result = list[0]; list.RemoveAt(0); }
                }
                finally { Monitor.Exit(_gameRxLock); }
            }
            if (RefOk(_p2pSentRecvF)) _p2pSentRecvF.SetValue(__instance, true);
            if (__result != null && _killPredicted.Count > 0) KillTiming_NotePacket(channel, __result);
            return false;
        }

        // ---------- one body per player ----------
        private static FieldInfo _mmConnectedF, _mmPlayersF, _ccdPlayerObjF, _ccdClientIdF, _mmUnassignedF, _mmGameManagerF, _ctrlPlayerIdF2;
        private static MethodInfo _gmKillPlayerM;

        private static bool PlayersReflect(object mm)
        {
            if (!RefOk(mm)) return false;
            if (!RefOk(_mmConnectedF))
            {
                var mmT = mm.GetType();
                _mmConnectedF = AccessTools.Field(mmT, "mConnectedClients");
                _mmPlayersF = AccessTools.Field(mmT, "m_Players");
                _mmUnassignedF = AccessTools.Field(mmT, "mUnassignedID");
                _mmGameManagerF = AccessTools.Field(mmT, "mGameManager");
                var ccdT = CachedType("ConnectedClientData");
                if (RefOk(ccdT)) { _ccdPlayerObjF = AccessTools.Field(ccdT, "PlayerObject"); _ccdClientIdF = AccessTools.Field(ccdT, "ClientID"); }
                var ctrlT = CachedType("Controller");
                if (RefOk(ctrlT)) _ctrlPlayerIdF2 = AccessTools.Field(ctrlT, "playerID");
                var gmT = CachedType("GameManager");
                if (RefOk(gmT)) _gmKillPlayerM = AccessTools.Method(gmT, "KillPlayer");
            }
            return RefOk(_mmConnectedF) && RefOk(_mmPlayersF) && RefOk(_ccdPlayerObjF) && RefOk(_ctrlPlayerIdF2);
        }

        // Remove every body this client has for `slot`. killFirst: what stock
        // does when a player leaves (GameManager.KillPlayer, so the alive list
        // and stats stay right); a duplicate about to be replaced just goes.
        private static int RemovePlayerBodies(object mm, int slot, bool killFirst)
        {
            int removed = 0;
            var players = _mmPlayersF.GetValue(mm) as System.Collections.IList;
            var bodies = new List<GameObject>();
            var arr = _mmConnectedF.GetValue(mm) as Array;
            if (arr != null && slot >= 0 && slot < arr.Length)
            {
                var ccd = arr.GetValue(slot);
                var po = RefOk(ccd) ? _ccdPlayerObjF.GetValue(ccd) as GameObject : null;
                if (po != null) bodies.Add(po);
                if (RefOk(ccd)) _ccdPlayerObjF.SetValue(ccd, null);
            }
            if (players != null)
            {
                for (int i = players.Count - 1; i >= 0; i--)
                {
                    var c = players[i] as Component;
                    if (c == null) { players.RemoveAt(i); continue; }
                    if ((int)_ctrlPlayerIdF2.GetValue(c) != slot) continue;
                    if (!bodies.Contains(c.gameObject)) bodies.Add(c.gameObject);
                }
            }
            var gm = RefOk(_mmGameManagerF) ? _mmGameManagerF.GetValue(mm) : null;
            foreach (var go in bodies)
            {
                var ctrl = go.GetComponent(CachedType("Controller"));
                if (killFirst && RefOk(gm) && RefOk(_gmKillPlayerM) && RefOk(ctrl))
                {
                    try { _gmKillPlayerM.Invoke(gm, new object[] { ctrl }); } catch { }
                }
                if (players != null && RefOk(ctrl)) players.Remove(ctrl);
                UnityEngine.Object.Destroy(go);
                removed++;
            }
            return removed;
        }

        internal static void OnPlayerSpawned_DedupPrefix(object __instance, byte[] data)
        {
            try
            {
                if (data == null || data.Length < 1 || !PlayersReflect(__instance)) return;
                int slot = data[0];
                int n = RemovePlayerBodies(__instance, slot, killFirst: false);
                if (n > 0) Log.LogInfo($"[players] slot {slot} spawned again: removed {n} old body(ies) first.");
            }
            catch (Exception e) { Log.LogWarning($"[players] dedup: {e.Message}"); }
        }

        // msgType 47 from the server (RX thread): queue for the main thread.
        private static readonly List<KeyValuePair<int, ulong>> _playersLeft = new List<KeyValuePair<int, ulong>>();
        private static void HandlePlayerLeftPacket(byte[] pkt)
        {
            if (pkt.Length - 14 < 9) return;
            int slot = pkt[5];
            ulong sid = 0;
            for (int i = 0; i < 8; i++) sid |= (ulong)pkt[6 + i] << (8 * i);
            Monitor.Enter(_playersLeft);
            try { _playersLeft.Add(new KeyValuePair<int, ulong>(slot, sid)); }
            finally { Monitor.Exit(_playersLeft); }
        }

        private void ApplyPlayersLeft()
        {
            if (_playersLeft.Count == 0) return;
            KeyValuePair<int, ulong>[] items;
            Monitor.Enter(_playersLeft);
            try { items = _playersLeft.ToArray(); _playersLeft.Clear(); }
            finally { Monitor.Exit(_playersLeft); }
            var mmT = CachedType("MultiplayerManager");
            var mm = RefOk(mmT) ? UnityEngine.Object.FindObjectOfType(mmT) : null;
            if (!PlayersReflect(mm)) return;
            foreach (var it in items)
            {
                var arr = _mmConnectedF.GetValue(mm) as Array;
                if (arr == null || it.Key < 0 || it.Key >= arr.Length) continue;
                var ccd = arr.GetValue(it.Key);
                // Only the player the server named: a rejoin already re-filled the slot.
                if (RefOk(ccd) && RefOk(_ccdClientIdF))
                {
                    var idObj = _ccdClientIdF.GetValue(ccd);
                    var sidF = RefOk(idObj) ? AccessTools.Field(idObj.GetType(), "m_SteamID") : null;
                    if (RefOk(sidF) && (ulong)sidF.GetValue(idObj) != it.Value) continue;
                }
                int n = RemovePlayerBodies(mm, it.Key, killFirst: true);
                if (RefOk(ccd) && RefOk(_ccdClientIdF) && RefOk(_mmUnassignedF))
                    _ccdClientIdF.SetValue(ccd, _mmUnassignedF.GetValue(mm));   // stock: ClientID = mUnassignedID
                try
                {
                    var wc = UnityEngine.Object.FindObjectOfType(CachedType("WinCounterUI"));
                    var refresh = RefOk(wc) ? AccessTools.Method(wc.GetType(), "RefreshWinTexts") : null;
                    if (RefOk(refresh)) refresh.Invoke(wc, null);
                }
                catch { }
                Log.LogInfo($"[players] slot {it.Key} left: removed {n} body(ies).");
            }
        }
    }
}
