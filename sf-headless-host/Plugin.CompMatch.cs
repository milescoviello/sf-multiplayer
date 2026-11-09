using System;
using System.Collections.Generic;
using UnityEngine;

namespace SFHeadlessHost
{
    // Fairness + the competitive match format.
    //
    // Ping: the server pings each player (stock Ping/PingResponse; the client
    // echoes any non-empty Ping body) and keeps a smoothed round-trip time.
    //
    // Round winner: decided when the round actually advances (after
    // SF_ROUND_END_DELAY), not on whichever death report arrived first. A
    // player still alive wins; if everyone is dead, the LATEST death wins,
    // with each death time corrected by that player's one-way latency (RTT/2).
    // Without the correction a lower-ping player loses every simultaneous
    // death, because their report simply arrives first. There are no draws.
    //
    // COMP lobby (SF_COMP=1): a match is SF_SETS sets (default 2) of first to
    // SF_FIRST_TO round wins (default 25), the DSF duel format; lobby wins don't
    // count. /discount removes the last point (anyone may use it), /score shows
    // the score. A player leaving mid-round voids the round and the same map is
    // replayed once both players are back; rounds only start with a full lobby.
    public partial class Plugin
    {
        internal static bool CompMode => Environment.GetEnvironmentVariable("SF_COMP") == "1";

        private static int EnvInt(string name, int def, int min, int max)
        {
            int v;
            return int.TryParse(Environment.GetEnvironmentVariable(name), out v) && v >= min && v <= max ? v : def;
        }
        internal static int FirstTo => EnvInt("SF_FIRST_TO", 25, 1, 1000);
        internal static int SetsPerMatch => EnvInt("SF_SETS", 2, 1, 9);

        private static readonly string[] SlotColor = { "Yellow", "Blue", "Red", "Green" };
        private static string SlotName(int slot) => slot >= 0 && slot < SlotColor.Length ? SlotColor[slot] : "P" + (slot + 1);

        // ---------------- ping ----------------
        private float _lastServerPingAt = -1f;
        private const byte PingMagic0 = 0x53, PingMagic1 = 0x46;   // "SF"

