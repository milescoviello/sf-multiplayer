using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using UnityEngine;

namespace SFHeadlessHost
{
    // Match replays.
    //
    // RECORDING (every lobby, SF_RECORD_REPLAYS=0 turns it off): from match start
    // the server writes what a spectator would receive — every broadcast once,
    // every relayed player update, banners, spawn assignments, and world-state
    // snapshots (all of them: each carries only the objects that moved) — to
    // <SF_REPLAY_DIR>/<LOBBY>/<utc>.sfrp (".part" while recording). One file per
    // session; a new file starts at a round boundary after
    // 30 min, and each COMP match gets its own. The last 30 files per lobby
    // are kept (2 GB cap, SF_REPLAY_MAX_MB). Gzipped files play too.
    //
    // PLAYBACK (a lobby with SF_REPLAY_MODE=1, reading SF_REPLAY_SOURCE, e.g. the
    // COMP folder): every client that joins is a viewer. Its ClientInit lists the
    // recorded players, and the recording streams to it with the original
    // timing, so the real game shows the match exactly as it happened.
    //   /replays      list recordings (newest first)
    //   /play <n>     watch recording n from the start
    //   /restart      start the current one over
    //
    // File: "SFRP1\n" + header line + records [u32 ms][u8 type][u8 ch][u64 steamId][u16 len][body].
    // Header: "lobby=<code>;scene=<n>;options=<hex>;roster=<slot>:<steamId>,..."
    public partial class Plugin
    {
        internal static bool ReplayMode => Environment.GetEnvironmentVariable("SF_REPLAY_MODE") == "1";
        private static bool RecordingEnabled => !ReplayMode && Environment.GetEnvironmentVariable("SF_RECORD_REPLAYS") != "0";
        // Default: next to SF_ORACLE_ROOT (~/sf-replays). Under Wine a Unix path
        // goes through drive Z: (HOME isn't passed to the game).
        private static string ReplayRoot => WinePath(Environment.GetEnvironmentVariable("SF_REPLAY_DIR")
            ?? (Environment.GetEnvironmentVariable("SF_ORACLE_ROOT") ?? "/tmp/sf-oracle").TrimEnd('/') + "/../sf-replays");

        private static string WinePath(string p)
        {
            if (Path.DirectorySeparatorChar == '\\' && p.StartsWith("/")) p = "Z:" + p.Replace('/', '\\');
            try { return Path.GetFullPath(p); } catch { return p; }
        }
        private const int ReplaysKeptPerLobby = 30;
        private const float ReplayRotateSec = 30f * 60f;

        // v26-socket message types (everything else goes to the game socket).
        private static bool IsV26Type(byte t) => t == PktWorldStateSnapshot || t == PktV26Announce || t == PktRoundSpawns || t == PktPlayerPings;

        // Per-client handshake/one-off types that are not part of the match stream.
        private static bool IsReplayExcluded(byte t) =>
            t == PktPing || t == PktPingResponse || t == PktClientRequestingAccepting || t == PktClientAccepted
            || t == PktClientInit || t == PktClientRequestingIndex || t == PktWorkshopMapsLoaded
            || t == PktOptionsChanged || t == PktPlayerTalked;

        // ---------------- recorder ----------------
        // Raw records through a 64 KB buffered FileStream, written on the main
        // thread: no extra thread and no native zlib inside the game process.
        // A 1v1 is ~5 MB per minute (mostly world snapshots); pruning caps the total.
        private class ReplayWriter
        {
            private FileStream _out;
            public string Path;
            public float StartedAt;

            public void Open(string path, string header)
            {
                Path = path;
                byte[] h = System.Text.Encoding.UTF8.GetBytes("SFRP1\n" + header + "\n");
                _out = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 65536);
                _out.Write(h, 0, h.Length);
            }

            public void Write(byte[] rec) { _out.Write(rec, 0, rec.Length); }

            public void Close()
            {
                try { _out.Close(); } catch { }
                // Finished recordings drop the ".part" suffix; only those are listed.
                try { if (Path.EndsWith(".part")) File.Move(Path, Path.Substring(0, Path.Length - 5)); } catch { }
            }
        }

        private ReplayWriter _replay;
        private bool _recStartMatchThisMap;
        private byte _recLastType; private byte _recLastCh; private byte[] _recLastBody; private int _recLastFrame = -1;

