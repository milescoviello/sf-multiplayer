using System;
using System.Collections;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace SFClientRecon
{
    /// <summary>
    /// When launched with -address/-port, Quick Match / Host Match must not call Steam matchmaking
    /// (hangs). Connect UDP to the oracle and enter the lobby hub like OnSocketServerJoined.
    /// </summary>
    public partial class Plugin
    {
        private static bool _oracleConnectMode;
        private static bool _oracleLobbyPatchesInstalled;
        private static bool _oracleConnectStarted;
        private static bool _endpointResolved;
        private static string _resolvedOracleHost;
        private static int _resolvedOraclePort;
        private static Type _pktType;
        private static Type _mmType;
        private static Type _mhType;
        private static Type _gmType;
        private static Type _mmSocketsType;

        internal static string OracleServerHost
        {
            get { ResolveOracleEndpoint(); return string.IsNullOrEmpty(_resolvedOracleHost) ? "127.0.0.1" : _resolvedOracleHost; }
        }

        internal static int OracleServerPort
        {
            get { ResolveOracleEndpoint(); return _resolvedOraclePort > 0 ? _resolvedOraclePort : 1338; }
        }

        internal static void ResolveOracleEndpoint()
        {
            if (_endpointResolved) return;
            _endpointResolved = true;
            // Default to the sf-router front door (1338), not MAIN-direct (1337).
            // The router SELECT-routes to the chosen lobby (and -default MAIN routes
            // no-SELECT traffic to MAIN), so browsing/switching works. -port 1337
            // still reaches MAIN directly as a router-bypass fallback.
            _resolvedOraclePort = 1338;

            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-address" && !string.IsNullOrEmpty(args[i + 1]))
                {
                    _resolvedOracleHost = args[i + 1];
                    _oracleConnectMode = true;
                }
                else if (args[i] == "-port" && int.TryParse(args[i + 1], out var p) && p > 0)
                    _resolvedOraclePort = p;
            }

            if (string.IsNullOrEmpty(_resolvedOracleHost))
            {
                var envHost = Environment.GetEnvironmentVariable("SF_ORACLE_ADDRESS");
                if (!string.IsNullOrEmpty(envHost))
                {
                    _resolvedOracleHost = envHost.Trim();
                    _oracleConnectMode = true;
                }
            }
            var envPort = Environment.GetEnvironmentVariable("SF_ORACLE_PORT");
            if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out var ep) && ep > 0)
                _resolvedOraclePort = ep;

            if (string.IsNullOrEmpty(_resolvedOracleHost))
            {
                try
                {
                    string gameRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                    string[] candidates =
                    {
                        Path.Combine(Path.Combine(Path.Combine(gameRoot, "BepInEx"), "config"), "sf-oracle-endpoint.txt"),
                        Path.Combine(Path.Combine(Path.Combine(gameRoot, "BepInEx"), "plugins"), "sf-oracle-endpoint.txt"),
                    };
                    foreach (var path in candidates)
                    {
                        if (!File.Exists(path)) continue;
                        var lines = File.ReadAllLines(path);
                        if (lines.Length == 0 || string.IsNullOrEmpty(lines[0]) || lines[0].Trim().Length == 0) continue;
                        var line = lines[0].Trim();
                        if (line.Contains(":"))
                        {
                            var parts = line.Split(':');
                            _resolvedOracleHost = parts[0].Trim();
                            if (parts.Length > 1) int.TryParse(parts[1].Trim(), out _resolvedOraclePort);
                        }
                        else _resolvedOracleHost = line;
                        if (lines.Length > 1 && int.TryParse(lines[1].Trim(), out var filePort) && filePort > 0)
                            _resolvedOraclePort = filePort;
                        _oracleConnectMode = true;
                        Log.LogInfo($"[oracle-lobby] Endpoint from {path} → {_resolvedOracleHost}:{_resolvedOraclePort}");
                        break;
                    }
                }
                catch (Exception e) { Log.LogWarning($"[oracle-lobby] endpoint file read: {e.Message}"); }
            }

            if (_oracleConnectMode && !string.IsNullOrEmpty(_resolvedOracleHost))
                Log.LogInfo($"[oracle-lobby] Oracle mode ON → {_resolvedOracleHost}:{_resolvedOraclePort}");
        }

        private static void DetectOracleConnectMode()
        {
            ResolveOracleEndpoint();
        }

        internal void InstallOracleLobbyConnectPatches()
        {
            DetectOracleConnectMode();
            if (!_oracleConnectMode || _oracleLobbyPatchesInstalled) return;
            _oracleLobbyPatchesInstalled = true;
            try
            {
                _pktType = CachedType("P2PPackageHandler");
                _mmType = CachedType("MultiplayerManager");
                _mhType = CachedType("MatchmakingHandler");
                _gmType = CachedType("GameManager");
                if (!RefOk(_pktType) || !RefOk(_mmType) || !RefOk(_mhType))
                {
                    Log.LogWarning("[oracle-lobby] Game types not found — is Assembly-CSharp.srv installed?");
                    return;
                }

                var harmony = new Harmony(PluginGuid + ".oracle-lobby");
                var joinRandom = AccessTools.Method(_mhType, "JoinRandomServer");
                if (RefOk(joinRandom))
                    harmony.Patch(joinRandom, prefix: new HarmonyMethod(typeof(Plugin), nameof(JoinRandomServer_OraclePrefix)));
                var createLobby = AccessTools.Method(_mhType, "CreateSteamLobby");
                if (RefOk(createLobby))
                    harmony.Patch(createLobby, prefix: new HarmonyMethod(typeof(Plugin), nameof(CreateSteamLobby_OraclePrefix)));
                var joinDefault = AccessTools.Method(_mhType, "JoinDefaultSocketServer");
                if (RefOk(joinDefault))
                    harmony.Patch(joinDefault, prefix: new HarmonyMethod(typeof(Plugin), nameof(JoinDefaultSocketServer_OraclePrefix)));

                _mmSocketsType = CachedType("MatchMakingHandlerSockets");
                if (RefOk(_mmSocketsType))
                {
                    var hostServer = AccessTools.Method(_mmSocketsType, "HostServer");
                    if (RefOk(hostServer))
                        harmony.Patch(hostServer, prefix: new HarmonyMethod(typeof(Plugin), nameof(HostServer_OraclePrefix)));
                    var joinServer = AccessTools.Method(_mmSocketsType, "JoinServer");
                    if (RefOk(joinServer))
                        harmony.Patch(joinServer, prefix: new HarmonyMethod(typeof(Plugin), nameof(JoinServer_OraclePrefix)));
                }

                var pktUpdate = AccessTools.Method(_pktType, "Update");
                if (RefOk(pktUpdate))
                    harmony.Patch(pktUpdate, postfix: new HarmonyMethod(typeof(Plugin), nameof(P2PPackageHandler_Update_OraclePostfix)));
                var pktInit = AccessTools.Method(_pktType, "Init");
                if (RefOk(pktInit))
                    harmony.Patch(pktInit, postfix: new HarmonyMethod(typeof(Plugin), nameof(P2PPackageHandler_Init_OraclePostfix)));
                var reqInit = AccessTools.Method(_mmType, "RequestClientInit");
                if (RefOk(reqInit))
                    harmony.Patch(reqInit, postfix: new HarmonyMethod(typeof(Plugin), nameof(MultiplayerManager_RequestClientInit_OraclePostfix)));
                var checkMsg = AccessTools.Method(_pktType, "CheckMessageType");
                if (RefOk(checkMsg))
                    harmony.Patch(checkMsg, prefix: new HarmonyMethod(typeof(Plugin), nameof(P2PPackageHandler_CheckMessageType_OraclePrefix)));
                Log.LogInfo($"[oracle-diag] patch attach: Update={RefOk(pktUpdate)} CheckMessageType={RefOk(checkMsg)}");

                // ReadMessageBuffer runs BEFORE the init-flag guard that can drop
                // MapChange. Prefix it to (a) log every inbound msgType so we can
                // see exactly what the game socket receives, and (b) force the
                // init flag true so the guard never drops a server packet.
                var readBuf = AccessTools.Method(_pktType, "ReadMessageBuffer");
                if (RefOk(readBuf))
                    harmony.Patch(readBuf, prefix: new HarmonyMethod(typeof(Plugin), nameof(P2PPackageHandler_ReadMessageBuffer_OraclePrefix)));
                Log.LogInfo($"[oracle-diag] patch attach: ReadMessageBuffer={RefOk(readBuf)}");

                var checkReady = AccessTools.Method(_mmType, "CheckReadyPlayers");
                if (RefOk(checkReady))
                    harmony.Patch(checkReady, prefix: new HarmonyMethod(typeof(Plugin), nameof(CheckReadyPlayers_OraclePrefix)));

                PatchAutoStartCountdown(harmony);
                var onMatchStart = AccessTools.Method(_mmType, "OnMatchStart");
                if (RefOk(onMatchStart))
                    harmony.Patch(onMatchStart, postfix: new HarmonyMethod(typeof(Plugin), nameof(OnMatchStart_OraclePostfix)));

                // NOTE: Do NOT patch GameManager.StartMatch / StartCountDown.
                // On this Mono 2.0 runtime the HarmonyX-generated DMD wrapper for
                // StartMatch references System.Array.Empty (missing in Mono 2.0),
                // throwing MissingMethodException inside StartMatch → the map-load
                // coroutine (StartMapSequence) never runs → isLoading stays true
                // → the client never switches from lobby to the map. Leaving these
                // methods unpatched lets the vanilla map-load run natively.

                Log.LogInfo("[oracle-lobby] Patched Quick/Host Match → UDP oracle (no Steam lobby).");
                ScheduleOracleAutoConnect();
            }
            catch (Exception e) { Log.LogWarning($"[oracle-lobby] patch failed: {e.Message}"); }
        }

        private void ScheduleOracleAutoConnect()
        {
            // After the premature-init fix (0.3.8+), a delayed auto-connect is safe:
            // it covers PLAY ONLINE → walk into lobby box without clicking Quick Match.
            _autoConnectAt = Time.realtimeSinceStartup + 4f;
            Log.LogInfo("[oracle-lobby] Auto-connect scheduled in 4s (backup if Quick Match not clicked).");
        }

        private static float _autoConnectAt = -1f;

        // Ticked every frame from Update(). Fires the oracle lobby connect once,
        // 2.5s after Awake, if we haven't already started connecting. Uses only
        // reflection that takes explicit args (no zero-arg params → no Array.Empty).
        internal void TickOracleAutoConnect()
        {
            if (!_oracleConnectMode || _autoConnectAt < 0f) return;
            if (Time.realtimeSinceStartup < _autoConnectAt) return;
            _autoConnectAt = -1f;
            if (_oracleConnectStarted) return;
            try
            {
                if (!RefOk(_mmType)) _mmType = CachedType("MultiplayerManager");
                var mm = UnityEngine.Object.FindObjectOfType(_mmType);
                if (RefOk(mm))
                {
                    var accF = AccessTools.Field(_mmType, "mHasBeenAcceptedFromServer");
                    var initF = AccessTools.Field(_mmType, "mHasBeenInitializedFromServer");
                    if (RefOk(accF) && (bool)accF.GetValue(mm)) return;
                    if (RefOk(initF) && (bool)initF.GetValue(mm)) return;
                }
                var pkt = GetPktHandler();
                if (RefOk(pkt) && RefOk(_pktType))
                {
                    var sentF = AccessTools.Field(_pktType, "mHasSentOrReceived");
                    if (RefOk(sentF) && (bool)sentF.GetValue(pkt)) return;
                }
            }
            catch { }
            Log.LogInfo("[oracle-lobby] Auto-connect → oracle (endpoint file / -address).");
            BeginOracleLobbyConnect("AutoConnect");
        }

        internal static bool JoinRandomServer_OraclePrefix()
        {
            if (!_oracleConnectMode) return true;
            BeginOracleLobbyConnect("QuickMatch");
            return false;
        }

        internal static bool CreateSteamLobby_OraclePrefix(int maxPlayers, bool privateLobby)
        {
            if (!_oracleConnectMode) return true;
            // Host Match now spins up a brand-new backend lobby (visible in the
            // browser list) and hosts it, instead of silently joining MAIN. If no
            // create endpoint is configured CreateAndHostLobby falls back to MAIN.
            CreateAndHostLobby();
            return false;
        }

        internal static bool JoinDefaultSocketServer_OraclePrefix()
        {
            if (!_oracleConnectMode) return true;
            BeginOracleLobbyConnect("QuickMatch-sockets");
            return false;
        }

        internal static bool HostServer_OraclePrefix()
        {
            if (!_oracleConnectMode) return true;
            BeginOracleLobbyConnect("HostMatch-sockets");
            return false;
        }

        internal static bool JoinServer_OraclePrefix()
        {
            if (!_oracleConnectMode) return true;
            BeginOracleLobbyConnect("QuickMatch-lan");
            return false;
        }

        private static void BeginOracleLobbyConnect(string source)
        {
            try
            {
                _oracleConnectStarted = true;
                if (Instance != null) Instance.ResetLocalSlot();   // look for our slot right away
                ArmJoinWatch(SelectedLobbyCode);
                Log.LogInfo($"[oracle-lobby] {source} → connecting to {OracleServerHost}:{OracleServerPort} (no Steam).");
                var pkt = GetPktHandler();
                if (!RefOk(pkt))
                {
                    Log.LogError("[oracle-lobby] P2PPackageHandler missing.");
                    return;
                }
                // Mirror the game's own entry point, OnlineBox.Update ("all local
                // players are in the ONLINE box"): ResumeNetworkTraffic → Init →
                // lock the box → AssignBox → join. The game PAUSES traffic at boot
                // (GameManager.Awake: "Disabling networking!") and Init() returns
                // immediately while paused, so without the resume the socket never
                // connects and every send is dropped: the client loops on
                // "Connecting to the server..." forever. That's what auto-connect
                // and server-browser joins did whenever nobody walked into the box.
                var resume = AccessTools.Method(_pktType, "ResumeNetworkTraffic");
                if (RefOk(resume)) resume.Invoke(pkt, null);
                var init = AccessTools.Method(_pktType, "Init");
                if (RefOk(init)) init.Invoke(pkt, null);
                ClaimOnlineBox();

                // Clean handshake: the joiner path must send ClientRequesting*
                // in order. A stale mHasBeenInitializedFromServer=true (e.g.
                // from an earlier partial session) makes ReadMessageBuffer drop
                // init packets and the client never leaves PLAY ONLINE.
                try
                {
                    var mmReset = UnityEngine.Object.FindObjectOfType(_mmType);
                    var initF = AccessTools.Field(_mmType, "mHasBeenInitializedFromServer");
                    if (RefOk(mmReset) && RefOk(initF) && (bool)initF.GetValue(mmReset))
                    {
                        initF.SetValue(mmReset, false);
                        Log.LogInfo("[oracle-lobby] Reset mHasBeenInitializedFromServer=false for clean join.");
                    }
                }
                catch { }

                var setNet = AccessTools.Method(_mhType, "SetNetworkMatch", new[] { typeof(bool) });
                if (RefOk(setNet)) setNet.Invoke(null, new object[] { true });

                var setLobbyType = AccessTools.Method(_mhType, "SetNewLobbyType");
                if (RefOk(setLobbyType))
                {
                    var lobbyTypeEnum = CachedType("ELobbyType");
                    if (RefOk(lobbyTypeEnum))
                    {
                        object publicLobby = Enum.ToObject(lobbyTypeEnum, 2);
                        setLobbyType.Invoke(null, new object[] { publicLobby });
                    }
                }

                var mm = UnityEngine.Object.FindObjectOfType(_mmType);
                if (!RefOk(mm))
                {
                    Log.LogError("[oracle-lobby] MultiplayerManager not found in scene.");
                    return;
                }
                var onJoined = AccessTools.Method(_mmType, "OnSocketServerJoined");
                if (RefOk(onJoined))
                    onJoined.Invoke(mm, null);
                else
                {
                    var onScene = AccessTools.Method(_mmType, "OnSceneStarted");
                    if (RefOk(onScene)) onScene.Invoke(mm, null);
                }
                // OnSocketServerJoined (the joiner path) never sets
                // mHasBeenInitializedFromServer; only the host path and the very
                // end of OnClientInit do. ReadMessageBuffer drops EVERY gameplay
                // packet (MapChange, MapInfo, ObjectUpdate...) — only ClientInit/
                // ClientAccepted are exempt — until that flag is true. So the
                // client spawns but never changes map. Force it true here.
                ForceInitializedFromServer(mm);
            }
            catch (Exception e) { Log.LogError($"[oracle-lobby] BeginOracleLobbyConnect: {e.Message}"); }
        }

        // Entry point for the in-game server browser (called cross-assembly by
        // reflection from SFServerBrowser): join a specific lobby by code over
        // the sf-router single port — no game restart. Sets the SELECT code,
        // emits a SELECT immediately so the router pins our IP→backend before
        // the game socket connects, then runs the normal oracle-connect path.
        // Works from the main menu; to switch lobbies, leave to menu and call
        // again (a fresh SELECT + connect; no live-connection rebind needed).
        internal static void RequestJoinLobby(string code)
        {
            code = (code ?? "").Trim().ToUpper();
            if (code.Length == 0) { Log.LogWarning("[oracle-lobby] RequestJoinLobby: empty code ignored."); return; }
            // JOIN on the lobby we're already in: nothing to do (it used to leave
            // and rejoin it, which drops you out of the round).
            if (IsInLobby(code))
            {
                Log.LogInfo($"[oracle-lobby] RequestJoinLobby({code}): already in it.");
                var bi0 = Instance;
                if (RefOk(bi0)) { bi0._bannerText = "You're already in " + code + "."; bi0._bannerUntilUtc = DateTime.UtcNow.AddSeconds(3); }
                return;
            }
            if (IsMatchInProgress())
            {
                Log.LogWarning($"[oracle-lobby] RequestJoinLobby({code}) ignored — a match is in progress. Leave to the menu first; switching lobbies mid-match re-inits the netstack under the live game and desyncs it. (issue #5)");
                // (issue #5 UX) Surface WHY nothing happens — the server-browser's
                // "Connecting…" toast otherwise just times out silently, which reads
                // as "switching is broken". Mirrors the SELECT-ACK "not found" banner.
                var bi = Instance;
                if (RefOk(bi)) { bi._bannerText = "Can't switch lobbies mid-match — leave to the menu first."; bi._bannerUntilUtc = DateTime.UtcNow.AddSeconds(4); }
                return;
            }
            JoinLobbyNow(code);
        }

        // In this lobby right now: it's the selected one and it gave us a session
        // (cleared on a switch, a kick, or a lobby restart). Also read by
        // SFServerBrowser by reflection.
        internal static bool IsInLobby(string code)
        {
            return _sessionToken != 0 && string.Equals(SelectedLobbyCode ?? "", (code ?? "").Trim().ToUpper(), StringComparison.Ordinal);
        }

        // RequestJoinLobby minus the mid-match refusal: also used to rejoin a
        // lobby that restarted under us (that match is gone anyway).
        private static void JoinLobbyNow(string code)
        {
            SelectedLobbyCode = code;
            Log.LogInfo($"[oracle-lobby] RequestJoinLobby({code}) — SELECT + connect via router.");
            var inst = Instance;
            if (RefOk(inst))
            {
                // Re-arm the SELECT resend loop for the NEW lobby: capture the
                // current (monotonic) snapshot count so "snapshots flowing" only
                // becomes true once the NEW lobby's snapshots actually arrive.
                inst._lastSeenSnapCount = inst._snapsReceived;
                inst._lastSnapGrowAt = -1f;
                inst._lastSelectAt = -1f;
                System.Threading.Interlocked.Exchange(ref _selectAckTicks, 0L);   // a new lobby needs its own ACK
                // Drop any slot discovered in the PREVIOUS lobby. FindLocalSlot
                // short-circuits on a cached _localSlot >= 0, so without this the
                // 60Hz input loop keeps sending under the old lobby's slot number
                // until the NEW lobby's first MapChange re-derives it — and the new
                // host rejects input for a slot whose handshaken owner doesn't match,
                // so the player's input is silently dropped until then.
                inst.ResetLocalSlot();
                inst._lastAnnouncedSlot = -2;
                try { inst.SendSelectLobbyPacket(); }
                catch (Exception e) { Log.LogWarning($"[oracle-lobby] SELECT send: {e.Message}"); }
            }
            // Switching from a lobby we're in: leave it the stock way first
            // (MultiplayerManager.OnDisconnected: ClientLeft to the old lobby,
            // which the game socket is still bound to; "accepted" reset; old
            // players' bodies removed). Without it the patched client ignored
            // the new lobby's ClientAccepted (mHasBeenAcceptedFromServer stayed
            // true) and the switch stalled; the old lobby's players stayed too.
            LeaveCurrentLobbyForSwitch();
            // Allow a re-connect from the menu (the autoconnect guard latches this).
            _oracleConnectStarted = false;
            BeginOracleLobbyConnect("ServerBrowser:" + code);
        }

        private static void LeaveCurrentLobbyForSwitch()
        {
            _sessionToken = 0;   // the next lobby issues its own
            try
            {
                if (!RefOk(_mmType)) _mmType = CachedType("MultiplayerManager");
                var mm = RefOk(_mmType) ? UnityEngine.Object.FindObjectOfType(_mmType) : null;
                if (!RefOk(mm)) return;
                var accF = AccessTools.Field(_mmType, "mHasBeenAcceptedFromServer");
                bool accepted = RefOk(accF) && (bool)accF.GetValue(mm);
                if (!accepted) return;
                // By signature: the online build's OnDisconnected(bool). The game code
                // SFOnlinePatcher builds also keeps stock's OnDisconnected() next to
                // it, and a by-name lookup picked that one ("parameters do not match
                // signature"), so the leave failed and the switch to the new lobby
                // stalled.
                var onDisc = AccessTools.Method(_mmType, "OnDisconnected", new Type[] { typeof(bool) });
                if (RefOk(onDisc)) onDisc.Invoke(mm, new object[] { true });
                else { onDisc = AccessTools.Method(_mmType, "OnDisconnected", new Type[0]); if (RefOk(onDisc)) onDisc.Invoke(mm, null); }
                if (RefOk(accF)) accF.SetValue(mm, false);
                // Forget the old lobby's roster too: the new lobby's ClientInit
                // first "removes disconnected clients" still listed here, by
                // indexing m_Players with their slot (it threw once m_Players
                // was emptied, and the join never finished).
                var ccF = AccessTools.Field(_mmType, "mConnectedClients");
                var cc = RefOk(ccF) ? ccF.GetValue(mm) as Array : null;
                if (cc != null) ccF.SetValue(mm, Array.CreateInstance(cc.GetType().GetElementType(), cc.Length));
                var playersF = AccessTools.Field(_mmType, "m_Players");
                var players = RefOk(playersF) ? playersF.GetValue(mm) as System.Collections.IList : null;
                if (players != null) players.Clear();   // their bodies were just destroyed
                Log.LogInfo("[oracle-lobby] left the current lobby (ClientLeft sent) before switching.");
            }
            catch (Exception e) { Log.LogWarning($"[oracle-lobby] leave before switch: {(e.InnerException ?? e).Message}"); }
        }

        // True if a match is actively in progress. GameManager.inFight is a
        // STATIC bool, so it's read with GetValue(null) — no instance lookup.
        // Used to refuse a mid-match lobby switch (which would re-init the
        // netstack under a live game). (issue #5)
        private static bool IsMatchInProgress()
        {
            try
            {
                var gmType = RefOk(_gmType) ? _gmType : CachedType("GameManager");
                if (!RefOk(gmType)) return false;
                var inFightF = AccessTools.Field(gmType, "inFight");
                return RefOk(inFightF) && (bool)inFightF.GetValue(null);
            }
            catch { return false; }
        }

        // Do what OnlineBox.Update does once it fires: lock the box (so its own
        // Update can't start a second Init/OnSceneStarted when a player later
        // walks in) and hand it to MultiplayerManager, whose join path calls
        // mOnlineBox.Joined() and checks it before disabling the lobby players.
        private static void ClaimOnlineBox()
        {
            try
            {
                var boxType = CachedType("OnlineBox");
                if (!RefOk(boxType)) return;
                var box = UnityEngine.Object.FindObjectOfType(boxType);
                if (!RefOk(box)) { Log.LogWarning("[oracle-lobby] OnlineBox not found — joining without it."); return; }
                var doneF = AccessTools.Field(boxType, "done");
                var lockF = AccessTools.Field(boxType, "mLock");
                if (RefOk(doneF)) doneF.SetValue(box, true);
                if (RefOk(lockF)) lockF.SetValue(box, true);
                if (!RefOk(_mmType)) _mmType = CachedType("MultiplayerManager");
                var mm = UnityEngine.Object.FindObjectOfType(_mmType);
                var assign = RefOk(_mmType) ? AccessTools.Method(_mmType, "AssignBox") : null;
                if (RefOk(mm) && RefOk(assign)) assign.Invoke(mm, new object[] { box });
            }
            catch (Exception e) { Log.LogWarning($"[oracle-lobby] ClaimOnlineBox: {e.Message}"); }
        }

        private static object GetPktHandler()
        {
            var prop = AccessTools.Property(_pktType, "Instance");
            if (RefOk(prop)) return prop.GetValue(null, null);
            return UnityEngine.Object.FindObjectOfType(_pktType);
        }

        // Set MultiplayerManager.mHasBeenInitializedFromServer = true. The packet
        // dispatcher (ReadMessageBuffer) gates every non-init packet on this flag
        // via mNetworkHandler.HasBeenInitializedFromServer, and the joiner path
        // (OnSocketServerJoined) never sets it. Without this, MapChange is dropped.
        private static float _lastForceDiagAt = -1f;
        // Cached: this runs every frame AND on every received packet (60 Hz
        // snapshots alone), and FindObjectOfType scans the whole scene each call.
        // Unity's != null also catches a destroyed (scene-reloaded) instance.
        private static UnityEngine.Object _mmCached;
        private static System.Reflection.FieldInfo _initFromServerF;
        internal static void ForceInitializedFromServer(object mm)
        {
            try
            {
                // _mmType is normally set by the connect-mode install, but that
                // only runs when launched with -address. Server-browser joins
                // skip it, so resolve the type here too.
                if (!RefOk(_mmType)) _mmType = CachedType("MultiplayerManager");
                if (!RefOk(_mmType)) return;
                if (!RefOk(mm))
                {
                    if (_mmCached == null) _mmCached = UnityEngine.Object.FindObjectOfType(_mmType);
                    mm = _mmCached;
                }
                if (!RefOk(mm))
                {
                    if (Time.realtimeSinceStartup - _lastForceDiagAt > 3f)
                    {
                        _lastForceDiagAt = Time.realtimeSinceStartup;
                        Log.LogWarning("[oracle-diag] ForceInitializedFromServer: MultiplayerManager instance NOT found.");
                    }
                    return;
                }
                if (!RefOk(_initFromServerF)) _initFromServerF = AccessTools.Field(_mmType, "mHasBeenInitializedFromServer");
                var f = _initFromServerF;
                if (!RefOk(f))
                {
                    if (Time.realtimeSinceStartup - _lastForceDiagAt > 3f)
                    {
                        _lastForceDiagAt = Time.realtimeSinceStartup;
                        Log.LogWarning("[oracle-diag] ForceInitializedFromServer: field mHasBeenInitializedFromServer NOT found.");
                    }
                    return;
                }
                if (!(bool)f.GetValue(mm))
                {
                    f.SetValue(mm, true);
                    Log.LogInfo("[oracle-lobby] Forced mHasBeenInitializedFromServer=true (unblocks MapChange/MapInfo).");
                }
            }
            catch (Exception e) { Log.LogWarning($"[oracle-lobby] ForceInitializedFromServer: {e.Message}"); }
        }

        internal static void P2PPackageHandler_Init_OraclePostfix(object __instance)
        {
            if (!_oracleConnectMode || !RefOk(__instance)) return;
            _oracleConnectStarted = true;
            try
            {
                var host = OracleServerHost;
                int port = OracleServerPort;
                // The patched assembly's OWN Init() already reads -address/-port and
                // calls UDPClient() once (connecting + starting the receive loop). So
                // ONLY rebind here when -address is ABSENT (the assembly couldn't
                // self-configure). Calling UDPClient a SECOND time with -address
                // present double-Connects the socket + starts a second receive loop,
                // which BREAKS the assembly's UDP entirely (handshake stops sending).
                bool hasCliAddr = false;
                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i] == "-address") { hasCliAddr = true; break; }
                if (!hasCliAddr && host != "127.0.0.1" && host != "localhost")
                {
                    var udpClient = AccessTools.Method(__instance.GetType(), "UDPClient");
                    if (RefOk(udpClient))
                    {
                        udpClient.Invoke(__instance, new object[] { host, port });
                        Log.LogInfo($"[oracle-lobby] Init rebound UDP client → {host}:{port}");
                    }
                }
            }
            catch (Exception e) { Log.LogWarning($"[oracle-lobby] Init postfix: {e.Message}"); }
            SendGameSocketBind(__instance, true);
            Log.LogInfo("[oracle-lobby] P2PPackageHandler.Init → oracle RX armed.");
        }

        // The router pins a socket to a lobby when it SELECTs, but the patched
        // game socket never did: it rode the per-IP fallback, i.e. whatever
        // lobby ANY client at this IP picked last. Two players behind one NAT in
        // different lobbies (one plays COMP, one watches REPLAY) got crossed, and
        // each re-SELECT tore the other's game flow down. So the game socket
        // sends a router BIND itself (op 0x06: endpoint binding only, no reply,
        // because a reply would land in the patched DLL's receive queue). Sent
        // after Init and every 10 s, before the handshake's first packet.
        private static System.Reflection.FieldInfo _gameSocketF;
        private static float _nextGameBindAt;
        internal static void SendGameSocketBind(object pktHandler, bool force)
        {
            try
            {
                if (!force && Time.realtimeSinceStartup < _nextGameBindAt) return;
                _nextGameBindAt = Time.realtimeSinceStartup + 10f;
                string code = SelectedLobbyCode ?? "";
                if (code.Length == 0 || code.Length > 16 || !RefOk(pktHandler)) return;
                if (!RefOk(_gameSocketF)) _gameSocketF = AccessTools.Field(pktHandler.GetType(), "_socket");
                var sock = RefOk(_gameSocketF) ? _gameSocketF.GetValue(pktHandler) as System.Net.Sockets.Socket : null;
                if (sock == null || !sock.Connected) return;
                byte[] codeBytes = System.Text.Encoding.ASCII.GetBytes(code);
                byte[] pkt = new byte[8 + 1 + 1 + codeBytes.Length + 4];
                pkt[0] = 0x53; pkt[1] = 0x46; pkt[2] = 0x52; pkt[3] = 0x54; pkt[4] = 0x52;   // "SFRTR"
                pkt[5] = 0x00; pkt[6] = 0x00; pkt[7] = 0x01;
                pkt[8] = 0x06;                                   // op = BIND (silent)
                pkt[9] = (byte)codeBytes.Length;
                Buffer.BlockCopy(codeBytes, 0, pkt, 10, codeBytes.Length);
                sock.Send(pkt);
                if (force) Log.LogInfo($"[oracle-lobby] game socket BIND lobby={code} ({sock.LocalEndPoint})");
            }
            catch (Exception e) { Log.LogWarning($"[oracle-lobby] game socket BIND: {e.Message}"); }
        }

        // Session token (server msgType 50, after ClientInit). Every player
        // reaches the server through the router, so the server can't tell
        // players apart by address: the token is how it knows us. Repeated from
        // the game socket every 2 s (msgType 51): if our address changes mid-
        // match (the router restarted, or a home router changed our port), the
        // server moves our session to the new address instead of losing us.
        // Also sent with msgType 48 from the snapshot socket, which binds that
        // socket to our slot so no one else can claim it.
        private const byte PktSessionToken = 50;
        private const byte PktGameHello = 51;
        internal static uint _sessionToken;
        private static float _nextGameHelloAt;
        internal static void SendGameHello(object pktHandler)
        {
            try
            {
                if (_sessionToken == 0 || Time.realtimeSinceStartup < _nextGameHelloAt || !RefOk(pktHandler)) return;
                _nextGameHelloAt = Time.realtimeSinceStartup + 2f;
                // Left the lobby (pause-menu leave, kick, lobby switch): the
                // token is spent, and must not trigger a rejoin.
                if (!RefOk(_mmType)) _mmType = CachedType("MultiplayerManager");
                var mm = RefOk(_mmType) ? UnityEngine.Object.FindObjectOfType(_mmType) : null;
                var accF = RefOk(_mmType) ? AccessTools.Field(_mmType, "mHasBeenAcceptedFromServer") : null;
                if (!RefOk(mm) || !RefOk(accF) || !(bool)accF.GetValue(mm)) { _sessionToken = 0; return; }
                ulong sid = OwnSteamId();
                if (sid == 0) return;
                if (!RefOk(_gameSocketF)) _gameSocketF = AccessTools.Field(pktHandler.GetType(), "_socket");
                var sock = RefOk(_gameSocketF) ? _gameSocketF.GetValue(pktHandler) as System.Net.Sockets.Socket : null;
                if (sock == null || !sock.Connected) return;
                // v25 framing: [u32 ts][type][body][u64 steamId][channel]
                byte[] pkt = new byte[5 + 12 + 9];
                uint ts = (uint)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
                for (int i = 0; i < 4; i++) pkt[i] = (byte)(ts >> (8 * i));
                pkt[4] = PktGameHello;
                for (int i = 0; i < 8; i++) { pkt[5 + i] = (byte)(sid >> (8 * i)); pkt[17 + i] = pkt[5 + i]; }
                for (int i = 0; i < 4; i++) pkt[13 + i] = (byte)(_sessionToken >> (8 * i));
                pkt[25] = 0;
                sock.Send(pkt);
            }
            catch { }
        }

        // msgType 52: the lobby doesn't know our token: it restarted under us
        // (a server update, the watchdog, RST) or dropped us.
        //  - In the lobby room: join the same lobby again in place, the way a
        //    lobby switch does (leave, then the normal join, whose ClientInit
        //    respawns our players).
        //  - On a map: the game can only join from its lobby, and every way
        //    back there reloads the scene and loses the local players
        //    (GameManager.RestartGame drops to "press every button to join";
        //    joining with the old map still loaded made the next MapChange
        //    unload the game's own scene). A fresh start joins fine, so the
        //    launcher restarts the game into this lobby: we leave a note
        //    (sf-online/rejoin.txt) and quit, and the launcher (launch.ps1 /
        //    sf-wrap.sh, which set SF_RELAUNCH=1) starts it again with
        //    SF_LOBBY=<code>. Started some other way: just say so.
        private static void OnSessionUnknown()
        {
            if (_sessionToken == 0 || string.IsNullOrEmpty(SelectedLobbyCode)) return;
            _sessionToken = 0;
            string code = SelectedLobbyCode;
            var bi = Instance;
            if (ClientInLobbyMap())
            {
                _rejoinCode = code;
                _rejoinAt = Time.realtimeSinceStartup + 0.5f;
                _rejoinTries = 0;
                Log.LogInfo($"[oracle-lobby] lobby {code} lost our session (it restarted?): rejoining.");
                if (RefOk(bi)) { bi._bannerText = "The lobby restarted. Rejoining..."; bi._bannerUntilUtc = DateTime.UtcNow.AddSeconds(5); }
                return;
            }
            bool relauncher = Environment.GetEnvironmentVariable("SF_RELAUNCH") == "1";
            if (relauncher)
            {
                try
                {
                    File.WriteAllText(Path.Combine(Path.Combine(BepInEx.Paths.GameRootPath, "sf-online"), "rejoin.txt"), code + "\n");
                    _quitAt = Time.realtimeSinceStartup + 2.5f;
                    Log.LogInfo($"[oracle-lobby] lobby {code} lost our session mid-match: restarting the game into it.");
                    if (RefOk(bi)) { bi._bannerText = "The lobby restarted. Restarting Stick Fight to rejoin..."; bi._bannerUntilUtc = DateTime.UtcNow.AddSeconds(5); }
                    return;
                }
                catch (Exception e) { Log.LogWarning($"[oracle-lobby] rejoin note: {e.Message}"); }
            }
            Log.LogInfo($"[oracle-lobby] lobby {code} lost our session mid-match (no launcher to restart us).");
            if (RefOk(bi)) { bi._bannerText = "The lobby restarted. Restart Stick Fight Online to rejoin."; bi._bannerUntilUtc = DateTime.UtcNow.AddSeconds(30); }
        }

        private const byte PktSessionUnknown = 52;
        private static string _rejoinCode;
        private static float _rejoinAt = -1f;
        private static int _rejoinTries;
        private static float _quitAt = -1f;

        private static object GameManagerInstance()
        {
            if (!RefOk(_gmType)) _gmType = CachedType("GameManager");
            if (!RefOk(_gmType)) return null;
            return AccessTools.Property(_gmType, "Instance")?.GetValue(null, null) ?? UnityEngine.Object.FindObjectOfType(_gmType);
        }

        // The lobby map is loaded and nothing is loading.
        private static bool ClientInLobbyMap()
        {
            try
            {
                var gm = GameManagerInstance();
                if (!RefOk(gm)) return true;
                var loadingF = AccessTools.Field(_gmType, "isLoading");
                if (RefOk(loadingF) && (bool)loadingF.GetValue(gm)) return false;
                var inLobby = AccessTools.Method(_gmType, "IsInLobby");
                return !RefOk(inLobby) || (bool)inLobby.Invoke(gm, null);
            }
            catch { return true; }
        }

        // Every frame: rejoin once the lobby is back up (its session tokens are
        // new, so a 52 only comes from a lobby that is answering again).
        internal static void TickRejoin()
        {
            if (_quitAt > 0f && Time.realtimeSinceStartup >= _quitAt)
            {
                _quitAt = -1f;
                // Application.Quit() hung here (stuck in Unity's shutdown, mid-
                // match); the launcher only needs the process gone.
                try { PlayerPrefs.Save(); } catch { }
                Log.LogInfo("[oracle-lobby] closing the game for the restart.");
                try { System.Diagnostics.Process.GetCurrentProcess().Kill(); } catch { Application.Quit(); }
                return;
            }
            if (_rejoinCode == null || Time.realtimeSinceStartup < _rejoinAt) return;
            if (!RefOk(GetPktHandler()) || !RefOk(UnityEngine.Object.FindObjectOfType(RefOk(_mmType) ? _mmType : CachedType("MultiplayerManager"))))
            {
                _rejoinAt = Time.realtimeSinceStartup + 0.5f;
                if (++_rejoinTries > 40) { Log.LogWarning("[oracle-lobby] rejoin gave up (no network manager)."); _rejoinCode = null; }
                return;
            }
            string code = _rejoinCode;
            _rejoinCode = null;
            Log.LogInfo($"[oracle-lobby] rejoining {code}.");
            JoinLobbyNow(code);
        }

        internal static void MultiplayerManager_RequestClientInit_OraclePostfix(object __instance)
        {
            if (!_oracleConnectMode) return;
            _oracleConnectStarted = true;
            Log.LogInfo("[oracle-lobby] RequestClientInit → handshake started (ClientRequestingAccepting).");
        }

        internal static void P2PPackageHandler_Update_OraclePostfix(object __instance)
        {
            if (!_oracleConnectMode || !RefOk(__instance)) return;
            try
            {
                var t = Traverse.Create(__instance);
                if (t.Field<bool>("mPauseTraffic").Value) return;
                // Do NOT require mHasHandler — during OnlineBox / scene load it can
                // be false while UDP replies (ClientAccepted) are already queued.
                bool sentOrRx = t.Field<bool>("mHasSentOrReceived").Value;
                SendGameSocketBind(__instance, false);
                SendGameHello(__instance);
                if (_oracleConnectStarted || sentOrRx)
                    ForceInitializedFromServer(null);
                t.Method("CheckForPackagesOnChannel", new object[] { 1, false }).GetValue();
                t.Method("CheckForPackagesOnChannel", new object[] { 0, false }).GetValue();
                // (C3) removed a no-op: SetValue("mHasSentOrReceived", true) only
                // ran when sentOrRx was already true (just read from that field).
            }
            catch (Exception e)
            {
                if (Time.realtimeSinceStartup - _lastUpdateDiagAt > 5f)
                {
                    _lastUpdateDiagAt = Time.realtimeSinceStartup;
                    Log.LogWarning($"[oracle-diag] Update postfix: {e.Message}");
                }
            }
        }

        private static float _lastUpdateDiagAt = -1f;

        internal static bool P2PPackageHandler_CheckMessageType_OraclePrefix(object __instance, byte[] data, object type, object steamIdRemote)
        {
            if (!_oracleConnectMode) return true;
            byte t = 255;
            if (type is byte b) t = b;
            else if (type != null && byte.TryParse(type.ToString(), out var parsed)) t = parsed;
            if (t == PktSessionToken)
            {
                if (data != null && data.Length >= 4)
                {
                    uint tok = BitConverter.ToUInt32(data, 0);
                    if (tok != _sessionToken) Log.LogInfo("[oracle-lobby] session token received.");
                    _sessionToken = tok;
                    _nextGameHelloAt = 0f;
                }
                return false;   // ours, not the game's
            }
            if (t == PktSessionUnknown) { OnSessionUnknown(); return false; }
            if (t == 38) _sessionToken = 0;   // KickPlayer: never auto-rejoin after a kick
            // DIAG: surface map/start traffic + the init-flag state at receive time
            // so we can tell whether MapChange(18)/StartMatch(35) actually reach
            // the game socket and whether the guard would drop them.
            if (t == 18 || t == 35 || t == 4 || t == 5)
            {
                bool initFlag = false;
                try
                {
                    if (!RefOk(_mmType)) _mmType = CachedType("MultiplayerManager");
                    var mm = UnityEngine.Object.FindObjectOfType(_mmType);
                    var f = AccessTools.Field(_mmType, "mHasBeenInitializedFromServer");
                    if (RefOk(mm) && RefOk(f)) initFlag = (bool)f.GetValue(mm);
                }
                catch { }
                Log.LogInfo($"[oracle-diag] CheckMessageType got t={t} initFromServer={initFlag}");
            }
            return true;
        }

        // Runs before the HasBeenInitializedFromServer guard. Logs every inbound
        // msgType (so we can see whether MapChange(18) actually reaches the game
        // socket) and force-clears the guard so no server packet is dropped.
        internal static void P2PPackageHandler_ReadMessageBuffer_OraclePrefix(byte[] rawData)
        {
            if (!_oracleConnectMode) return;
            try
            {
                if (rawData != null && rawData.Length >= 5)
                {
                    byte mt = rawData[4];
                    if (VerboseDiag && (mt == 4 || mt == 5 || mt == 18 || mt == 35))
                        Log.LogInfo($"[oracle-diag] ReadMessageBuffer inbound msgType={mt} len={rawData.Length}");
                }
                if (_oracleConnectStarted)
                    ForceInitializedFromServer(null);
            }
            catch { }
        }

        internal static bool CheckReadyPlayers_OraclePrefix() => !_oracleConnectMode;

        // Each client's map load ends with StartCountdownAfterSeconds(3): if the
        // round hasn't started 3 s after it got ready, it starts the countdown
        // itself. The server sends StartMatch once BOTH players are ready, so when
        // one loads slower the other counted down on its own and then again on
        // StartMatch: two countdowns, and the two players started at different
        // times. Online, that wait is 30 s: the server's StartMatch starts every
        // round (the coroutine stops itself once the fight is on) and the local
        // countdown is only a fallback. Patched on the coroutine's MoveNext (the
        // tiny wrapper methods can be inlined; GameManager itself must not be
        // patched on this runtime, see above).
        private const float OnlineAutoStartSec = 30f;
        private static System.Reflection.FieldInfo _acdStateF, _acdSecondsF;
        private static void PatchAutoStartCountdown(Harmony harmony)
        {
            try
            {
                if (!RefOk(_gmType)) _gmType = CachedType("GameManager");
                if (!RefOk(_gmType)) return;
                Type it = null;
                foreach (var nt in _gmType.GetNestedTypes(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                    if (nt.Name.Contains("StartCountdownAfterSeconds")) { it = nt; break; }
                if ((object)it == null) { Log.LogWarning("[oracle-lobby] auto-start countdown coroutine not found."); return; }
                foreach (var f in it.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                {
                    if (f.Name.Contains("state")) _acdStateF = f;
                    else if (f.FieldType == typeof(float) && f.Name.Contains("seconds")) _acdSecondsF = f;
                }
                var mn = AccessTools.Method(it, "MoveNext");
                if (!RefOk(mn) || !RefOk(_acdStateF) || !RefOk(_acdSecondsF)) { Log.LogWarning("[oracle-lobby] auto-start countdown: fields not found."); return; }
                harmony.Patch(mn, prefix: new HarmonyMethod(typeof(Plugin), nameof(AutoStartCountdown_MoveNextPrefix)));
                Log.LogInfo("[oracle-lobby] The game's own 3 s auto-start waits " + OnlineAutoStartSec + " s online: the server's StartMatch starts the round.");
            }
            catch (Exception e) { Log.LogWarning("[oracle-lobby] auto-start countdown patch: " + e.Message); }
        }

        internal static void AutoStartCountdown_MoveNextPrefix(object __instance)
        {
            try
            {
                if (!_oracleConnectMode || (int)_acdStateF.GetValue(__instance) != 0) return;
                if ((float)_acdSecondsF.GetValue(__instance) < OnlineAutoStartSec) _acdSecondsF.SetValue(__instance, OnlineAutoStartSec);
            }
            catch { }
        }

        internal static void OnMatchStart_OraclePostfix()
        {
            if (!_oracleConnectMode) return;
            try
            {
                if (RefOk(_gmType))
                {
                    var stillMenuF = AccessTools.Field(_gmType, "stillInMenu");
                    var inst = AccessTools.Property(_gmType, "Instance")?.GetValue(null, null)
                        ?? UnityEngine.Object.FindObjectOfType(_gmType);
                    if (RefOk(inst) && RefOk(stillMenuF))
                        stillMenuF.SetValue(inst, false);
                }
                Log.LogInfo("[oracle-lobby] OnMatchStart (countdown deferred until map load if needed).");
            }
            catch (Exception e) { Log.LogWarning($"[oracle-lobby] OnMatchStart postfix: {e.Message}"); }
        }

        internal static bool _countDownDeferred;
        // Re-entry guard: when the deferred coroutine re-invokes StartCountDown,
        // vanilla SF flips isLoading=true again while it loads pumpkin/boss
        // assets. Without this flag the prefix re-defers → coroutine re-invokes
        // → infinite loop (visible in client log as alternating "after map
        // load" / "deferred — map still loading" forever).
        internal static bool _countDownBypassPrefix;

        /// <summary>Halloween/boss: StartCountDown while isLoading skips pumpkin spawn — wait for load.</summary>
        internal static bool GameManager_StartCountDown_OraclePrefix(object __instance)
        {
            if (!_oracleConnectMode || !RefOk(__instance)) return true;
            if (_countDownBypassPrefix) return true;  // re-entry from coroutine — let original run
            try
            {
                var loadingF = AccessTools.Field(__instance.GetType(), "isLoading");
                if (!RefOk(loadingF) || !(bool)loadingF.GetValue(__instance)) return true;
                if (_countDownDeferred) return false;
                if (Instance != null)
                {
                    _countDownDeferred = true;
                    Instance.StartCoroutine(DeferredStartCountDownWhenLoaded(__instance));
                    Log.LogInfo("[oracle-lobby] StartCountDown deferred — map still loading (boss/Halloween).");
                }
                return false;
            }
            catch { return true; }
        }

        private static IEnumerator DeferredStartCountDownWhenLoaded(object gmInst)
        {
            float deadline = Time.realtimeSinceStartup + 20f;
            var loadingF = RefOk(_gmType) ? AccessTools.Field(_gmType, "isLoading") : null;
            while (Time.realtimeSinceStartup < deadline)
            {
                bool loading = RefOk(loadingF) && RefOk(gmInst) && (bool)loadingF.GetValue(gmInst);
                if (!loading) break;
                yield return null;
            }
            _countDownDeferred = false;
            try
            {
                var m = AccessTools.Method(_gmType, "StartCountDown");
                if (RefOk(m) && RefOk(gmInst))
                {
                    _countDownBypassPrefix = true;
                    try { m.Invoke(gmInst, null); }
                    finally { _countDownBypassPrefix = false; }
                    Log.LogInfo("[oracle-lobby] StartCountDown after map load (pumpkin/boss hooks).");
                }
            }
            catch (Exception e)
            {
                var real = e.InnerException ?? e;
                Log.LogWarning($"[oracle-diag] deferred StartCountDown threw: {real.GetType().Name}: {real.Message}\n{real.StackTrace}");
            }
        }

        internal static bool GameManager_StartMatch_OraclePrefix(object __instance)
        {
            if (!_oracleConnectMode || !RefOk(__instance)) return true;
            try
            {
                var inLobby = Traverse.Create(__instance).Method("IsInLobby").GetValue<bool>();
                if (inLobby) return false;
            }
            catch { }
            return true;
        }
    }
}
