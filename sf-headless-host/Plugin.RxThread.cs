using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace SFHeadlessHost
{
    // Game socket receive on its own thread.
    //
    // The main thread used to read the socket once per frame: at the server's
    // 40-90 fps every packet waited up to a frame (10-25 ms) and the packets
    // that arrived in between went out together, so other players moved in
    // uneven steps (clients send PlayerUpdate 50 times a second). Now:
    //  - PlayerUpdate is relayed to the other players the moment it arrives
    //    (only from a joined, non-spectating player, and only on that player's
    //    own channel: slot*2+2; the old relay took any channel, so a modified
    //    client could move another player on everyone's screen);
    //  - every packet is stamped with its arrival time; RTT and death times use
    //    it (a death report waiting for the next frame used to look later than
    //    it was, which can decide a trade);
    //  - everything (PlayerUpdate included, for the server's copy of the
    //    player) is queued for the main thread, which handles it as before.
    // SF_FAST_RELAY=0: no relay on this thread (the main thread relays).
    public partial class Plugin
    {
        private struct RxItem { public byte[] Data; public IPEndPoint From; public double At; public bool Relayed; }
        private readonly Queue<RxItem> _rxQueue = new Queue<RxItem>();
        private readonly object _rxLock = new object();
        private Thread _rxThread;
        private volatile bool _rxRun;
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private float _clockOffset;          // realtimeSinceStartup - _clock, set by the main thread
        private float _rxNow = -1f;          // arrival time of the packet being handled
        private bool _rxRelayed;             // that packet was already relayed by the receive thread
        private int _rxDropped, _fastRelayed, _fastRefused;
        private static readonly bool FastRelay = Environment.GetEnvironmentVariable("SF_FAST_RELAY") != "0";

        // Arrival time (realtimeSinceStartup) of the packet being handled; now otherwise.
        internal float RxNow { get { return _rxNow >= 0f ? _rxNow : Time.realtimeSinceStartup; } }

        private class RelaySender { public int Slot; public ulong SteamID; }
        private class RelaySnap
        {
            public Dictionary<string, RelaySender> Senders = new Dictionary<string, RelaySender>();
            public List<KeyValuePair<string, IPEndPoint>> Targets = new List<KeyValuePair<string, IPEndPoint>>();
        }
        private volatile RelaySnap _relaySnap;
        private int _relaySig = int.MinValue;

        private void StartRxThread()
        {
            try
            {
                _sfServer.Client.Blocking = true;
                _sfServer.Client.ReceiveTimeout = 500;
                _rxRun = true;
                _rxThread = new Thread(new ThreadStart(RxLoop));
                _rxThread.IsBackground = true;
                _rxThread.Name = "sf-game-rx";
                _rxThread.Start();
                Log.LogInfo($"[rx] receive thread started (fast PlayerUpdate relay {(FastRelay ? "on" : "off")}).");
            }
            catch (Exception e)
            {
                _rxRun = false;
                try { _sfServer.Client.Blocking = false; } catch { }
                Log.LogWarning($"[rx] receive thread failed ({e.Message}); reading on the main thread.");
            }
        }

        private void RxLoop()
        {
            while (_rxRun)
            {
                byte[] d;
                IPEndPoint from = null;
                try { d = _sfServer.Receive(ref from); }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { continue; }     // timeout, or an ICMP reset from a client that went away
                catch (Exception) { continue; }
                if (d == null || d.Length < 14 || from == null) continue;
                var item = new RxItem { Data = d, From = from, At = _clock.Elapsed.TotalSeconds };
                if (FastRelay && d[4] == PktPlayerUpdate)
                {
                    try { item.Relayed = TryFastRelay(d, from); } catch { item.Relayed = false; }
                }
                Monitor.Enter(_rxLock);
                try
                {
                    if (_rxQueue.Count >= 8192) { _rxQueue.Dequeue(); _rxDropped++; }
                    _rxQueue.Enqueue(item);
                }
                finally { Monitor.Exit(_rxLock); }
            }
        }

        // Same packet SendSfPacket would build: timestamp, type, body, the
        // SENDER's steamId, channel.
        private bool TryFastRelay(byte[] d, IPEndPoint from)
        {
            var snap = _relaySnap;
            if (snap == null) return false;
            string key = from.ToString();
            RelaySender s;
            if (!snap.Senders.TryGetValue(key, out s)) return false;
            byte channel = d[d.Length - 1];
            if (channel != (byte)(s.Slot * 2 + 2)) { _fastRefused++; return false; }
            byte[] pkt = new byte[d.Length];
            Buffer.BlockCopy(d, 0, pkt, 0, d.Length);
            uint ts = (uint)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            pkt[0] = (byte)ts; pkt[1] = (byte)(ts >> 8); pkt[2] = (byte)(ts >> 16); pkt[3] = (byte)(ts >> 24);
            int t = d.Length - 9;
            for (int i = 0; i < 8; i++) pkt[t + i] = (byte)(s.SteamID >> (8 * i));
            foreach (var target in snap.Targets)
            {
                if (target.Key == key) continue;
                try { _sfServer.Send(pkt, pkt.Length, target.Value); } catch { }
            }
            _fastRelayed++;
            return true;
        }

        // Main thread, every frame: refresh who may relay to whom when the
        // client table changed.
        private void UpdateRelaySnapshot()
        {
            int sig = _sfClients.Count;
            foreach (var kv in _sfClients)
            {
                var c = kv.Value;
                sig = sig * 31 + kv.Key.GetHashCode();
                sig = sig * 31 + (c.Initialized ? 1 : 0) + (c.IsSpectator ? 2 : 0) + (c.Slot + 1) * 4;
                sig = sig * 31 + (int)c.SteamID;
            }
            if (sig == _relaySig && _relaySnap != null) return;
            _relaySig = sig;
            var snap = new RelaySnap();
            foreach (var kv in _sfClients)
            {
                var c = kv.Value;
                if (!c.Initialized) continue;
                snap.Targets.Add(new KeyValuePair<string, IPEndPoint>(kv.Key, c.Addr));
                if (c.Slot >= 0 && !c.IsSpectator) snap.Senders[kv.Key] = new RelaySender { Slot = c.Slot, SteamID = c.SteamID };
            }
            _relaySnap = snap;
        }

        // Main thread: handle everything the receive thread queued.
        private void DrainRxQueue()
        {
            _clockOffset = Time.realtimeSinceStartup - (float)_clock.Elapsed.TotalSeconds;
            UpdateRelaySnapshot();
            for (int n = 0; n < 1024; n++)
            {
                RxItem item;
                Monitor.Enter(_rxLock);
                try
                {
                    if (_rxQueue.Count == 0) break;
                    item = _rxQueue.Dequeue();
                }
                finally { Monitor.Exit(_rxLock); }
                _rxNow = (float)(item.At + _clockOffset);
                _rxRelayed = item.Relayed;
                try { _sfPacketsRx++; SfDispatch(item.Data, item.From); }
                catch (Exception e) { if (Verbose) Log.LogDebug($"SF dispatch: {e.Message}"); }
                finally { _rxNow = -1f; _rxRelayed = false; }
            }
            UpdateRelaySnapshot();   // a join/leave handled just now applies to the next packets at once
        }

        private void StopRxThread() { _rxRun = false; }
    }
}