        private void StartRecording()
        {
            if (!RecordingEnabled || _replay != null) return;
            try
            {
                string code = Environment.GetEnvironmentVariable("SF_LOBBY_CODE") ?? "LOBBY";
                string dir = Path.Combine(ReplayRoot, code);
                Directory.CreateDirectory(dir);
                // A ".part" left by an earlier run (the server was stopped mid-
                // recording) is as complete as it will get: list it.
                foreach (var part in Directory.GetFiles(dir, "*.sfrp.part"))
                    try { File.Move(part, part.Substring(0, part.Length - 5)); } catch { }
                var roster = new List<string>();
                foreach (var kv in _sfClients)
                    if (IsParticipant(kv.Value)) roster.Add($"{kv.Value.Slot}:{kv.Value.SteamID}");
                string header = $"lobby={code};scene={_currentSceneIndex};options={BitConverter.ToString(LobbyOptionsBody()).Replace("-", "")};roster={string.Join(",", roster.ToArray())}";
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"), path = Path.Combine(dir, stamp + ".sfrp.part");
                for (int n = 2; File.Exists(path) || File.Exists(path.Substring(0, path.Length - 5)); n++)
                    path = Path.Combine(dir, $"{stamp}-{n}.sfrp.part");
                var w = new ReplayWriter { StartedAt = Time.realtimeSinceStartup };
                w.Open(path, header);
                _replay = w;
                Log.LogInfo($"[replay] recording → {path} ({header})");
                PruneReplays(dir);
            }
            catch (Exception e) { Log.LogWarning($"[replay] start failed: {e.Message}"); _replay = null; }
        }

        private void OnApplicationQuit() => StopRecording("quit");

        private void StopRecording(string why)
        {
            if (_replay == null) return;
            Log.LogInfo($"[replay] recording closed ({why}): {_replay.Path}");
            _replay.Close();
            _replay = null;
        }

        // Round boundary: start a fresh file after ReplayRotateSec (the new file
        // then begins with this round's MapChange, so it plays on its own).
        private bool _recordingSplitPending;   // COMP match over: the next match gets its own file

        private void RotateRecordingIfDue()
        {
            if (_replay == null) return;
            bool old = Time.realtimeSinceStartup - _replay.StartedAt > ReplayRotateSec;
            if (!old && !_recordingSplitPending) return;
            _recordingSplitPending = false;
            StopRecording(old ? "30 min" : "match over");
            StartRecording();
        }

        // Keep the newest ReplaysKeptPerLobby files and at most SF_REPLAY_MAX_MB
        // (default 2048) per lobby.
        private static void PruneReplays(string dir)
        {
            try
            {
                long maxBytes = 2048L << 20; long mb;
                if (long.TryParse(Environment.GetEnvironmentVariable("SF_REPLAY_MAX_MB"), out mb) && mb > 0) maxBytes = mb << 20;
                var files = new List<string>(Directory.GetFiles(dir, "*.sfrp"));
                files.Sort(StringComparer.Ordinal);
                files.Reverse();   // newest first
                long total = 0;
                for (int i = 0; i < files.Count; i++)
                {
                    total += new FileInfo(files[i]).Length;
                    if (i >= ReplaysKeptPerLobby || total > maxBytes) File.Delete(files[i]);
                }

            }
            catch { }
        }

        // Called from SendSfPacket for every packet sent.
        private bool _recSuppress;   // a per-viewer message (e.g. a spectator's banner): not recorded

        private void RecordOutgoing(byte msgType, byte[] body, ulong steamID, byte channel)
        {
            var w = _replay;
            if (w == null || _recSuppress || IsReplayExcluded(msgType)) return;
            // A broadcast is sent once per client with the same body array: keep one.
            int frame = Time.frameCount;
            if (frame == _recLastFrame && msgType == _recLastType && channel == _recLastCh && ReferenceEquals(body, _recLastBody)) return;
            _recLastFrame = frame; _recLastType = msgType; _recLastCh = channel; _recLastBody = body;
            // StartMatch is re-sent to each late-ready player; a viewer must see one per map.
            if (msgType == PktMapChange) _recStartMatchThisMap = false;
            else if (msgType == PktStartMatch) { if (_recStartMatchThisMap) return; _recStartMatchThisMap = true; }
            int len = body?.Length ?? 0;
            if (len > 65535) return;
            uint ms = (uint)((Time.realtimeSinceStartup - w.StartedAt) * 1000f);
            byte[] rec = new byte[4 + 1 + 1 + 8 + 2 + len];
            WriteU32LE(rec, 0, ms);
            rec[4] = msgType; rec[5] = channel;
            WriteU64LE(rec, 6, steamID);
            rec[14] = (byte)len; rec[15] = (byte)(len >> 8);
            if (len > 0) Buffer.BlockCopy(body, 0, rec, 16, len);
            w.Write(rec);
        }

