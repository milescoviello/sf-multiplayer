using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SFClientRecon
{
    // Join diagnostics sent to the router (control op 0x07 DIAG, logged there,
    // never forwarded to a lobby). When a player's join fails on their side, no
    // packet ever reaches a lobby and the server has nothing to go on (seen
    // 11-11: an outside player picked MAIN and COMP twice, their game sent the
    // router BIND and then nothing). So the client reports, once per session:
    //   - a hello line: mod version, 32/64-bit, game/Unity/OS versions, Steam state;
    //   - Error/Fatal log lines from any source (our plugins, the game's own
    //     exceptions through the Unity log), and our "[oracle-lobby]" warnings;
    //   - "join stalled" when the server hasn't accepted us 15 s after a connect.
    // Capped at 15 lines a session, deduplicated, user folders stripped from
    // paths. SF_DIAG=0 turns it off.
    public partial class Plugin
    {
        internal static readonly bool DiagEnabled = Environment.GetEnvironmentVariable("SF_DIAG") != "0";
        private const int DiagMaxPerSession = 15;
        private const int DiagMaxText = 300;
        private static readonly object _diagLock = new object();
        private static readonly Queue<string> _diagQueue = new Queue<string>();
        private static readonly HashSet<string> _diagSeen = new HashSet<string>();
        private static int _diagSent;
        private static bool _diagHelloQueued;
        private static float _joinWatchAt = -1f;
        private static string _joinWatchCode = "";

        private sealed class DiagLogListener : ILogListener
        {
            public void LogEvent(object sender, LogEventArgs e)
            {
                try
                {
                    if (e == null || e.Data == null) return;
                    string text = e.Data.ToString();
                    bool error = (e.Level & (LogLevel.Error | LogLevel.Fatal)) != 0;
                    bool ours = (e.Level & LogLevel.Warning) != 0 && text.StartsWith("[oracle-lobby]");
                    if (!error && !ours) return;
                    // Stock boot noise on every start (not about joining).
                    if (text.IndexOf("WeaponPickUp.Awake", StringComparison.Ordinal) >= 0
                        || text.IndexOf("MusicHandler.Awake", StringComparison.Ordinal) >= 0
                        || text.IndexOf("NetworkSyncableObject.Awake", StringComparison.Ordinal) >= 0) return;
                    string src = e.Source != null ? e.Source.SourceName : "?";
                    QueueDiag((error ? "error " : "warn ") + src + ": " + text);
                }
                catch { }   // never log from here: it would come back to this listener
            }

            public void Dispose() { }
        }

        internal static void InstallDiagReport()
        {
            if (!DiagEnabled) return;
            try { BepInEx.Logging.Logger.Listeners.Add(new DiagLogListener()); }
            catch (Exception e) { Log.LogInfo("[diag] listener: " + e.Message); }
        }

        // Thread-safe: log events arrive on any thread (the RX thread logs too).
        internal static void QueueDiag(string text)
        {
            if (!DiagEnabled || string.IsNullOrEmpty(text)) return;
            text = CleanDiag(text);
            string key = text.Length > 80 ? text.Substring(0, 80) : text;
            System.Threading.Monitor.Enter(_diagLock);   // not lock(): Mono 2.0 (see tools/check-mono2-landmines.sh)
            try
            {
                if (_diagSent + _diagQueue.Count >= DiagMaxPerSession) return;
                if (!_diagSeen.Add(key)) return;
                _diagQueue.Enqueue(text);
            }
            finally { System.Threading.Monitor.Exit(_diagLock); }
        }

        // One line, printable ASCII, user folders removed, at most DiagMaxText.
        private static string CleanDiag(string s)
        {
            var sb = new System.Text.StringBuilder(Math.Min(s.Length, DiagMaxText + 16));
            for (int i = 0; i < s.Length && sb.Length < DiagMaxText + 64; i++)
            {
                char c = s[i];
                if (c == '\r') continue;
                if (c == '\n' || c == '\t') { sb.Append(" | "); continue; }
                sb.Append(c >= ' ' && c < (char)127 ? c : '?');
            }
            string o = sb.ToString();
            o = StripUserFolder(o, "\\Users\\");
            o = StripUserFolder(o, "/Users/");
            o = StripUserFolder(o, "/home/");
            return o.Length > DiagMaxText ? o.Substring(0, DiagMaxText) : o;
        }

        private static string StripUserFolder(string s, string marker)
        {
            int at = 0;
            while ((at = s.IndexOf(marker, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int start = at + marker.Length;
                int end = start;
                while (end < s.Length && s[end] != '\\' && s[end] != '/') end++;
                s = s.Substring(0, start) + "_" + s.Substring(end);
                at = start + 1;
            }
            return s;
        }

        internal static void ArmJoinWatch(string code)
        {
            if (!DiagEnabled) return;
            _joinWatchAt = Time.realtimeSinceStartup + 15f;
            _joinWatchCode = code ?? "";
        }

        // Main thread, every frame (cheap until something is queued).
        internal void TickDiagReport()
        {
            if (!DiagEnabled || _socket == null || _serverEp == null) return;
            // Only once the router has answered us (a SELECT-ACK in the last 30 s):
            // an old router without DIAG still drops it as an unknown op, but
            // there's no point sending before anything is reachable.
            long ack = System.Threading.Interlocked.Read(ref _selectAckTicks);
            if (ack == 0 || DateTime.UtcNow.Ticks - ack > TimeSpan.TicksPerSecond * 30) return;

            if (!_diagHelloQueued)
            {
                _diagHelloQueued = true;
                QueueDiag(DiagHello());
            }
            if (_joinWatchAt > 0f && Time.realtimeSinceStartup >= _joinWatchAt)
            {
                _joinWatchAt = -1f;
                string stall = JoinStallReport();
                if (stall != null) QueueDiag(stall);
            }

            string next = null;
            System.Threading.Monitor.Enter(_diagLock);
            try { if (_diagQueue.Count > 0) { next = _diagQueue.Dequeue(); _diagSent++; } }
            finally { System.Threading.Monitor.Exit(_diagLock); }
            if (next != null) SendDiagPacket(next);
        }

        private string DiagHello()
        {
            string steam = OwnSteamId() != 0 ? "id ok" : "no id";
            return "hello client " + PluginVersion + " " + (IntPtr.Size * 8) + "-bit, game "
                + Application.version + ", Unity " + Application.unityVersion + ", "
                + SystemInfo.operatingSystem + ", steam " + steam
                + ", online code " + (RefOk(CachedType("P2PPackageHandler")) ? "yes" : "MISSING");
        }

        // null = joined fine.
        private static string JoinStallReport()
        {
            try
            {
                if (_joinWatchCode == "REPLAY") return null;   // viewers never get "accepted"
                var mmType = CachedType("MultiplayerManager");
                var mm = RefOk(mmType) ? UnityEngine.Object.FindObjectOfType(mmType) : null;
                bool accepted = false, initialized = false;
                if (RefOk(mm))
                {
                    var accF = AccessTools.Field(mmType, "mHasBeenAcceptedFromServer");
                    var initF = AccessTools.Field(mmType, "mHasBeenInitializedFromServer");
                    if (RefOk(accF)) accepted = (bool)accF.GetValue(mm);
                    if (RefOk(initF)) initialized = (bool)initF.GetValue(mm);
                }
                if (accepted) return null;
                bool sock = false, sentOrRx = false;
                var pktType = CachedType("P2PPackageHandler");
                var pkt = RefOk(pktType) ? UnityEngine.Object.FindObjectOfType(pktType) : null;
                if (RefOk(pkt))
                {
                    var sf = AccessTools.Field(pktType, "_socket");
                    var s = RefOk(sf) ? sf.GetValue(pkt) as System.Net.Sockets.Socket : null;
                    sock = s != null && s.Connected;
                    var srF = AccessTools.Field(pktType, "mHasSentOrReceived");
                    if (RefOk(srF)) sentOrRx = (bool)srF.GetValue(pkt);
                }
                return "join stalled 15 s: lobby " + _joinWatchCode
                    + ", scene " + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name
                    + ", mm " + (RefOk(mm) ? "yes" : "NO") + ", accepted " + accepted
                    + ", initialized " + initialized + ", pkt " + (RefOk(pkt) ? "yes" : "NO")
                    + ", socket " + sock + ", sentOrRx " + sentOrRx
                    + ", steam " + (OwnSteamId() != 0 ? "id ok" : "no id");
            }
            catch (Exception e) { return "join stalled 15 s: lobby " + _joinWatchCode + " (report failed: " + e.Message + ")"; }
        }

        // [8 magic][1 op=0x07][1 codeLen][code][4 nonce][text]: the SELECT layout
        // plus a text tail, so the router parses it with its normal control parser.
        private void SendDiagPacket(string text)
        {
            try
            {
                byte[] codeBytes = System.Text.Encoding.ASCII.GetBytes(SelectedLobbyCode ?? "");
                if (codeBytes.Length > 16) codeBytes = new byte[0];
                byte[] textBytes = System.Text.Encoding.ASCII.GetBytes(text);
                byte[] pkt = new byte[8 + 1 + 1 + codeBytes.Length + 4 + textBytes.Length];
                pkt[0] = 0x53; pkt[1] = 0x46; pkt[2] = 0x52; pkt[3] = 0x54; pkt[4] = 0x52;   // "SFRTR"
                pkt[5] = 0x00; pkt[6] = 0x00; pkt[7] = 0x01;
                pkt[8] = 0x07;                                   // op = DIAG (no reply)
                pkt[9] = (byte)codeBytes.Length;
                Buffer.BlockCopy(codeBytes, 0, pkt, 10, codeBytes.Length);
                Buffer.BlockCopy(textBytes, 0, pkt, 10 + codeBytes.Length + 4, textBytes.Length);
                _socket.Send(pkt, pkt.Length, _serverEp);
            }
            catch { }
        }
    }
}
