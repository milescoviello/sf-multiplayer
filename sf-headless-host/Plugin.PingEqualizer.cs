using System;
using System.Collections.Generic;
using System.Net;
using UnityEngine;

namespace SFHeadlessHost
{
    // COMP ping equalizer.
    //
    // Player against player, the server relay is already even: I see you with
    // your upload delay plus my download delay, you see me with mine plus yours,
    // and both sums are the same. Ping only gives an edge on what the SERVER
    // decides and sends: when the round starts, when a weapon drops, and who gets
    // a contested weapon first. So in COMP the lower-ping player gets those three
    // held back by half the ping gap (capped), and their pickup requests are held
    // by the same amount, as if both players had the higher ping:
    //   - WeaponSpawned, WeaponWasPickedUp and StartMatch to them leave later;
    //   - once something is held for a player, everything after it to that player
    //     queues behind it (nothing overtakes: no pickup before its spawn);
    //   - their ClientRequestingWeaponPickUp is handled later, so a race for the
    //     same weapon is decided as if both pings were equal.
    // Movement, hits and crates are untouched.
    //
    // SF_PING_EQUALIZE=0 turns it off; SF_PING_EQUALIZE_MAX_MS caps the hold
    // (default 100 ms one way). Main thread only (the receive thread relays
    // PlayerUpdate itself and never comes through SendSfPacket).
    public partial class Plugin
    {
        private static bool PingEqualize => CompMode && Environment.GetEnvironmentVariable("SF_PING_EQUALIZE") != "0";

        private static float _peMaxSec = -1f;
        private static float PeMaxSec
        {
            get
            {
                if (_peMaxSec < 0f)
                {
                    float v;
                    _peMaxSec = float.TryParse(Environment.GetEnvironmentVariable("SF_PING_EQUALIZE_MAX_MS"), out v) && v >= 0f ? v / 1000f : 0.1f;
                }
                return _peMaxSec;
            }
        }

        private struct HeldPacket { public float Due; public IPEndPoint To; public byte[] Pkt; }
        private struct HeldPickup { public float Due; public SfClient From; public byte[] Body; }

        private readonly Dictionary<SfClient, Queue<HeldPacket>> _peQueues = new Dictionary<SfClient, Queue<HeldPacket>>();
        private readonly Dictionary<SfClient, float> _peLastDue = new Dictionary<SfClient, float>();
        private readonly List<HeldPickup> _peHeldPickups = new List<HeldPickup>();
        private static int _peMainThread = -1;
        // Per-round counts, logged as one line when the next round starts.
        private readonly Dictionary<int, int> _peHeldCount = new Dictionary<int, int>();
        private readonly Dictionary<int, float> _peHeldMs = new Dictionary<int, float>();
        private readonly Dictionary<int, int> _pePickupsHeld = new Dictionary<int, int>();

        private static bool PeEvenlyTimed(byte msgType)
        {
            return msgType == PktWeaponSpawned || msgType == PktWeaponWasPickedUp || msgType == PktStartMatch;
        }

        // One-way hold for this player: half the gap between their ping and the
        // highest ping among the players in the match (0 if they have the highest).
        private float PeHoldSec(SfClient c)
        {
            if (!PingEqualize || c == null || !IsParticipant(c) || c.RttMs <= 0f) return 0f;
            float max = 0f;
            int n = 0;
            foreach (var kv in _sfClients)
            {
                var o = kv.Value;
                if (!IsParticipant(o) || o.RttMs <= 0f) continue;
                n++;
                if (o.RttMs > max) max = o.RttMs;
            }
            if (n < 2) return 0f;
            float d = (max - c.RttMs) / 2000f;
            return d <= 0f ? 0f : Mathf.Min(d, PeMaxSec);
        }