        // ---------------- playback ----------------
        private class ReplayRecord { public uint Ms; public byte Type, Ch; public ulong SteamId; public byte[] Body; }

        private class ReplayReader
        {
            private Stream _in;
            public string Header;
            public string Path;

            public bool Open(string path)
            {
                Path = path;
                var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                int b0 = fs.ReadByte(), b1 = fs.ReadByte();
                fs.Position = 0;
                _in = b0 == 0x1f && b1 == 0x8b ? (Stream)new GZipStream(fs, CompressionMode.Decompress) : fs;
                string magic = ReadLine(), header = ReadLine();
                if (magic != "SFRP1" || header == null) { Close(); return false; }
                Header = header;
                return true;
            }

            private string ReadLine()
            {
                var sb = new System.Text.StringBuilder();
                int b;
                while ((b = _in.ReadByte()) >= 0 && b != '\n') { sb.Append((char)b); if (sb.Length > 8192) return null; }
                return b < 0 && sb.Length == 0 ? null : sb.ToString();
            }

            private bool ReadFully(byte[] buf)
            {
                int off = 0;
                while (off < buf.Length) { int n = _in.Read(buf, off, buf.Length - off); if (n <= 0) return false; off += n; }
                return true;
            }

            public ReplayRecord Next()
            {
                try
                {
                    var head = new byte[16];
                    if (!ReadFully(head)) return null;
                    var r = new ReplayRecord
                    {
                        Ms = (uint)(head[0] | (head[1] << 8) | (head[2] << 16) | (head[3] << 24)),
                        Type = head[4], Ch = head[5], SteamId = BitConverter.ToUInt64(head, 6)
                    };
                    int len = head[14] | (head[15] << 8);
                    r.Body = new byte[len];
                    if (len > 0 && !ReadFully(r.Body)) return null;
                    return r;
                }
                catch { return null; }
            }

            public void Close() { try { _in?.Close(); } catch { } }

            public string Get(string key)
            {
                foreach (var part in (Header ?? "").Split(';'))
                {
                    int eq = part.IndexOf('=');
                    if (eq > 0 && part.Substring(0, eq) == key) return part.Substring(eq + 1);
                }
                return null;
            }

            // The recorded lobby's OptionsChanged body (null if missing).
            public byte[] Options()
            {
                string hex = Get("options");
                if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0) return null;
                var b = new byte[hex.Length / 2];
                for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                return b.Length == 4 ? b : null;
            }

