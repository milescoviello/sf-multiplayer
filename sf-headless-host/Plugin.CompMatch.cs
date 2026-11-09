using System;
using System.Collections.Generic;
using System.Net;
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
            if ((_pingRound++ & 1) == 0) BroadcastPlayerPings();
        }

        // "Display ping": stock shows each remote player's ping beside their name
        // (OnlinePlayerUI, ConnectedClientData.Ping), measured peer to peer. Here
        // players only talk to the server, so every 2 s the server sends each
        // player's measured RTT to it: [u8 count] + count x [u8 slot][u16 ms].
        private int _pingRound;
        private void BroadcastPlayerPings()
        {
            var list = new List<SfClient>();
            foreach (var kv in _sfClients)
                if (IsParticipant(kv.Value) && kv.Value.RttMs > 0f && list.Count < 4) list.Add(kv.Value);
            if (list.Count == 0) return;
            byte[] body = new byte[1 + list.Count * 3];
            body[0] = (byte)list.Count;
            for (int i = 0; i < list.Count; i++)
            {
                int ms = Mathf.Clamp(Mathf.RoundToInt(list[i].RttMs), 0, 65535);
                body[1 + i * 3] = (byte)list[i].Slot;
                body[2 + i * 3] = (byte)ms;
                body[3 + i * 3] = (byte)(ms >> 8);
            }
            foreach (var kv in _sfClients)
            {
                if (!kv.Value.Initialized) continue;
                IPEndPoint v26Ep;
                if (!_slotV26Endpoint.TryGetValue(kv.Value.Slot, out v26Ep))
                    v26Ep = new IPEndPoint(kv.Value.Addr.Address, V26_CLIENT_PORT);
                SendSfPacket(v26Ep, PktPlayerPings, body, 0, 0);
            }
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
            Announce("A player left: this round doesn't count and will be replayed on the same map.");
            _pendingRoundAdvanceAt = Time.realtimeSinceStartup + RoundEndDelaySec;
            Log.LogInfo("[comp] player left mid-round → round voided, same map will be replayed.");
        }

        // Score / match messages: chat for the players, plus the top-of-screen
        // banner (msgType 42, drawn by SFClientRecon) for everyone. Spectators
        // get the banner only (chat to them is turned into the same banner).
        private void Announce(string text)
        {
            foreach (var kv in _sfClients)
                if (kv.Value.Initialized && !kv.Value.IsSpectator) SendChatToPlayer(kv.Value, text);
            byte[] body = System.Text.Encoding.UTF8.GetBytes(text.Length > 200 ? text.Substring(0, 200) : text);
            foreach (var kv in _sfClients)
            {
                if (!kv.Value.Initialized) continue;
                IPEndPoint v26Ep;
                if (!_slotV26Endpoint.TryGetValue(kv.Value.Slot, out v26Ep))
                    v26Ep = new IPEndPoint(kv.Value.Addr.Address, V26_CLIENT_PORT);
                SendSfPacket(v26Ep, PktV26Announce, body, 0, 0);
            }
        }

        // ---------------- COMP random spawns ----------------
        // Stock: every client moves player #k to spawnPoints[k] each round, so the
        // same player always gets the same side, which favours one player on
        // lopsided maps. COMP shuffles which stock spawn point each player gets,
        // per round; clients apply it in GameManager.MovePlayer (SFClientRecon).
        private static readonly System.Random _spawnRng = new System.Random();
        private void SendRoundSpawns()
        {
            if (!CompMode) return;
            var slots = new List<int>();
            foreach (var kv in _sfClients)
                if (IsParticipant(kv.Value) && !slots.Contains(kv.Value.Slot)) slots.Add(kv.Value.Slot);
            if (slots.Count == 0) return;
            slots.Sort();
            var spawn = new List<int>();
            for (int i = 0; i < slots.Count; i++) spawn.Add(i);
            for (int i = spawn.Count - 1; i > 0; i--) { int j = _spawnRng.Next(i + 1); int t = spawn[i]; spawn[i] = spawn[j]; spawn[j] = t; }
            byte[] body = new byte[1 + slots.Count * 2];
            body[0] = (byte)slots.Count;
            var desc = new List<string>();
            for (int i = 0; i < slots.Count; i++)
            {
                body[1 + i * 2] = (byte)slots[i];
                body[2 + i * 2] = (byte)spawn[i];
                desc.Add($"{SlotName(slots[i])}->spawn{spawn[i]}");
            }
            foreach (var kv in _sfClients)
            {
                if (!kv.Value.Initialized) continue;
                IPEndPoint v26Ep;
                if (!_slotV26Endpoint.TryGetValue(kv.Value.Slot, out v26Ep))
                    v26Ep = new IPEndPoint(kv.Value.Addr.Address, V26_CLIENT_PORT);
                SendSfPacket(v26Ep, PktRoundSpawns, body, 0, 0);
            }
            Log.LogInfo("[comp] round spawns: " + string.Join(", ", desc.ToArray()));
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
            foreach (var kv in _sfClients) if (IsParticipant(kv.Value) && !slots.Contains(kv.Value.Slot)) slots.Add(kv.Value.Slot);
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
            if (Get(_setScore, w) < FirstTo) { Announce(ScoreLine()); return; }

            _setsWon[w] = Get(_setsWon, w) + 1;
            Announce($"{SlotName(w)} wins set {_currentSet}! {ScoreLine()}");
            if (_currentSet < SetsPerMatch)
            {
                _currentSet++;
                _setScore.Clear();
                _pointLog.Clear();
                Announce($"Set {_currentSet} of {SetsPerMatch} starts now: first to {FirstTo}.");
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
            Announce($"{result}! " + string.Join(", ", summary.ToArray()) + ". New match starting.");
            Log.LogInfo($"[comp] MATCH OVER: {result}. " + string.Join(", ", summary.ToArray()));
            ResetCompMatch();
            _recordingSplitPending = true;   // one replay file per COMP match
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
            Announce($"Last round discounted ({SlotName(w)} -1, by {SlotName(by?.Slot ?? -1)}). {ScoreLine()}");
            return null;
        }

        internal string ScoreAndPings()
        {
            var pings = new List<string>();
            foreach (var kv in _sfClients)
                if (IsParticipant(kv.Value))
                    pings.Add($"{SlotName(kv.Value.Slot)} {(kv.Value.RttMs > 0f ? Mathf.RoundToInt(kv.Value.RttMs) + "ms" : "?")}");
            string ping = "Ping: " + string.Join(", ", pings.ToArray());
            return CompMode ? ScoreLine() + " | " + ping : ping;
        }
    }
}