        private void TickServerPings()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastServerPingAt < 1f) return;
            _lastServerPingAt = now;
            uint ms = (uint)(now * 1000f);
            byte[] body = { PingMagic0, PingMagic1, (byte)ms, (byte)(ms >> 8), (byte)(ms >> 16), (byte)(ms >> 24) };
            foreach (var kv in _sfClients)
                if (kv.Value.Initialized) SendSfPacket(kv.Value.Addr, PktPing, body, kv.Value.SteamID, 0);
        }

        private void HandleServerPingResponse(SfClient cli, byte[] data, int off, int len)
        {
            if (len < 6 || data[off] != PingMagic0 || data[off + 1] != PingMagic1) return;
            uint sent = (uint)(data[off + 2] | (data[off + 3] << 8) | (data[off + 4] << 16) | (data[off + 5] << 24));
            float rtt = Time.realtimeSinceStartup * 1000f - sent;
            if (rtt < 0f || rtt > 5000f) return;
            cli.RttMs = cli.RttMs <= 0f ? rtt : cli.RttMs * 0.8f + rtt * 0.2f;
        }

        // ---------------- round winner ----------------
        private readonly Dictionary<int, float> _deathTimeComp = new Dictionary<int, float>();
        private bool _roundVoid;          // COMP: someone left mid-round → no point, replay the map
        private bool _replaySameMap;

        private void RecordDeathTime(SfClient cli)
        {
            float oneWay = cli.RttMs > 0f ? cli.RttMs / 2000f : 0f;
            _deathTimeComp[cli.Slot] = Time.realtimeSinceStartup - oneWay;
        }

        private byte DecideRoundWinner()
        {
            if (_roundVoid || _roundParticipantsAtStart < 2) return 255;
            CountAlive(out int participants, out int alive, out int lastAlive);
            if (alive == 1) return (byte)lastAlive;
            if (alive > 1) return 255;   // e.g. /next
            int best = -1; float bestT = float.MinValue;
            foreach (var kv in _deathTimeComp)
                if (kv.Value > bestT) { bestT = kv.Value; best = kv.Key; }
            return best >= 0 ? (byte)best : (byte)255;
        }

        // COMP: a participant leaving mid-round voids it.
        private void OnCompPlayerLeftMidRound()
        {
            if (!CompMode || !_matchStarted || !_startMatchSentThisRound || _pendingRoundAdvanceAt >= 0f) return;
            _roundVoid = true;
            _replaySameMap = true;
            BroadcastChatToAll("A player left: this round doesn't count and will be replayed on the same map.");
            _pendingRoundAdvanceAt = Time.realtimeSinceStartup + RoundEndDelaySec;
            Log.LogInfo("[comp] player left mid-round → round voided, same map will be replayed.");
        }

        // ---------------- COMP score ----------------
        private int _currentSet = 1;
        private readonly Dictionary<int, int> _setScore = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _setsWon = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _matchRounds = new Dictionary<int, int>();
        private readonly List<int> _pointLog = new List<int>();   // winners of this set's points, in order

        private static int Get(Dictionary<int, int> d, int k) { int v; return d.TryGetValue(k, out v) ? v : 0; }

        private string ScoreLine()
        {
            var slots = new List<int>();
            foreach (var kv in _sfClients) if (kv.Value.Initialized && kv.Value.Slot >= 0 && !slots.Contains(kv.Value.Slot)) slots.Add(kv.Value.Slot);
            foreach (var s in _setScore.Keys) if (!slots.Contains(s)) slots.Add(s);
            slots.Sort();
            var parts = new List<string>();
            foreach (var s in slots) parts.Add($"{SlotName(s)} {Get(_setScore, s)}");
            return $"Set {_currentSet}/{SetsPerMatch} (first to {FirstTo}): " + string.Join(" - ", parts.ToArray());
        }

        private void RecordRoundPoint(byte winner)
        {
            if (!CompMode || winner == 255) return;
            int w = winner;
            _setScore[w] = Get(_setScore, w) + 1;
            _matchRounds[w] = Get(_matchRounds, w) + 1;
            _pointLog.Add(w);
            Log.LogInfo($"[comp] point {SlotName(w)} → {ScoreLine()}");
            if (Get(_setScore, w) < FirstTo) { BroadcastChatToAll(ScoreLine()); return; }

            _setsWon[w] = Get(_setsWon, w) + 1;
            BroadcastChatToAll($"{SlotName(w)} wins set {_currentSet}! {ScoreLine()}");
            if (_currentSet < SetsPerMatch)
            {
                _currentSet++;
                _setScore.Clear();
                _pointLog.Clear();
                BroadcastChatToAll($"Set {_currentSet} of {SetsPerMatch} starts now: first to {FirstTo}.");
                return;
            }
            // Match over: most sets wins; on equal sets, most rounds won overall.
            int bestSlot = -1, bestSets = -1, bestRounds = -1; bool tie = false;
            foreach (var kv in _matchRounds)
            {
                int sets = Get(_setsWon, kv.Key), rounds = kv.Value;
                if (sets > bestSets || (sets == bestSets && rounds > bestRounds)) { bestSlot = kv.Key; bestSets = sets; bestRounds = rounds; tie = false; }
                else if (sets == bestSets && rounds == bestRounds) tie = true;
            }
            var summary = new List<string>();
            foreach (var kv in _matchRounds) summary.Add($"{SlotName(kv.Key)}: {Get(_setsWon, kv.Key)} set(s), {kv.Value} round(s)");
            string result = tie ? "The match is tied" : $"{SlotName(bestSlot)} wins the match";
            BroadcastChatToAll($"{result}! " + string.Join(", ", summary.ToArray()) + ". New match starting.");
            Log.LogInfo($"[comp] MATCH OVER: {result}. " + string.Join(", ", summary.ToArray()));
            ResetCompMatch();
        }

        private void ResetCompMatch()
        {
            _currentSet = 1;
            _setScore.Clear(); _setsWon.Clear(); _matchRounds.Clear(); _pointLog.Clear();
        }

        // /discount: the last point of the current set doesn't count.
        private string DiscountLastPoint(SfClient by)
        {
            if (!CompMode) return "Discounts only apply in the COMP lobby.";
            if (_pointLog.Count == 0) return "Nothing to discount in this set.";
            int w = _pointLog[_pointLog.Count - 1];
            _pointLog.RemoveAt(_pointLog.Count - 1);
            _setScore[w] = Math.Max(0, Get(_setScore, w) - 1);
            _matchRounds[w] = Math.Max(0, Get(_matchRounds, w) - 1);
            Log.LogInfo($"[comp] /discount by slot {by?.Slot}: removed a point from {SlotName(w)} → {ScoreLine()}");
            BroadcastChatToAll($"Last round discounted ({SlotName(w)} -1, by {SlotName(by?.Slot ?? -1)}). {ScoreLine()}");
            return null;
        }

        internal string ScoreAndPings()
        {
            var pings = new List<string>();
            foreach (var kv in _sfClients)
                if (kv.Value.Initialized && kv.Value.Slot >= 0)
                    pings.Add($"{SlotName(kv.Value.Slot)} {(kv.Value.RttMs > 0f ? Mathf.RoundToInt(kv.Value.RttMs) + "ms" : "?")}");
            string ping = "Ping: " + string.Join(", ", pings.ToArray());
            return CompMode ? ScoreLine() + " | " + ping : ping;
        }
    }
}