            // slot -> steamId
            public Dictionary<int, ulong> Roster()
            {
                var map = new Dictionary<int, ulong>();
                foreach (var tok in (Get("roster") ?? "").Split(','))
                {
                    int c = tok.IndexOf(':'); int slot; ulong sid;
                    if (c > 0 && int.TryParse(tok.Substring(0, c), out slot) && ulong.TryParse(tok.Substring(c + 1), out sid)) map[slot] = sid;
                }
                return map;
            }
        }

        private class ReplayViewer
        {
            public SfClient Cli; public ReplayReader Reader; public ReplayRecord Pending; public float T0;
            public Dictionary<int, ulong> Roster;          // slot -> steamId as this viewer's client knows it
            public Dictionary<ulong, ulong> SidSwap;       // recorded steamId -> what this viewer is sent
            public bool Streaming, PlayersSpawned;
            public bool Paused, ReadySincePause; public float PauseStart;   // held at a MapChange until the viewer has loaded
            public string LastPath;
            public float BeganAt;
        }
        private readonly Dictionary<string, ReplayViewer> _viewers = new Dictionary<string, ReplayViewer>();
        private ReplayReader _replaySelected;   // set by /play: what the next join (or a switch) plays

        private static string ReplaySourceDir => Environment.GetEnvironmentVariable("SF_REPLAY_SOURCE") != null
            ? WinePath(Environment.GetEnvironmentVariable("SF_REPLAY_SOURCE")) : Path.Combine(ReplayRoot, "COMP");

        private static List<string> ListReplays()
        {
            var list = new List<string>();
            try { list.AddRange(Directory.GetFiles(ReplaySourceDir, "*.sfrp")); } catch { }
            list.Sort(StringComparer.Ordinal);
            list.Reverse();   // newest first
            return list;
        }

        // "20261109-102531.sfrp" -> "Nov 9 10:25"
        private static string ReplayLabel(string path)
        {
            string n = System.IO.Path.GetFileNameWithoutExtension(path);
            DateTime t;
            return DateTime.TryParseExact(n, "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out t) ? t.ToString("MMM d HH:mm", System.Globalization.CultureInfo.InvariantCulture) : n;
        }

        private static ReplayReader OpenReplay(string path)
        {
            try { var r = new ReplayReader(); return r.Open(path) ? r : null; }
            catch (Exception e) { Log.LogWarning($"[replay] open {path}: {e.Message}"); return null; }
        }

        // Join in replay mode: pick the recording (the /play selection, else the
        // newest), give the viewer a slot the recording doesn't use, and build
        // the roster its ClientInit lists. Returns the viewer's slot, -1 = none.
        private int PrepareReplayViewer(SfClient cli, out Dictionary<int, ulong> rosterForInit)
        {
            rosterForInit = null;
            ReplayReader r = _replaySelected;
            _replaySelected = null;
            if (r == null) { var files = ListReplays(); if (files.Count > 0) r = OpenReplay(files[0]); }
            var roster = r != null ? r.Roster() : new Dictionary<int, ulong>();
            int slot = -1;
            for (int s = 3; s >= 0 && slot < 0; s--)
            {
                if (roster.ContainsKey(s)) continue;
                bool taken = false;
                foreach (var kv in _sfClients) if (kv.Value != cli && kv.Value.Slot == s) { taken = true; break; }
                if (!taken) slot = s;
            }
            if (slot < 0) { r?.Close(); return -1; }
            // A player watching their own match: their client must not see its own
            // steamId on another slot (it would treat that player as itself).
            var swap = new Dictionary<ulong, ulong>();
            var shown = new Dictionary<int, ulong>();
            foreach (var kv in roster)
            {
                ulong sid = kv.Value;
                if (sid == cli.SteamID) { swap[sid] = 76561197960265728UL + 1000UL + (ulong)kv.Key; sid = swap[kv.Value]; }
                shown[kv.Key] = sid;
            }
            rosterForInit = shown;
            ReplayViewer prev;
            if (_viewers.TryGetValue(cli.Addr.ToString(), out prev)) prev.Reader?.Close();
            _viewers[cli.Addr.ToString()] = new ReplayViewer { Cli = cli, Reader = r, Roster = shown, SidSwap = swap };
            cli.IsSpectator = true;
            return slot;
        }

        // The viewer's client asked to spawn (it's in the lobby scene): spawn the
        // recorded players and start the stream.
        private byte[] ReplayOptionsFor(SfClient cli)
        {
            ReplayViewer v;
            return _viewers.TryGetValue(cli.Addr.ToString(), out v) && v.Reader != null ? v.Reader.Options() ?? LobbyOptionsBody() : LobbyOptionsBody();
        }

        private void BeginReplayForViewer(SfClient cli)
        {
            ReplayViewer v;
            if (!_viewers.TryGetValue(cli.Addr.ToString(), out v) || v.Streaming) return;
            if (v.Reader == null) { SendChatToPlayer(cli, "No replays recorded yet."); return; }
            if (!v.PlayersSpawned)
            {
                foreach (var kv in v.Roster)
                {
                    byte[] spawn = new byte[1 + 24 + 1 + 4];
                    spawn[0] = (byte)kv.Key;   // pos/rot zeros, spawnFlag 0, colorCount 0
                    SendSfPacket(cli.Addr, PktClientSpawned, spawn, kv.Value, 0);
                }
                v.PlayersSpawned = true;
            }
            v.LastPath = v.Reader.Path;
            v.Paused = false;
            v.BeganAt = Time.realtimeSinceStartup;
            v.T0 = Time.realtimeSinceStartup;
            v.Pending = v.Reader.Next();
            v.Streaming = true;
            string name = ReplayLabel(v.Reader.Path);
            Log.LogInfo($"[replay] viewer slot {cli.Slot} watching {System.IO.Path.GetFileName(v.Reader.Path)} ({v.Reader.Header})");
            SendChatToPlayer(cli, $"Replay {name} UTC. Keys: 1-9 pick a replay, R restart, L list.");
        }

        private void TickReplayPlayback()
        {
            if (!ReplayMode || _viewers.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            List<string> gone = null;
            foreach (var kv in _viewers)
            {
                var v = kv.Value;
                if (!_sfClients.ContainsKey(kv.Key)) { (gone ?? (gone = new List<string>())).Add(kv.Key); continue; }
                if (!v.Streaming || v.Reader == null) continue;
                // Hold the start until we know the viewer's v26 address (its first
                // PlayerInput or hello): the recording opens with v26 messages (the
                // round's spawn assignment) that would otherwise go to a default port.
                if (v.Pending != null && v.Pending.Ms < 1000 && !_slotV26Endpoint.ContainsKey(v.Cli.Slot) && now - v.BeganAt < 5f)
                {
                    v.T0 = now;
                    continue;
                }
                if (v.Paused)
                {
                    // Recorded time stands still while the viewer loads the map, so
                    // StartMatch and the round play with the original timing after it.
                    if (!v.ReadySincePause && now - v.PauseStart < ReplayLoadWaitSec) continue;
                    v.T0 += now - v.PauseStart;
                    v.Paused = false;
                }
                uint elapsed = (uint)((now - v.T0) * 1000f);
                int sent = 0;
                while (v.Pending != null && v.Pending.Ms <= elapsed && sent < 400)
                {
                    var r = v.Pending;
                    IPEndPoint to = v.Cli.Addr;
                    if (IsV26Type(r.Type) && !_slotV26Endpoint.TryGetValue(v.Cli.Slot, out to))
                        to = new IPEndPoint(v.Cli.Addr.Address, V26_CLIENT_PORT);
                    ulong sid = r.SteamId, swapped;
                    if (sid != 0 && v.SidSwap.TryGetValue(sid, out swapped)) sid = swapped;
                    byte[] body = r.Body;
                    if (r.Type == PktClientJoined && body.Length >= 9 && v.SidSwap.Count > 0
                        && v.SidSwap.TryGetValue(BitConverter.ToUInt64(body, 1), out swapped))
                    {
                        body = (byte[])body.Clone();   // [u8 slot][u64 steamId]: the joiner can be the viewer too
                        for (int b = 0; b < 8; b++) body[1 + b] = (byte)(swapped >> (8 * b));
                    }
                    SendSfPacket(to, r.Type, body, sid, r.Ch);
                    sent++;
                    v.Pending = v.Reader.Next();
                    if (r.Type == PktMapChange) { v.Paused = true; v.ReadySincePause = false; v.PauseStart = now; break; }
                }
                if (v.Pending == null)
                {
                    SendChatToPlayer(v.Cli, "Replay finished. Keys: 1-9 pick a replay, R restart, L list.");
                    v.Reader.Close(); v.Reader = null; v.Streaming = false;
                }
            }
            if (gone != null) foreach (var k in gone) { _viewers[k].Reader?.Close(); _viewers.Remove(k); }
        }

        // msgType 46 from a bodyless client's recon socket: [u64 steamId][u8 op][u8 arg].
        // Records where its v26 traffic goes (it sends no PlayerInput), and in
        // the replay lobby carries its controls (it has no chat box):
        // 1 list, 2 play <arg>, 3 restart.
        private readonly Dictionary<string, float> _viewerOpAt = new Dictionary<string, float>();
        private void HandleViewerHello(byte[] data, int off, int len, IPEndPoint from)
        {
            if (len < 10) return;
            ulong sid = BitConverter.ToUInt64(data, off);
            byte op = data[off + 8], arg = data[off + 9];
            SfClient cli = null;
            foreach (var kv in _sfClients)
                if (kv.Value.Initialized && kv.Value.IsSpectator && kv.Value.SteamID == sid && kv.Value.Slot >= 0) { cli = kv.Value; break; }
            if (cli == null) return;   // players learn their v26 address from PlayerInput
            IPEndPoint prev;
            if (!_slotV26Endpoint.TryGetValue(cli.Slot, out prev) || !prev.Equals(from))
            {
                _slotV26Endpoint[cli.Slot] = from;
                Log.LogInfo($"[viewer] slot {cli.Slot} v26 endpoint {from}");
            }
            if (op == 0 || !ReplayMode) return;
            string key = cli.Addr.ToString();
            float last, now = Time.realtimeSinceStartup;
            if (_viewerOpAt.TryGetValue(key, out last) && now - last < 0.5f) return;
            _viewerOpAt[key] = now;
            if (op == 1) HandleReplayChat(cli, "/replays", "");
            else if (op == 2) HandleReplayChat(cli, "/play", arg.ToString());
            else if (op == 3) HandleReplayChat(cli, "/restart", "");
        }

        private const float ReplayLoadWaitSec = 12f;

        private void OnReplayViewerReady(SfClient cli)
        {
            ReplayViewer v;
            if (_viewers.TryGetValue(cli.Addr.ToString(), out v) && v.Paused) v.ReadySincePause = true;
        }

        private static bool SameRoster(Dictionary<int, ulong> a, Dictionary<int, ulong> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var kv in a) { ulong o; if (!b.TryGetValue(kv.Key, out o) || o != kv.Value) return false; }
            return true;
        }

        // Replay lobby chat. Every command is handled here (match commands don't apply).
        private void HandleReplayChat(SfClient sender, string cmd, string arg)
        {
            var files = ListReplays();
            switch (cmd)
            {
                case "/replays":
                case "/list":
                {
                    if (files.Count == 0) { SendChatToPlayer(sender, "No replays recorded yet."); return; }
                    var parts = new List<string>();
                    for (int i = 0; i < files.Count && i < 9; i++) parts.Add($"{i + 1}) {ReplayLabel(files[i])}");
                    SendChatToPlayer(sender, "Replays (UTC): " + string.Join("  ", parts.ToArray()));
                    return;
                }
                case "/play":
                case "/watch":
                {
                    int n;
                    if (!int.TryParse(arg, out n) || n < 1 || n > files.Count) { SendChatToPlayer(sender, files.Count == 0 ? "No replays recorded yet." : $"There are {files.Count} replays: pick 1-{Math.Min(9, files.Count)} (L lists them)."); return; }
                    var r = OpenReplay(files[n - 1]);
                    if (r == null) { SendChatToPlayer(sender, "Couldn't open that replay."); return; }
                    ReplayViewer v;
                    if (_viewers.TryGetValue(sender.Addr.ToString(), out v))
                    {
                        // Same players in the same slots: switch in place (the
                        // recording starts with its own MapChange).
                        var shown = new Dictionary<int, ulong>();
                        foreach (var kv in r.Roster()) { ulong sw; shown[kv.Key] = v.SidSwap.TryGetValue(kv.Value, out sw) ? sw : kv.Value; }
                        if (SameRoster(shown, v.Roster) && !shown.ContainsKey(sender.Slot))
                        {
                            v.Reader?.Close();
                            v.Reader = r; v.Streaming = false;
                            BeginReplayForViewer(sender);
                            return;
                        }
                    }
                    _replaySelected?.Close();
                    _replaySelected = r;
                    SendChatToPlayer(sender, $"Replay {n} has different players: leave and rejoin this lobby to watch it (it's selected).");
                    return;
                }
                case "/restart":
                {
                    ReplayViewer v;
                    if (!_viewers.TryGetValue(sender.Addr.ToString(), out v)) return;
                    string path = v.Reader?.Path ?? v.LastPath ?? (files.Count > 0 ? files[0] : null);
                    var r = path != null ? OpenReplay(path) : null;
                    if (r == null) { SendChatToPlayer(sender, "Nothing to restart."); return; }
                    v.Reader?.Close(); v.Reader = r; v.Streaming = false;
                    BeginReplayForViewer(sender);
                    return;
                }
                default:
                    SendChatToPlayer(sender, "Replay lobby. Keys: 1-9 pick a replay, R restart, L list.");
                    return;
            }
        }
    }
}