        // From SendSfPacket, with the finished packet. True: held, sent later.
        private bool PeMaybeHold(IPEndPoint to, byte msgType, byte[] pkt)
        {
            if (!PingEqualize) return false;
            if (_peMainThread < 0) _peMainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != _peMainThread) return false;
            SfClient c;
            if (!_sfClients.TryGetValue(to.ToString(), out c)) return false;   // v26 sockets: not held
            Queue<HeldPacket> q;
            _peQueues.TryGetValue(c, out q);
            bool behind = q != null && q.Count > 0;
            float hold = PeEvenlyTimed(msgType) ? PeHoldSec(c) : 0f;
            if (hold <= 0f && !behind) return false;
            if (q == null) { q = new Queue<HeldPacket>(); _peQueues[c] = q; }
            float now = Time.realtimeSinceStartup;
            float due = now + hold;
            float last;
            if (_peLastDue.TryGetValue(c, out last) && last > due) due = last;   // never overtake
            _peLastDue[c] = due;
            q.Enqueue(new HeldPacket { Due = due, To = to, Pkt = pkt });
            if (hold > 0f)
            {
                if (msgType == PktStartMatch)
                {
                    PeLogRoundSummary();
                    Log.LogInfo($"[ping-eq] slot {c.Slot}: round start held {hold * 1000f:0} ms (rtt {c.RttMs:0} ms vs the highest in the match)");
                }
                else
                {
                    int n; _peHeldCount.TryGetValue(c.Slot, out n); _peHeldCount[c.Slot] = n + 1;
                    float ms; _peHeldMs.TryGetValue(c.Slot, out ms); _peHeldMs[c.Slot] = ms + hold * 1000f;
                }
            }
            return true;
        }

        // From the pickup dispatch. True: held, handled later.
        private bool PeMaybeHoldPickup(SfClient cli, byte[] data, int off, int len)
        {
            float hold = PeHoldSec(cli);
            if (hold <= 0f) return false;
            var body = new byte[len];
            Buffer.BlockCopy(data, off, body, 0, len);
            _peHeldPickups.Add(new HeldPickup { Due = Time.realtimeSinceStartup + hold, From = cli, Body = body });
            int n; _pePickupsHeld.TryGetValue(cli.Slot, out n); _pePickupsHeld[cli.Slot] = n + 1;
            return true;
        }

        // Every frame from the main loop.
        private void TickPingEqualizer()
        {
            if (_peQueues.Count == 0 && _peHeldPickups.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            List<SfClient> drop = null;
            foreach (var kv in _peQueues)
            {
                var q = kv.Value;
                bool gone = !_sfClients.ContainsValue(kv.Key);
                while (q.Count > 0 && (gone || q.Peek().Due <= now))
                {
                    var h = q.Dequeue();
                    if (gone) continue;
                    try { _sfServer.Send(h.Pkt, h.Pkt.Length, h.To); _sfPacketsTx++; }
                    catch (Exception e) { if (Verbose) Log.LogDebug($"[ping-eq] send: {e.Message}"); }
                }
                if (q.Count == 0) { if (drop == null) drop = new List<SfClient>(); drop.Add(kv.Key); }
            }
            if (drop != null) foreach (var c in drop) { _peQueues.Remove(c); _peLastDue.Remove(c); }
            for (int i = 0; i < _peHeldPickups.Count; )
            {
                var p = _peHeldPickups[i];
                if (p.Due > now) { i++; continue; }
                _peHeldPickups.RemoveAt(i);
                if (!_sfClients.ContainsValue(p.From)) continue;
                try { HandlePickupRequest(p.From, p.Body, 0, p.Body.Length); }
                catch (Exception e) { Log.LogWarning($"[ping-eq] held pickup: {e.Message}"); }
            }
        }

        // One line for the round that just ended (called when the next starts).
        private void PeLogRoundSummary()
        {
            if (_peHeldCount.Count == 0 && _pePickupsHeld.Count == 0) return;
            var sb = new System.Text.StringBuilder("[ping-eq] last round:");
            var slots = new List<int>();
            foreach (var k in _peHeldCount.Keys) if (!slots.Contains(k)) slots.Add(k);
            foreach (var k in _pePickupsHeld.Keys) if (!slots.Contains(k)) slots.Add(k);
            slots.Sort();
            foreach (var slot in slots)
            {
                int n, p; float ms;
                _peHeldCount.TryGetValue(slot, out n); _peHeldMs.TryGetValue(slot, out ms); _pePickupsHeld.TryGetValue(slot, out p);
                sb.Append($" slot {slot}: {n} weapon event(s) held{(n > 0 ? $" (avg {ms / n:0} ms)" : "")}, {p} pickup request(s) held;");
            }
            Log.LogInfo(sb.ToString());
            _peHeldCount.Clear(); _peHeldMs.Clear(); _pePickupsHeld.Clear();
        }

        private static string MsgName(byte t)
        {
            if (t == PktWeaponSpawned) return "weapon spawn";
            if (t == PktWeaponWasPickedUp) return "pickup result";
            if (t == PktStartMatch) return "round start";
            return "msgType " + t;
        }
    }
}
